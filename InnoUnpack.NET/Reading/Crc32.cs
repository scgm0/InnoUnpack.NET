namespace InnoUnpack.NET.Reading;

/// <summary>
///     CRC-32 校验和实现（IEEE 802.3 多项式，反射式，初值 0xFFFFFFFF，异或输出 0xFFFFFFFF），
///     用于校验块头、加密头等数据。
/// </summary>
sealed class Crc32 {
	static private readonly uint[] _table = BuildTable();

	private uint _value = 0xFFFFFFFF;

	static private uint[] BuildTable() {
		var table = new uint[256];
		for (uint i = 0; i < 256; i++) {
			var c = i;
			for (var k = 0; k < 8; k++) {
				c = (c & 1) != 0 ? 0xEDB88320 ^ c >> 1 : c >> 1;
			}

			table[i] = c;
		}

		return table;
	}

	public void Update(ReadOnlySpan<byte> data) {
		var c = _value;
		foreach (var b in data) {
			c = _table[(c ^ b) & 0xFF] ^ c >> 8;
		}

		_value = c;
	}

	public void Update(byte b) { _value = _table[(_value ^ b) & 0xFF] ^ _value >> 8; }

	/// <summary>返回最终的 CRC-32 值。</summary>
	public uint GetValue() { return _value ^ 0xFFFFFFFF; }

	/// <summary>计算一段数据的 CRC-32。</summary>
	public static uint Compute(ReadOnlySpan<byte> data) {
		Crc32 crc = new();
		crc.Update(data);
		return crc.GetValue();
	}
}