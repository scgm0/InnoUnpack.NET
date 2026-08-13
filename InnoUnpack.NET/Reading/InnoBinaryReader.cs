using System.Buffers.Binary;

namespace InnoUnpack.NET.Reading;

/// <summary>
///     从小端序二进制流中读取 Inno Setup 格式的各类标量与字符串。
///     所有多字节整数均按小端序存储。
/// </summary>
sealed class InnoBinaryReader(Stream stream) {

	/// <summary>当前流位置。</summary>
	public long Position => stream.Position;

	public byte ReadByte() {
		var value = stream.ReadByte();
		if (value < 0) {
			throw new InnoFormatException("意外的文件结束");
		}

		return (byte)value;
	}

	public ushort ReadUInt16() {
		Span<byte> buffer = stackalloc byte[2];
		ReadExactly(buffer);
		return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
	}

	public uint ReadUInt32() {
		Span<byte> buffer = stackalloc byte[4];
		ReadExactly(buffer);
		return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
	}

	public int ReadInt32() {
		Span<byte> buffer = stackalloc byte[4];
		ReadExactly(buffer);
		return BinaryPrimitives.ReadInt32LittleEndian(buffer);
	}

	public long ReadInt64() {
		Span<byte> buffer = stackalloc byte[8];
		ReadExactly(buffer);
		return BinaryPrimitives.ReadInt64LittleEndian(buffer);
	}

	public ulong ReadUInt64() {
		Span<byte> buffer = stackalloc byte[8];
		ReadExactly(buffer);
		return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
	}

	/// <summary>读取指定数量的字节。</summary>
	public byte[] ReadBytes(int count) {
		var result = new byte[count];
		ReadExactly(result);
		return result;
	}

	/// <summary>跳过指定数量的字节。</summary>
	public void Skip(long count) {
		ArgumentOutOfRangeException.ThrowIfNegative(count);

		if (count == 0) {
			return;
		}

		if (stream.CanSeek) {
			stream.Seek(count, SeekOrigin.Current);
			return;
		}

		Span<byte> buffer = stackalloc byte[4096];
		while (count > 0) {
			var n = (int)Math.Min(count, buffer.Length);
			ReadExactly(buffer[..n]);
			count -= n;
		}
	}

	/// <summary>
	///     读取一个长度前缀字符串的原始字节。
	///     格式为 4 字节小端长度 + 内容字节（不含终止符）。
	/// </summary>
	public byte[] ReadStringBytes() {
		var length = ReadUInt32();
		// 防御损坏/恶意长度：真实字符串远小于此上限（编译代码/许可文本 < 64 MiB）
		const int maxStringBytes = 256 * 1024 * 1024;
		if (length > maxStringBytes) {
			throw new InnoFormatException($"字符串长度异常：{length}");
		}

		return ReadBytes((int)length);
	}

	/// <summary>跳过长度前缀字符串（不分配内容缓冲区）。</summary>
	public void SkipStringBytes() {
		var length = ReadUInt32();
		// 跳过不分配大缓冲区，但同样拒绝异常长度以尽早终止
		const int maxStringBytes = 256 * 1024 * 1024;
		if (length > maxStringBytes) {
			throw new InnoFormatException($"字符串长度异常：{length}");
		}

		Skip(length);
	}

	internal void ReadExactly(Span<byte> buffer) {
		try {
			stream.ReadExactly(buffer);
		} catch (EndOfStreamException) {
			throw new InnoFormatException("意外的文件结束");
		}
	}
}