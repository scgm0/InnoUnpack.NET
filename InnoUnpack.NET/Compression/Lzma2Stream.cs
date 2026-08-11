/*
 * LZMA2 流式解码器（原始 LZMA2 流，Inno Setup 的块/chunk 即此格式）。
 *
 * 结构参考 xz 的 liblzma（lzma2_decoder.c）：
 * - 流以 1 字节属性（字典大小编码，由工厂读取并传入）开始；
 * - 随后为若干 chunk；
 * - 0x00：流结束；0x01：未压缩 chunk；0x80-0xFF：压缩 chunk。
 * 压缩 chunk 内嵌一段无结束标记的 LZMA1 码流（由 Lzma1Decoder 解码），
 * 码流尾部不足时以 0xFF 填充（与 innoextract 捆绑 liblzma 一致）。
 */


namespace InnoUnpack.NET.Compression;

using System.Buffers;

/// <summary>
///     原始 LZMA2 流解码器（Inno Setup 的块数据格式）。
/// </summary>
sealed class Lzma2Stream : Stream {
	private readonly uint _dictSize;
	private readonly Stream _input;
	private Lzma1Decoder? _decoder;
	private long _dictValid;
	private bool _disposed;
	private bool _eof;
	private byte _lastProp;
	private byte[] _pending = [];
	private bool _pendingRented;
	private int _pendingLen;
	private int _pendingPos;

	/// <summary>
	///     以 1 字节属性（字典大小编码，0-40）初始化。
	/// </summary>
	public Lzma2Stream(Stream input, byte prop) {
		_input = input;
		if (prop > 40) {
			throw new InnoFormatException($"无效的 LZMA2 属性 {prop}");
		}

		_dictSize = prop == 40 ? uint.MaxValue : (uint)(2 | prop & 1) << prop / 2 + 11;
	}

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count) { return Read(buffer.AsSpan(offset, count)); }

	public override int Read(Span<byte> buffer) {
		if (buffer.IsEmpty) {
			return 0;
		}

		var chunkCount = 0;
		while (_pendingPos == _pendingLen) {
			chunkCount++;
			if (_eof || !NextChunk()) {
				return 0;
			}

			if (_pendingLen == 0 && chunkCount > 100000) {
				throw new InnoFormatException("LZMA2 解码无进展（疑似损坏）");
			}
		}

		var count = Math.Min(_pendingLen - _pendingPos, buffer.Length);
		_pending.AsSpan(_pendingPos, count).CopyTo(buffer);
		_pendingPos += count;

		return count;
	}

	/// <summary>解码下一个 chunk 到 _pending。返回 false 表示流结束。</summary>
	private bool NextChunk() {
		var ctrl = _input.ReadByte();
		if (ctrl < 0) {
			_eof = true;
			return false;
		}

		if (ctrl == 0x00) {
			_eof = true; // 流结束标记
			return false;
		}

		if (ctrl is 0x01 or 0x02) {
			// 未压缩 chunk：2 字节大小（+1）后直接数据（无校验字段）
			var usize = ReadUInt16() + 1;
			if (_eof) {
				throw new InnoFormatException("LZMA2 数据意外结束");
			}

			EnsurePendingCapacity(usize);
			ReadExact(_pending.AsSpan(0, usize));
			_pendingLen = usize;
			_pendingPos = 0;
			_dictValid += usize;
			// 未压缩数据写入 LZMA 窗口（匹配可引用）
			_decoder ??= NewDecoder(0);
			_decoder.WriteUncompressed(_pending.AsSpan(0, usize));
			return true;
		}

		if ((ctrl & 0x80) == 0) {
			throw new InnoFormatException($"无效的 LZMA2 chunk 控制字节 0x{ctrl:X2}");
		}

		// 压缩 chunk（0x80-0xFF）：
		//   usize = (ctrl & 0x1F) << 16 | 2 字节（大端）+ 1
		//   csize = 2 字节（大端）+ 1
		//   [1 字节 lc/lp/pb，若 ctrl >= 0xC0]
		//   LZMA1 码流（csize 字节）
		var chunkUsize = (ctrl & 0x1F) << 16 | ReadUInt16();
		var csize = ReadUInt16() + 1;
		if (_eof) {
			throw new InnoFormatException("LZMA2 数据意外结束");
		}

		chunkUsize += 1;


		var newProps = ctrl >= 0xC0;
		var dictReset = ctrl >= 0xE0;
		var stateReset = ctrl >= 0xA0 && !newProps;

		if (newProps) {
			_lastProp = (byte)ReadByteChecked();
			if (_lastProp >= 9 * 5 * 5) {
				throw new InnoFormatException("无效的 LZMA2 lc/lp/pb 属性");
			}
		}

		if (dictReset || _decoder == null) {
			// 0xE0+：新 props + 全部重置（含字典）
			if (_decoder is null) {
				_decoder = NewDecoder(_lastProp);
			} else {
				// 复用字典与概率数组，避免每次字典重置重新分配数 MB 缓冲
				_decoder.ResetWithProps(_lastProp);
				_decoder.ResetDictionary();
			}

			_dictValid = 0;
		} else if (newProps) {
			// 0xC0-0xDF：新 props + 状态重置（字典保留）
			_decoder.ResetWithProps(_lastProp);
		} else if (stateReset) {
			// 0xA0-0xBF：仅状态重置（概率模型 + range coder）
			_decoder.ResetState();
		} else {
			// 0x80-0x9F：继续——range coder 重新初始化（每个 chunk 有 5 字节初始化头），概率模型延续
			_decoder.ResetRangeOnly();
		}

		_decoder.ResetInput();
		_decoder.ExternalCheckDicSize = (uint)Math.Min(_dictValid, uint.MaxValue);

		// 复用输出缓冲（ArrayPool 租用，chunk 大小不同时按需扩容）
		EnsurePendingCapacity(chunkUsize);

		LimitedReadStream limited = new(_input, csize);
		var n = _decoder.Decode(limited, _pending.AsSpan(0, chunkUsize), true);

		_pendingLen = n;
		_pendingPos = 0;
		_dictValid += n;
		return true;
	}

	/// <summary>用 1 字节 lc/lp/pb + 流属性字典大小构造 LZMA1 解码器。</summary>
	private Lzma1Decoder NewDecoder(byte prop) {
		var props = new byte[5];
		props[0] = prop;
		props[1] = (byte)_dictSize;
		props[2] = (byte)(_dictSize >> 8);
		props[3] = (byte)(_dictSize >> 16);
		props[4] = (byte)(_dictSize >> 24);
		return new(props);
	}

	private int ReadByteChecked() {
		var b = _input.ReadByte();
		if (b < 0) {
			_eof = true;
		}

		return b;
	}

	private int ReadUInt16() {
		var a = ReadByteChecked();
		var b = ReadByteChecked();
		return a << 8 | b;
	}

	private void ReadExact(Span<byte> buffer) {
		var offset = 0;
		while (offset < buffer.Length) {
			var n = _input.Read(buffer[offset..]);
			if (n <= 0) {
				throw new InnoFormatException("LZMA2 数据意外结束");
			}

			offset += n;
		}
	}

	/// <summary>确保输出缓冲容量（ArrayPool 租用并按需扩容，避免每 chunk 分配）。</summary>
	private void EnsurePendingCapacity(int required) {
		if (_pending.Length >= required) {
			return;
		}

		if (_pendingRented) {
			ArrayPool<byte>.Shared.Return(_pending);
		}

		_pending = ArrayPool<byte>.Shared.Rent(required);
		_pendingRented = true;
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

	override protected void Dispose(bool disposing) {
		if (!_disposed) {
			_disposed = true;
			if (disposing) {
				_input.Dispose();
				_decoder?.Dispose();
				if (_pendingRented) {
					ArrayPool<byte>.Shared.Return(_pending);
					_pendingRented = false;
					_pending = [];
				}
			}
		}

		base.Dispose(disposing);
	}

	/// <summary>限制读取范围（最多 size 字节）的输入包装。</summary>
	sealed private class LimitedReadStream(Stream input, int size) : Stream {
		private int _remaining = size;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => _remaining;
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }

		public override int Read(byte[] buffer, int offset, int count) {
			if (_remaining <= 0) {
				return 0;
			}

			var n = input.Read(buffer, offset, Math.Min(count, _remaining));
			_remaining -= n;
			return n;
		}

		public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

		public override void SetLength(long value) { throw new NotSupportedException(); }

		public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
	}
}