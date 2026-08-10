namespace InnoUnpack.NET.Compression;

/// <summary>
///     反转 Inno Setup 的"调用指令优化"（Call Instruction Optimizer，4.1.8+ 默认启用）：
///     将存储的 x86 CALL/JMP 指令地址还原为相对偏移。
///     4108 逻辑参考 InnoUnpacker（MIT License）的 CallOptimizer.pas 解码分支；
///     5200/5309 变体依据 Inno Setup 5.2.0+ 的格式行为独立实现。
/// </summary>
sealed class ExeFilterStream : Stream {

	private const int InstructionSize = 5; // E8/E9 + 4 字节地址
	private const int OptimizationBlockSize = 0x10000;
	private readonly byte[] _addressBuffer = new byte[4];
	private readonly bool _flipHighByte;

	private readonly Stream _inner;
	private readonly byte[] _inputBuffer = new byte[4096];
	private readonly bool _isLegacy;
	private int _addressBytesPending; // >0: 待输出；<0: 正在读取地址字节
	private int _addressBytesRemaining;

	// Blocked（5.2.0+）状态
	private long _bytesRead;
	private uint _decodedAddress;

	// Legacy（4.1.8 – 5.1.x）状态
	private long _fileOffset = InstructionSize; // 当前字节偏移 + 指令大小（可回绕）
	private int _inputBufferLen;
	private int _inputBufferPos;

	private ExeFilterStream(Stream inner, bool isLegacy, bool flipHighByte) {
		_inner = inner;
		_isLegacy = isLegacy;
		_flipHighByte = flipHighByte;
	}

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	/// <summary>创建指令过滤器流（读取时还原优化）。</summary>
	public static Stream Create(Stream inner, Mode mode) {
		return mode switch {
			Mode.Legacy => new ExeFilterStream(inner, true, false),
			Mode.Blocked => new ExeFilterStream(inner, false, false),
			Mode.BlockedFlip => new ExeFilterStream(inner, false, true),
			_ => throw new ArgumentOutOfRangeException(nameof(mode))
		};
	}

	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count) {
		ArgumentNullException.ThrowIfNull(buffer);
		return Read(buffer.AsSpan(offset, count));
	}

	public override int Read(Span<byte> buffer) { return _isLegacy ? ReadLegacy(buffer) : ReadBlocked(buffer); }

	/// <summary>Legacy：逐字节还原，地址 = 存储值 - (指令偏移 + 5)。</summary>
	private int ReadLegacy(Span<byte> output) {
		var written = 0;
		while (written < output.Length) {
			if (_inputBufferPos == _inputBufferLen) {
				var n = _inner.Read(_inputBuffer);
				if (n <= 0) {
					break;
				}

				_inputBufferPos = 0;
				_inputBufferLen = n;
			}

			var value = _inputBuffer[_inputBufferPos++];
			if (_addressBytesRemaining == 0) {
				if (value is 0xE8 or 0xE9) {
					_decodedAddress = unchecked((uint)-_fileOffset);
					_addressBytesRemaining = 4;
				}
			} else {
				_decodedAddress += value;
				value = (byte)_decodedAddress;
				_decodedAddress >>= 8;
				_addressBytesRemaining--;
			}

			output[written++] = value;
			_fileOffset++;
		}

		return written;
	}

	/// <summary>Blocked：仅还原高字节为 0x00/0xFF 的地址，不跨 64KB 块。</summary>
	private int ReadBlocked(Span<byte> output) {
		var written = 0;

		// 先输出已还原的地址字节
		if (_addressBytesPending > 0) {
			var n = Math.Min(_addressBytesPending, output.Length);
			_addressBuffer.AsSpan(0, n).CopyTo(output);
			ShiftAddressBuffer(n);
			output = output[n..];
			written += n;
		}

		while (!output.IsEmpty) {
			if (_addressBytesPending == 0) {
				var b = _inner.ReadByte();
				if (b < 0) {
					break;
				}

				output[0] = (byte)b;
				output = output[1..];
				written++;
				_bytesRead++;

				if (b is not (0xE8 or 0xE9)) {
					continue;
				}

				// 指令跨越块边界时不优化
				var positionInBlock = (_bytesRead - 1) % OptimizationBlockSize;
				if (OptimizationBlockSize - positionInBlock < InstructionSize) {
					continue;
				}

				_addressBytesPending = -4;
			}

			// 读取 4 个地址字节（低字节在前）
			var toRead = -_addressBytesPending;
			var got = _inner.Read(_addressBuffer.AsSpan(4 + _addressBytesPending, toRead));
			if (got <= 0) {
				var remaining = 4 + _addressBytesPending;
				if (remaining > 0) {
					var n = Math.Min(remaining, output.Length);
					_addressBuffer.AsSpan(0, n).CopyTo(output);
					ShiftAddressBuffer(n);
					written += n;
				}

				_addressBytesPending = 0;
				break;
			}

			_addressBytesPending += got;
			_bytesRead += got;
			if (_addressBytesPending != 0) {
				break; // 地址尚未读满
			}

			// 高字节为 0x00/0xFF 时，把 24 位"绝对"地址还原为相对偏移（下一条指令 = 指令偏移 + 5）
			if (_addressBuffer[3] is 0x00 or 0xFF) {
				var absolute = _addressBuffer[0]
					| (uint)_addressBuffer[1] << 8
					| (uint)_addressBuffer[2] << 16;
				var relative = absolute - (uint)(_bytesRead & 0xFFFFFF); // 允许 24 位回绕
				_addressBuffer[0] = (byte)relative;
				_addressBuffer[1] = (byte)(relative >> 8);
				_addressBuffer[2] = (byte)(relative >> 16);
				if (_flipHighByte && (relative & 0x800000) != 0) {
					_addressBuffer[3] = (byte)~_addressBuffer[3];
				}
			}

			_addressBytesPending = 4;
			var outN = Math.Min(4, output.Length);
			_addressBuffer.AsSpan(0, outN).CopyTo(output);
			ShiftAddressBuffer(outN);
			output = output[outN..];
			written += outN;
		}

		return written;
	}

	/// <summary>将地址缓冲中已输出的前缀移除（memmove 语义）。</summary>
	private void ShiftAddressBuffer(int consumed) {
		if (consumed <= 0) {
			return;
		}

		var remaining = _addressBytesPending - consumed;
		if (remaining > 0) {
			for (var i = 0; i < remaining; i++) {
				_addressBuffer[i] = _addressBuffer[consumed + i];
			}
		}

		_addressBytesPending = remaining;
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

	override protected void Dispose(bool disposing) {
		if (disposing) {
			_inner.Dispose();
		}

		base.Dispose(disposing);
	}

	/// <summary>解码模式。</summary>
	internal enum Mode {
		/// <summary>Inno Setup 5.2.0 之前：任何 E8/E9 都还原（等价 CallOptimizer.pas 解码分支）。</summary>
		Legacy,

		/// <summary>Inno Setup 5.2.0 – 5.3.8：仅还原高字节为 0x00/0xFF 的地址，且不跨 64KB 块。</summary>
		Blocked,

		/// <summary>Inno Setup 5.3.9+：同 Blocked，另对负相对偏移的高字节取反。</summary>
		BlockedFlip
	}
}