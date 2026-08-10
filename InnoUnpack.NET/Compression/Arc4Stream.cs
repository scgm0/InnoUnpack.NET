namespace InnoUnpack.NET.Compression;

/// <summary>
///     ARC4（RC4）流解密器，用于 Inno Setup 4.2.2 – 6.3.x 的加密 chunk。
///     标准 RC4 算法（Rivest 1987，公开算法），读取底层流并逐字节异或 ARC4 密钥流。
/// </summary>
sealed class Arc4Stream : Stream {
	private readonly Stream _inner;
	private readonly byte[] _state = new byte[256];
	private int _i;
	private int _j;

	public Arc4Stream(Stream inner, ReadOnlySpan<byte> key) {
		_inner = inner;
		for (var i = 0; i < 256; i++) {
			_state[i] = (byte)i;
		}

		var j = 0;
		for (var i = 0; i < 256; i++) {
			j = j + _state[i] + key[i % key.Length] & 0xFF;
			(_state[i], _state[j]) = (_state[j], _state[i]);
		}
	}

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count) {
		var n = _inner.Read(buffer, offset, count);
		for (var k = 0; k < n; k++) {
			_i = _i + 1 & 0xFF;
			_j = _j + _state[_i] & 0xFF;
			(_state[_i], _state[_j]) = (_state[_j], _state[_i]);
			buffer[offset + k] ^= _state[_state[_i] + _state[_j] & 0xFF];
		}

		return n;
	}

	public override int Read(Span<byte> buffer) {
		var n = _inner.Read(buffer);
		for (var k = 0; k < n; k++) {
			_i = _i + 1 & 0xFF;
			_j = _j + _state[_i] & 0xFF;
			(_state[_i], _state[_j]) = (_state[_j], _state[_i]);
			buffer[k] ^= _state[_state[_i] + _state[_j] & 0xFF];
		}

		return n;
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
}