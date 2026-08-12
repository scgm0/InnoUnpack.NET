/*
 * LZMA1 流式解码器（Stream 包装）。
 *
 * 内部核心为 Lzma1Decoder（移植自 LZMA SDK LzmaDec.c，Public Domain）。
 * 输入流以 5 字节属性头开始（lc/lp/pb + 字典大小），随后为 LZMA 码流。
 * 输入耗尽时停止解码（Inno Setup 的块数据即为此格式）。
 */


namespace InnoUnpack.NET.Compression;

using System.Buffers;

/// <summary>
///     流式 LZMA1 解码器（无结束标记、输出大小未知）。
///     输入流以 5 字节属性头开始（lc/lp/pb + 字典大小），随后为 LZMA 码流。
/// </summary>
sealed class Lzma1Stream : Stream {
	private const int PropsSize = 5;

	private const int ChunkSize = 256 * 1024;

	/// <summary>压缩区域预取上限（超过则回退流式读取）。</summary>
	private const int PrefetchMaxBytes = 96 * 1024 * 1024;

	private readonly bool _allowTruncated;
	private readonly Lzma1Decoder _decoder;

	private readonly Stream? _input;
	private bool _disposed;
	private bool _eof;
	private byte[] _pending = [];
	private bool _pendingRented;
	private int _pendingLen;
	private int _pendingPos;

	/// <summary>输入流必须以 5 字节属性头开始。</summary>
	/// <param name="input">压缩数据输入流。</param>
	/// <param name="allowTruncated">输入耗尽时用 0xFF 填充继续解码（头部块）或严格停止（数据块）。</param>
	/// <param name="prefetch">允许预取整个压缩区域（区域长度已知且不超过上限时）。</param>
	public Lzma1Stream(Stream input, bool allowTruncated = false, bool prefetch = true) {
		_allowTruncated = allowTruncated;
		if (prefetch && !allowTruncated && TryGetLength(input, out var regionLength) && regionLength <= PrefetchMaxBytes) {
			// 数据块：一次性预取到池化缓冲，消除逐块流读取与输入缓冲搬移
			var buf = ArrayPool<byte>.Shared.Rent((int)regionLength);
			try {
				Span<byte> props = stackalloc byte[PropsSize];
				var offset = 0;
				while (offset < props.Length) {
					var n = input.Read(props[offset..]);
					if (n <= 0) {
						throw new InnoFormatException("LZMA 数据意外结束（缺少属性头）");
					}

					offset += n;
				}

				var total = 0;
				// 区域长度按块头给出，实际数据可能略短（末尾含填充）：读到 EOF 为止，
				// 解码器按截断语义处理尾部缺口（与流模式一致）
				while (total < (int)regionLength - PropsSize) {
					var n = input.Read(buf, total, (int)regionLength - PropsSize - total);
					if (n <= 0) {
						break;
					}

					total += n;
				}

				input.Dispose();
				_decoder = new([.. props]);
				_decoder.SetInput(buf, 0, total);
				_memoryInput = buf;
				return;
			} catch {
				ArrayPool<byte>.Shared.Return(buf);
				throw;
			}
		}

		_input = input;
		var propsBytes = new byte[PropsSize];
		var pos = 0;
		while (pos < PropsSize) {
			var n = input.Read(propsBytes, pos, PropsSize - pos);
			if (n <= 0) {
				throw new InnoFormatException("LZMA 数据意外结束（缺少属性头）");
			}

			pos += n;
		}

		_decoder = new(propsBytes);
	}

	/// <summary>以内存中的完整块数据初始化（属性头 5 字节在区域起始处；接管输入数组，Dispose 时归还池）。</summary>
	internal Lzma1Stream(byte[] input, int offset, int length, bool allowTruncated = false) {
		_allowTruncated = allowTruncated;
		_decoder = new(input.AsSpan(offset, PropsSize).ToArray());
		_decoder.SetInput(input, offset + PropsSize, offset + length);
		_memoryInput = input;
	}

	/// <summary>安全获取流长度（不支持 Length 的流（如解密器）返回 false）。</summary>
	static private bool TryGetLength(Stream input, out long length) {
		try {
			length = input.Length;
			return length > 0;
		} catch (NotSupportedException) {
			length = 0;
			return false;
		}
	}

	/// <summary>内存模式输入（池化，Dispose 归还）。</summary>
	private byte[]? _memoryInput;

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

		// 直接解码路径：无待取缓冲且调用方缓冲 ≥ 一个解码块时，直接解码进调用方缓冲，
		// 省去 _pending 中转的整块复制（小缓冲如 SkipBytes 仍走 _pending 路径）。
		// 内存模式下输入已由 SetInput 提供（_input 为 null，Decode 不再读流）。
		if (_pendingPos == _pendingLen && !_eof && buffer.Length >= ChunkSize) {
			var n = _decoder.Decode(_input!, buffer, _allowTruncated);
			if (n > 0) {
				return n;
			}

			_eof = true;
			return 0;
		}

		while (_pendingPos == _pendingLen) {
			if (_eof) {
				return 0;
			}

			// 复用解码缓冲（ArrayPool 租用），避免每次 Decode 分配 256KB
			if (_pending.Length < ChunkSize) {
				if (_pendingRented) {
					ArrayPool<byte>.Shared.Return(_pending);
				}

				_pending = ArrayPool<byte>.Shared.Rent(ChunkSize);
				_pendingRented = true;
			}

			var n = _decoder.Decode(_input!, _pending, _allowTruncated);
			if (n <= 0) {
				_eof = true;
				return 0;
			}

			_pendingLen = n;
			_pendingPos = 0;
		}

		var count = Math.Min(_pendingLen - _pendingPos, buffer.Length);
		_pending.AsSpan(_pendingPos, count).CopyTo(buffer);
		_pendingPos += count;
		if (_pendingPos == _pendingLen) {
			_pendingPos = 0;
			_pendingLen = 0;
		}

		return count;
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

	override protected void Dispose(bool disposing) {
		if (!_disposed) {
			_disposed = true;
			if (disposing) {
				_input?.Dispose();
				_decoder.Dispose();
				if (_pendingRented) {
					ArrayPool<byte>.Shared.Return(_pending);
					_pendingRented = false;
					_pending = [];
				}

				if (_memoryInput is not null) {
					ArrayPool<byte>.Shared.Return(_memoryInput);
					_memoryInput = null;
				}
			}
		}

		base.Dispose(disposing);
	}
}
