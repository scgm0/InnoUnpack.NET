using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace InnoUnpack.NET.Compression;

/// <summary>
///     XChaCha20 流解密器（Inno Setup 6.4.0+ 的加密 chunk）。
///     基于 RFC 8439 ChaCha20 块函数与 XChaCha20 草案的 HChaCha20 子密钥派生；
///     8 字节 nonce 布局（state[13]=0、state[14..15]=nonce）为 Inno Setup 格式事实
///     （参考 InnoUnpacker（MIT License）的 ChaCha20.pas）。
///     counter 从 0 开始。
///     支持 SIMD 时一次并行生成 4 个块（Vector128&lt;uint&gt;，每 lane 承载一个块）的 keystream。
/// </summary>
sealed class XChaCha20Stream : Stream {
	private const int BlockSize = 64;
	private const int SimdBlocks = 4; // Vector128<uint> 的 lane 数
	private const int KeystreamSize = BlockSize * SimdBlocks;

	/// <summary>SIMD 可用性（构造期判定一次）。</summary>
	static private readonly bool UseSimd = Vector128.IsHardwareAccelerated;

	private readonly Stream _inner;
	private readonly byte[] _keystream = new byte[KeystreamSize];
	private readonly uint[] _state = new uint[16];
	private readonly uint[] _working = new uint[16];
	private int _keystreamPos = KeystreamSize;

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
			if (_keystreamPos == KeystreamSize) {
				GenerateKeystreamBlock();
			}

			var n = Math.Min(count, KeystreamSize - _keystreamPos);
			var dataRead = _inner.Read(buffer, offset, n);
			if (dataRead <= 0) {
				break;
			}

			XorWithKeystream(buffer.AsSpan(offset, dataRead), _keystreamPos);
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
			if (_keystreamPos == KeystreamSize) {
				GenerateKeystreamBlock();
			}

			var n = Math.Min(buffer.Length, KeystreamSize - _keystreamPos);
			var dataRead = _inner.Read(buffer[..n]);
			if (dataRead <= 0) {
				break;
			}

			XorWithKeystream(buffer[..dataRead], _keystreamPos);
			_keystreamPos += dataRead;
			buffer = buffer[dataRead..];
			total += dataRead;
		}

		return total;
	}

	/// <summary>生成 4 个块的 keystream（SIMD 可用时并行，否则标量 4 次）。</summary>
	private void GenerateKeystreamBlock() {
		if (UseSimd) {
			GenerateKeystreamBlocksSimd();
		} else {
			for (var b = 0; b < SimdBlocks; b++) {
				GenerateKeystreamBlockScalar(b);
			}
		}

		_keystreamPos = 0;
	}

	/// <summary>单块标量 keystream 生成（SIMD 回退路径）。</summary>
	private void GenerateKeystreamBlockScalar(int blockIndex) {
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

		var output = _keystream.AsSpan(blockIndex * BlockSize, BlockSize);
		for (var i = 0; i < 16; i++) {
			WriteLe32(output, i * 4, working[i]);
		}

		_state[12]++; // 递增 counter
	}

	/// <summary>
	///     4 块并行 keystream 生成：16 个 Vector128&lt;uint&gt;，每 lane 承载一个块的字，
	///     列/对角线 QuarterRound 全部 lane 级并行，一次产出 256 字节。
	/// </summary>
	private void GenerateKeystreamBlocksSimd() {
		var v0 = Vector128.Create(_state[0]);
		var v1 = Vector128.Create(_state[1]);
		var v2 = Vector128.Create(_state[2]);
		var v3 = Vector128.Create(_state[3]);
		var v4 = Vector128.Create(_state[4]);
		var v5 = Vector128.Create(_state[5]);
		var v6 = Vector128.Create(_state[6]);
		var v7 = Vector128.Create(_state[7]);
		var v8 = Vector128.Create(_state[8]);
		var v9 = Vector128.Create(_state[9]);
		var v10 = Vector128.Create(_state[10]);
		var v11 = Vector128.Create(_state[11]);
		var v12 = Vector128.Create(_state[12]);
		var v13 = Vector128.Create(_state[13]);
		var v14 = Vector128.Create(_state[14]);
		var v15 = Vector128.Create(_state[15]);

		for (var round = 0; round < 10; round++) {
			// 列
			QuarterRound(ref v0, ref v4, ref v8, ref v12);
			QuarterRound(ref v1, ref v5, ref v9, ref v13);
			QuarterRound(ref v2, ref v6, ref v10, ref v14);
			QuarterRound(ref v3, ref v7, ref v11, ref v15);
			// 对角线
			QuarterRound(ref v0, ref v5, ref v10, ref v15);
			QuarterRound(ref v1, ref v6, ref v11, ref v12);
			QuarterRound(ref v2, ref v7, ref v8, ref v13);
			QuarterRound(ref v3, ref v4, ref v9, ref v14);
		}

		v0 = Vector128.Add(v0, Vector128.Create(_state[0]));
		v1 = Vector128.Add(v1, Vector128.Create(_state[1]));
		v2 = Vector128.Add(v2, Vector128.Create(_state[2]));
		v3 = Vector128.Add(v3, Vector128.Create(_state[3]));
		v4 = Vector128.Add(v4, Vector128.Create(_state[4]));
		v5 = Vector128.Add(v5, Vector128.Create(_state[5]));
		v6 = Vector128.Add(v6, Vector128.Create(_state[6]));
		v7 = Vector128.Add(v7, Vector128.Create(_state[7]));
		v8 = Vector128.Add(v8, Vector128.Create(_state[8]));
		v9 = Vector128.Add(v9, Vector128.Create(_state[9]));
		v10 = Vector128.Add(v10, Vector128.Create(_state[10]));
		v11 = Vector128.Add(v11, Vector128.Create(_state[11]));
		v12 = Vector128.Add(v12, Vector128.Create(_state[12]));
		v13 = Vector128.Add(v13, Vector128.Create(_state[13]));
		v14 = Vector128.Add(v14, Vector128.Create(_state[14]));
		v15 = Vector128.Add(v15, Vector128.Create(_state[15]));

		// 写出：块 b 的字 i 位于 b*64 + i*4
		WriteKeystreamBlock(0, v0, v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13, v14, v15);
		WriteKeystreamBlock(1, v0, v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13, v14, v15);
		WriteKeystreamBlock(2, v0, v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13, v14, v15);
		WriteKeystreamBlock(3, v0, v1, v2, v3, v4, v5, v6, v7, v8, v9, v10, v11, v12, v13, v14, v15);

		_state[12] += SimdBlocks; // 4 个块 counter 递增
	}

	private void WriteKeystreamBlock(
		int blockIndex,
		Vector128<uint> v0,
		Vector128<uint> v1,
		Vector128<uint> v2,
		Vector128<uint> v3,
		Vector128<uint> v4,
		Vector128<uint> v5,
		Vector128<uint> v6,
		Vector128<uint> v7,
		Vector128<uint> v8,
		Vector128<uint> v9,
		Vector128<uint> v10,
		Vector128<uint> v11,
		Vector128<uint> v12,
		Vector128<uint> v13,
		Vector128<uint> v14,
		Vector128<uint> v15) {
		var span = new Span<byte>(_keystream, blockIndex * BlockSize, BlockSize);
		WriteLe32(span, 0, v0.GetElement(blockIndex));
		WriteLe32(span, 4, v1.GetElement(blockIndex));
		WriteLe32(span, 8, v2.GetElement(blockIndex));
		WriteLe32(span, 12, v3.GetElement(blockIndex));
		WriteLe32(span, 16, v4.GetElement(blockIndex));
		WriteLe32(span, 20, v5.GetElement(blockIndex));
		WriteLe32(span, 24, v6.GetElement(blockIndex));
		WriteLe32(span, 28, v7.GetElement(blockIndex));
		WriteLe32(span, 32, v8.GetElement(blockIndex));
		WriteLe32(span, 36, v9.GetElement(blockIndex));
		WriteLe32(span, 40, v10.GetElement(blockIndex));
		WriteLe32(span, 44, v11.GetElement(blockIndex));
		WriteLe32(span, 48, v12.GetElement(blockIndex));
		WriteLe32(span, 52, v13.GetElement(blockIndex));
		WriteLe32(span, 56, v14.GetElement(blockIndex));
		WriteLe32(span, 60, v15.GetElement(blockIndex));
	}

	/// <summary>与 keystream 异或（SIMD 可用时 16 字节向量异或，否则逐字节）。</summary>
	private void XorWithKeystream(Span<byte> buffer, int keyPos) {
		if (UseSimd) {
			ref var bufRef = ref MemoryMarshal.GetReference(buffer);
			ref var keyRef = ref MemoryMarshal.GetArrayDataReference(_keystream);
			var i = 0;
			for (; i + 16 <= buffer.Length; i += 16) {
				var data = Vector128.LoadUnsafe(ref bufRef, (nuint)i);
				var key = Vector128.LoadUnsafe(ref keyRef, (nuint)(keyPos + i));
				Vector128.StoreUnsafe(Vector128.Xor(data, key), ref bufRef, (nuint)i);
			}

			for (; i < buffer.Length; i++) {
				buffer[i] ^= _keystream[keyPos + i];
			}

			return;
		}

		for (var i = 0; i < buffer.Length; i++) {
			buffer[i] ^= _keystream[keyPos + i];
		}
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

	static private void QuarterRound(
		ref Vector128<uint> a,
		ref Vector128<uint> b,
		ref Vector128<uint> c,
		ref Vector128<uint> d) {
		a = Vector128.Add(a, b);
		d = Vector128.Xor(d, a);
		d = Vector128.BitwiseOr(Vector128.ShiftLeft(d, 16), Vector128.ShiftRightLogical(d, 16));
		c = Vector128.Add(c, d);
		b = Vector128.Xor(b, c);
		b = Vector128.BitwiseOr(Vector128.ShiftLeft(b, 12), Vector128.ShiftRightLogical(b, 20));
		a = Vector128.Add(a, b);
		d = Vector128.Xor(d, a);
		d = Vector128.BitwiseOr(Vector128.ShiftLeft(d, 8), Vector128.ShiftRightLogical(d, 24));
		c = Vector128.Add(c, d);
		b = Vector128.Xor(b, c);
		b = Vector128.BitwiseOr(Vector128.ShiftLeft(b, 7), Vector128.ShiftRightLogical(b, 25));
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
