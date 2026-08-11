namespace InnoUnpack.NET.Compression;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
		if (n > 0) {
			Xor(buffer.AsSpan(offset, n));
		}

		return n;
	}

	public override int Read(Span<byte> buffer) {
		var n = _inner.Read(buffer);
		if (n > 0) {
			Xor(buffer[..n]);
		}

		return n;
	}

	/// <summary>RC4 密钥流异或：ref 风格消除索引边界检查与元组交换临时量。</summary>
	private void Xor(Span<byte> buffer) {
		ref var buf = ref MemoryMarshal.GetReference(buffer);
		ref var state = ref MemoryMarshal.GetArrayDataReference(_state);
		var i = _i;
		var j = _j;
		for (var k = 0; k < buffer.Length; k++) {
			i = i + 1 & 0xFF;
			j = j + Unsafe.Add(ref state, i) & 0xFF;
			var t = Unsafe.Add(ref state, i);
			Unsafe.Add(ref state, i) = Unsafe.Add(ref state, j);
			Unsafe.Add(ref state, j) = t;
			Unsafe.Add(ref buf, k) ^= Unsafe.Add(ref state, Unsafe.Add(ref state, i) + Unsafe.Add(ref state, j) & 0xFF);
		}

		_i = i;
		_j = j;
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
}