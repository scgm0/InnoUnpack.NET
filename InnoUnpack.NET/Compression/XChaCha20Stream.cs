namespace InnoUnpack.NET.Compression;

/// <summary>
///     XChaCha20 流解密器（Inno Setup 6.4.0+ 的加密 chunk）。
///     基于 RFC 8439 ChaCha20 块函数与 XChaCha20 草案的 HChaCha20 子密钥派生；
///     8 字节 nonce 布局（state[13]=0、state[14..15]=nonce）为 Inno Setup 格式事实
///     （参考 InnoUnpacker，MIT License，的 ChaCha20.pas）。
///     counter 从 0 开始。
/// </summary>
sealed class XChaCha20Stream : Stream {
	private const int BlockSize = 64;

	private readonly Stream _inner;
	private readonly byte[] _keystream = new byte[BlockSize];
	private readonly uint[] _state = new uint[16];
	private readonly uint[] _working = new uint[16];
	private int _keystreamPos = BlockSize;

	public XChaCha20Stream(Stream inner, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce) {
		ArgumentNullException.ThrowIfNull(inner);
		if (key.Length != 32) {
			throw new ArgumentException("XChaCha20 密钥必须为 32 字节", nameof(key));
		}

		if (nonce.Length != 24) {
			throw new ArgumentException("XChaCha20 nonce 必须为 24 字节", nameof(nonce));
		}

		_inner = inner;

		Span<byte> subkey = stackalloc byte[32];
		HChaCha20(key, nonce[..16], subkey);

		// 标准常量
		_state[0] = 0x61707865; // "expa"
		_state[1] = 0x3320646E; // "nd 3"
		_state[2] = 0x79622D32; // "2-by"
		_state[3] = 0x6B206574; // "te k"
		for (var i = 0; i < 8; i++) {
			_state[4 + i] = ReadLe32(subkey, i * 4);
		}

		_state[12] = 0; // counter（从 0 开始）
		// Inno Setup 变体：ChaCha20 部分使用 8 字节 nonce，布局为 state[13]=0、state[14..15]=nonce[16..23]
		_state[13] = 0;
		_state[14] = ReadLe32(nonce, 16);
		_state[15] = ReadLe32(nonce, 20);
	}

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count) {
		var total = 0;
		while (count > 0) {
			if (_keystreamPos == BlockSize) {
				GenerateKeystreamBlock();
			}

			var n = Math.Min(count, BlockSize - _keystreamPos);
			var dataRead = _inner.Read(buffer, offset, n);
			if (dataRead <= 0) {
				break;
			}

			for (var i = 0; i < dataRead; i++) {
				buffer[offset + i] ^= _keystream[_keystreamPos + i];
			}

			_keystreamPos += dataRead;
			offset += dataRead;
			count -= dataRead;
			total += dataRead;
		}

		return total;
	}

	public override int Read(Span<byte> buffer) {
		var total = 0;
		while (!buffer.IsEmpty) {
			if (_keystreamPos == BlockSize) {
				GenerateKeystreamBlock();
			}

			var n = Math.Min(buffer.Length, BlockSize - _keystreamPos);
			var dataRead = _inner.Read(buffer[..n]);
			if (dataRead <= 0) {
				break;
			}

			for (var i = 0; i < dataRead; i++) {
				buffer[i] ^= _keystream[_keystreamPos + i];
			}

			_keystreamPos += dataRead;
			buffer = buffer[dataRead..];
			total += dataRead;
		}

		return total;
	}

	private void GenerateKeystreamBlock() {
		var working = _working;
		Array.Copy(_state, working, 16);
		for (var round = 0; round < 10; round++) {
			QuarterRound(ref working[0], ref working[4], ref working[8], ref working[12]);
			QuarterRound(ref working[1], ref working[5], ref working[9], ref working[13]);
			QuarterRound(ref working[2], ref working[6], ref working[10], ref working[14]);
			QuarterRound(ref working[3], ref working[7], ref working[11], ref working[15]);
			QuarterRound(ref working[0], ref working[5], ref working[10], ref working[15]);
			QuarterRound(ref working[1], ref working[6], ref working[11], ref working[12]);
			QuarterRound(ref working[2], ref working[7], ref working[8], ref working[13]);
			QuarterRound(ref working[3], ref working[4], ref working[9], ref working[14]);
		}

		for (var i = 0; i < 16; i++) {
			working[i] += _state[i];
		}

		for (var i = 0; i < 16; i++) {
			WriteLe32(_keystream, i * 4, working[i]);
		}

		_state[12]++; // 递增 counter
		_keystreamPos = 0;
	}

	static private void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d) {
		a += b;
		d ^= a;
		d = RotateLeft(d, 16);
		c += d;
		b ^= c;
		b = RotateLeft(b, 12);
		a += b;
		d ^= a;
		d = RotateLeft(d, 8);
		c += d;
		b ^= c;
		b = RotateLeft(b, 7);
	}

	static private uint RotateLeft(uint value, int bits) { return value << bits | value >> 32 - bits; }

	static private void HChaCha20(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce16, Span<byte> output) {
		var state = new uint[16];
		state[0] = 0x61707865;
		state[1] = 0x3320646E;
		state[2] = 0x79622D32;
		state[3] = 0x6B206574;
		for (var i = 0; i < 8; i++) {
			state[4 + i] = ReadLe32(key, i * 4);
		}

		for (var i = 0; i < 4; i++) {
			state[12 + i] = ReadLe32(nonce16, i * 4);
		}

		for (var round = 0; round < 10; round++) {
			QuarterRound(ref state[0], ref state[4], ref state[8], ref state[12]);
			QuarterRound(ref state[1], ref state[5], ref state[9], ref state[13]);
			QuarterRound(ref state[2], ref state[6], ref state[10], ref state[14]);
			QuarterRound(ref state[3], ref state[7], ref state[11], ref state[15]);
			QuarterRound(ref state[0], ref state[5], ref state[10], ref state[15]);
			QuarterRound(ref state[1], ref state[6], ref state[11], ref state[12]);
			QuarterRound(ref state[2], ref state[7], ref state[8], ref state[13]);
			QuarterRound(ref state[3], ref state[4], ref state[9], ref state[14]);
		}

		for (var i = 0; i < 4; i++) {
			WriteLe32(output, i * 4, state[0 + i]);
		}

		for (var i = 0; i < 4; i++) {
			WriteLe32(output, 16 + i * 4, state[12 + i]);
		}
	}

	static private uint ReadLe32(ReadOnlySpan<byte> data, int offset) {
		return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
	}

	static private void WriteLe32(Span<byte> output, int offset, uint value) {
		output[offset] = (byte)value;
		output[offset + 1] = (byte)(value >> 8);
		output[offset + 2] = (byte)(value >> 16);
		output[offset + 3] = (byte)(value >> 24);
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
}