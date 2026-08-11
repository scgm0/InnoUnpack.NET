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
	private readonly bool _allowTruncated;
	private readonly Lzma1Decoder _decoder;

	private readonly Stream _input;
	private bool _disposed;
	private bool _eof;
	private byte[] _pending = [];
	private bool _pendingRented;
	private int _pendingLen;
	private int _pendingPos;

	/// <summary>输入流必须以 5 字节属性头开始。</summary>
	/// <param name="input">压缩数据输入流。</param>
	/// <param name="allowTruncated">输入耗尽时用 0xFF 填充继续解码（头部块）或严格停止（数据块）。</param>
	public Lzma1Stream(Stream input, bool allowTruncated = false) {
		_input = input;
		_allowTruncated = allowTruncated;
		var props = new byte[PropsSize];
		var offset = 0;
		while (offset < props.Length) {
			var n = input.Read(props, offset, props.Length - offset);
			if (n <= 0) {
				throw new InnoFormatException("LZMA 数据意外结束（缺少属性头）");
			}

			offset += n;
		}

		_decoder = new(props);
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

			var n = _decoder.Decode(_input, _pending, _allowTruncated);
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
				_input.Dispose();
				_decoder.Dispose();
				if (_pendingRented) {
					ArrayPool<byte>.Shared.Return(_pending);
					_pendingRented = false;
					_pending = [];
				}
			}
		}

		base.Dispose(disposing);
	}
}