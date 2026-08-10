namespace InnoUnpack.NET.Compression;

/// <summary>
///     流式 bzip2 解码器（标准 bzip2 流，用于 Inno Setup 的 BZip2 压缩数据）。
///     内部按块解码（Huffman → BWT → RLE），输出为连续解压数据。
/// </summary>
sealed class BZip2Stream(Stream input) : Stream {
	private readonly BZip2Decoder _decoder = new(input);
	private bool _eof;
	private byte[] _pending = [];
	private int _pendingPos;

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

		while (_pendingPos == _pending.Length) {
			if (_eof) {
				return 0;
			}

			var block = _decoder.DecodeNextBlock();
			if (block is null) {
				_eof = true;
				return 0;
			}

			_pending = block;
			_pendingPos = 0;
		}

		var n = Math.Min(_pending.Length - _pendingPos, buffer.Length);
		_pending.AsSpan(_pendingPos, n).CopyTo(buffer);
		_pendingPos += n;
		if (_pendingPos != _pending.Length) {
			return n;
		}

		_pending = []; // 块已耗尽，下次 Read 解码下一块
		_pendingPos = 0;
		return n;
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

	override protected void Dispose(bool disposing) {
		if (disposing) {
			// 解码器不拥有输入流的所有权（工厂链的中间层），不释放
		}

		base.Dispose(disposing);
	}
}