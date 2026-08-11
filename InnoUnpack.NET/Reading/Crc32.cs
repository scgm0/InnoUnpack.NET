using System.Buffers.Binary;

namespace InnoUnpack.NET.Reading;

/// <summary>
///     CRC-32 校验和实现（IEEE 802.3 多项式，反射式，初值 0xFFFFFFFF，异或输出 0xFFFFFFFF），
///     用于校验块头、加密头等数据。
///     主循环采用 Slice-by-8 算法：8 个 256 项子表，每 8 字节并行异或（比单字节查表快约 4-8 倍，零分配）。
/// </summary>
sealed class Crc32 {
	private const int SliceCount = 8;

	static private readonly uint[] Table = BuildTable();

	private uint _value = 0xFFFFFFFF;

	/// <summary>
	///     构建 Slice-by-8 表：T0 为经典单字节表；Tk[b] = S^k(T0[b])，S(x) = (x &gt;&gt; 8) ^ T0[x &amp; 0xFF]。
	///     由 CRC 线性性可证：处理 8 字节 b0..b7 后
	///     crc' = T7[(crc^b0)&amp;0xFF] ^ T6[((crc&gt;&gt;8)^b1)&amp;0xFF] ^ T5[((crc&gt;&gt;16)^b2)&amp;0xFF]
	///     ^ T4[((crc&gt;&gt;24)^b3)&amp;0xFF] ^ T3[b4] ^ T2[b5] ^ T1[b6] ^ T0[b7]。
	/// </summary>
	static private uint[] BuildTable() {
		var table = new uint[SliceCount * 256];
		for (uint i = 0; i < 256; i++) {
			var c = i;
			for (var k = 0; k < 8; k++) {
				c = (c & 1) != 0 ? 0xEDB88320 ^ c >> 1 : c >> 1;
			}

			table[i] = c;
		}

		for (var k = 1; k < SliceCount; k++) {
			var prev = (k - 1) * 256;
			var cur = k * 256;
			for (var i = 0; i < 256; i++) {
				var x = table[prev + i];
				table[cur + i] = x >> 8 ^ table[(int)(x & 0xFF)];
			}
		}

		return table;
	}

	public void Update(ReadOnlySpan<byte> data) {
		var c = _value;
		while (data.Length >= 8) {
			var low = BinaryPrimitives.ReadUInt32LittleEndian(data);
			var high = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
			var mixed = c ^ low;
			c = Table[(int)(mixed & 0xFF) + 7 * 256]
				^ Table[(int)((mixed >> 8) & 0xFF) + 6 * 256]
				^ Table[(int)((mixed >> 16) & 0xFF) + 5 * 256]
				^ Table[(int)((mixed >> 24) & 0xFF) + 4 * 256]
				^ Table[(int)(high & 0xFF) + 3 * 256]
				^ Table[(int)((high >> 8) & 0xFF) + 2 * 256]
				^ Table[(int)((high >> 16) & 0xFF) + 1 * 256]
				^ Table[(int)(high >> 24)];
			data = data[8..];
		}

		foreach (var b in data) {
			c = Table[(c ^ b) & 0xFF] ^ c >> 8;
		}

		_value = c;
	}

	public void Update(byte b) { _value = Table[(_value ^ b) & 0xFF] ^ _value >> 8; }

	/// <summary>返回最终的 CRC-32 值。</summary>
	public uint GetValue() { return _value ^ 0xFFFFFFFF; }

	/// <summary>计算一段数据的 CRC-32。</summary>
	public static uint Compute(ReadOnlySpan<byte> data) {
		Crc32 crc = new();
		crc.Update(data);
		return crc.GetValue();
	}
}
