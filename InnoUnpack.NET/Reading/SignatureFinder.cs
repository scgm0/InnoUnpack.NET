using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using InnoUnpack.NET.Metadata;

namespace InnoUnpack.NET.Reading;

/// <summary>
///     定位安装包中 setup 数据的起始偏移。
///     支持三种方式（参考 innoextract 的 loader 格式文档）：
///     1. exe 头部 0x30 处的 "Inno" 魔数与偏移表（revision 1/2）；
///     2. 在文件（overlay 区域）中直接搜索签名串；
///     3. 均失败时假定输入就是 setup-0.bin，头部从文件起始开始。
/// </summary>
static class SignatureFinder {
	private const uint SetupLoaderHeaderOffset = 0x30;
	private const uint SetupLoaderHeaderMagic = 0x6F6E6E49; // "Inno"

	static private readonly byte[][] _loaderMagics = [
		// 与 innoextract known_setup_loader_versions 对应（每项 12 字节）
		// Latin1 编码：U+0080-U+00FF 映射为单字节（u8 字面量的 \u 转义会编码为两字节，\x 会贪婪吞并后续十六进制字符）
		Latin1("rDlPtS02\u0087eVx"), // 1.2.10
		Latin1("rDlPtS04\u0087eVx"), // 4.0.0
		Latin1("rDlPtS05\u0087eVx"), // 4.0.3
		Latin1("rDlPtS06\u0087eVx"), // 4.0.10
		Latin1("rDlPtS07\u0087eVx"), // 4.1.6
		Latin1("rDlPtS\u00CD\u00E6\u00D7{\v*"), // 5.1.5
		Latin1("nS5W7dT\u0083\u00AA\e\u000Fj") // 5.1.5
	];

	static private byte[] Latin1(string s) { return Encoding.Latin1.GetBytes(s); }

	/// <summary>
	///     在安装包流中查找 setup 数据偏移。
	/// </summary>
	public static Offsets Find(Stream stream) {
		if (stream.CanSeek) {
			// 方式 1：exe 头部偏移表
			if (TryLoadFromExeFile(stream, out var offsets)) {
				return offsets;
			}

			// 方式 2：PE 资源中的偏移表（5.1.5+）
			if (TryLoadFromExeResource(stream, out offsets)) {
				return offsets;
			}

			// 方式 3：overlay 中搜索签名
			if (TrySearchSignature(stream, out offsets)) {
				return offsets;
			}
		}

		// 方式 3：假定为 setup-0.bin（setup 数据从文件起始开始）
		return new(0, 0);
	}

	static private bool TryLoadFromExeFile(Stream stream, out Offsets offsets) {
		offsets = default;
		try {
			stream.Position = SetupLoaderHeaderOffset;
			InnoBinaryReader reader = new(stream);
			if (reader.ReadUInt32() != SetupLoaderHeaderMagic) {
				return false;
			}

			var tableOffset = reader.ReadUInt32();
			var notTableOffset = reader.ReadUInt32();
			if (tableOffset != ~notTableOffset) {
				return false;
			}

			return TryLoadOffsetsAt(stream, tableOffset, out offsets);
		} catch (InnoFormatException) {
			return false;
		}
	}

	/// <summary>从 PE 资源（类型任意、ID 11111）读取偏移表。</summary>
	static private bool TryLoadFromExeResource(Stream stream, out Offsets offsets) {
		offsets = default;
		try {
			var resourceOffset = PeResourceReader.FindResourceData(stream, 11111);
			if (resourceOffset < 0) {
				return false;
			}

			return TryLoadOffsetsAt(stream, (ulong)resourceOffset, out offsets);
		} catch (InnoFormatException) {
			return false;
		}
	}

	static private bool TryLoadOffsetsAt(Stream stream, ulong pos, out Offsets offsets) {
		offsets = default;
		stream.Position = (long)pos;

		Span<byte> magic = stackalloc byte[12];
		if (!ReadExactly(stream, magic)) {
			return false;
		}

		var versionIndex = IndexOfMagic(magic);
		if (versionIndex < 0) {
			return false;
		}

		Crc32 checksum = new();
		checksum.Update(magic);
		InnoBinaryReader reader = new(stream);

		uint revision = 1;
		// 5.1.5+ 的偏移表包含 revision 字段
		if (versionIndex >= 5) {
			revision = ReadUInt32WithChecksum(reader, checksum);
		}

		if (revision == 2) {
			// Inno Setup 6.5.0+：TSetupLdrOffsetTable 为 Int64 布局（共 64 字节）
			_ = ReadInt64WithChecksum(reader, checksum); // TotalSize
			_ = ReadInt64WithChecksum(reader, checksum); // OffsetExe
			_ = ReadUInt32WithChecksum(reader, checksum); // UncompressedExe
			_ = ReadUInt32WithChecksum(reader, checksum); // CRCEXE
			var offset0 = ReadInt64WithChecksum(reader, checksum); // header
			var offset1 = ReadInt64WithChecksum(reader, checksum); // data
			_ = ReadUInt32WithChecksum(reader, checksum); // ReservedPadding
			uint expected;
			try {
				expected = reader.ReadUInt32(); // TableCRC
			} catch (InnoFormatException) {
				return false;
			}

			if (checksum.GetValue() != expected) {
				return false;
			}

			offsets = new((ulong)offset0, (ulong)offset1);
			return true;
		}

		// revision 1（传统 32 位布局）
		_ = ReadUInt32WithChecksum(reader, checksum); // TotalSize（忽略）
		_ = ReadUInt32WithChecksum(reader, checksum); // exe_offset
		if (versionIndex < 2) // 4.1.6 之前有 exe_compressed_size
		{
			_ = ReadUInt32WithChecksum(reader, checksum);
		}

		_ = ReadUInt32WithChecksum(reader, checksum); // exe_uncompressed_size
		_ = ReadUInt32WithChecksum(reader, checksum); // exe_checksum
		ulong headerOffset = ReadUInt32WithChecksum(reader, checksum);
		ulong dataOffset = ReadUInt32WithChecksum(reader, checksum);
		if (versionIndex >= 4) // 4.0.10+
		{
			uint expected;
			try {
				expected = reader.ReadUInt32(); // TableCRC
			} catch (InnoFormatException) {
				return false;
			}

			if (checksum.GetValue() != expected) {
				return false;
			}
		}

		offsets = new(headerOffset, dataOffset);
		return true;
	}

	static private int IndexOfMagic(ReadOnlySpan<byte> magic) {
		for (var i = 0; i < _loaderMagics.Length; i++) {
			if (magic.SequenceEqual(_loaderMagics[i])) {
				return i;
			}
		}

		return -1;
	}

	/// <summary>在流中搜索 Inno Setup 签名串。</summary>
	static private bool TrySearchSignature(Stream stream, out Offsets offsets) {
		offsets = default;
		var length = stream.Length;
		const string signature = "Inno Setup Setup Data";
		var pattern = Encoding.ASCII.GetBytes(signature);

		// 从文件末尾向前分块搜索（签名位于 overlay 数据起始处）
		var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
		try {
			var readEnd = length;
			while (readEnd > 0) {
				var readStart = Math.Max(0, readEnd - buffer.Length + pattern.Length - 1);
				var toRead = (int)(readEnd - readStart);
				stream.Position = readStart;
				var n = ReadPartial(stream, buffer, toRead);
				if (n <= 0) {
					break;
				}

				readEnd = readStart;

				var found = IndexOf(buffer.AsSpan(0, n), pattern);
				if (found >= 0) {
					var offset = readStart + found;
					if (LooksLikeValidSignature(stream, offset)) {
						offsets = new((ulong)offset, 0);
						return true;
					}
				}
			}
		} finally {
			ArrayPool<byte>.Shared.Return(buffer);
		}

		return false;
	}

	/// <summary>检查签名起始位置后是版本号并确认其为真实签名。</summary>
	static private bool LooksLikeValidSignature(Stream stream, long offset) {
		try {
			stream.Position = offset;
			var signatureBytes = new byte[InnoVersionParser.SignatureSize];
			var n = ReadPartial(stream, signatureBytes, signatureBytes.Length);
			if (n != signatureBytes.Length) {
				return false;
			}

			var version = InnoVersionParser.Parse(signatureBytes);
			return version.Major >= 4;
		} catch (InnoFormatException) {
			return false;
		}
	}

	static private uint ReadUInt32WithChecksum(InnoBinaryReader reader, Crc32 checksum) {
		Span<byte> data = stackalloc byte[4];
		reader.ReadExactly(data);
		checksum.Update(data);
		return BinaryPrimitives.ReadUInt32LittleEndian(data);
	}

	static private long ReadInt64WithChecksum(InnoBinaryReader reader, Crc32 checksum) {
		Span<byte> data = stackalloc byte[8];
		reader.ReadExactly(data);
		checksum.Update(data);
		return BinaryPrimitives.ReadInt64LittleEndian(data);
	}

	static private bool ReadExactly(Stream stream, Span<byte> buffer) {
		var offset = 0;
		while (offset < buffer.Length) {
			var n = stream.Read(buffer[offset..]);
			if (n <= 0) {
				return false;
			}

			offset += n;
		}

		return true;
	}

	static private int ReadPartial(Stream stream, byte[] buffer, int count) {
		var offset = 0;
		while (offset < count) {
			var n = stream.Read(buffer, offset, count - offset);
			if (n <= 0) {
				break;
			}

			offset += n;
		}

		return offset;
	}

	static private int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) {
		if (needle.Length == 0) {
			return 0;
		}

		for (var i = 0; i <= haystack.Length - needle.Length; i++) {
			if (haystack.Slice(i, needle.Length).SequenceEqual(needle)) {
				return i;
			}
		}

		return -1;
	}

	/// <summary>setup 数据布局偏移信息。</summary>
	internal readonly record struct Offsets(ulong HeaderOffset, ulong DataOffset);

	/// <summary>PE 文件资源读取器（用于定位 5.1.5+ 的偏移表资源）。</summary>
	static private class PeResourceReader {
		/// <summary>查找指定 ID 的资源数据，返回其在文件中的偏移；未找到返回 -1。</summary>
		public static long FindResourceData(Stream stream, int resourceId) {
			long peOffset;
			{
				stream.Position = 0x3C;
				InnoBinaryReader reader = new(stream);
				peOffset = reader.ReadUInt32();
			}

			stream.Position = peOffset;
			InnoBinaryReader peReader = new(stream);
			if (peReader.ReadUInt32() != 0x00004550) // "PE\0\0"
			{
				return -1;
			}

			_ = peReader.ReadUInt16(); // Machine
			var numberOfSections = peReader.ReadUInt16();
			_ = peReader.ReadUInt32(); // TimeDateStamp
			_ = peReader.ReadUInt32(); // PointerToSymbolTable
			_ = peReader.ReadUInt32(); // NumberOfSymbols
			var sizeOfOptionalHeader = peReader.ReadUInt16();
			_ = peReader.ReadUInt16(); // Characteristics

			// 数据目录
			var optionalHeaderOffset = stream.Position;
			var magic = peReader.ReadUInt16();
			int dataDirectoryOffset;
			if (magic == 0x10B) // PE32
			{
				dataDirectoryOffset = 96;
			} else if (magic == 0x20B) // PE32+
			{
				dataDirectoryOffset = 112;
			} else {
				return -1;
			}

			// 资源目录（数据目录索引 2）
			stream.Position = optionalHeaderOffset + dataDirectoryOffset + 2 * 8;
			var resourceRva = peReader.ReadUInt32();
			if (resourceRva == 0) {
				return -1;
			}

			// 节表
			var sectionTableOffset = optionalHeaderOffset + sizeOfOptionalHeader;
			long resourceFileOffset = -1;
			for (var i = 0; i < numberOfSections; i++) {
				stream.Position = sectionTableOffset + i * 40;
				peReader.Skip(8); // name
				var virtualSize = peReader.ReadUInt32();
				var virtualAddress = peReader.ReadUInt32();
				var sizeOfRawData = peReader.ReadUInt32();
				var pointerToRawData = peReader.ReadUInt32();
				if (resourceRva >= virtualAddress && resourceRva < virtualAddress + Math.Max(virtualSize, sizeOfRawData)) {
					resourceFileOffset = resourceRva - virtualAddress + pointerToRawData;
					break;
				}
			}

			if (resourceFileOffset < 0) {
				return -1;
			}

			// 资源目录树：类型（任意）→ 名称（resourceId）→ 语言（任意）→ 数据
			var root = resourceFileOffset;
			var typeEntries = ReadDirectoryEntries(stream, root);
			foreach (var (_, typeSub) in typeEntries) {
				var nameEntries = ReadDirectoryEntries(stream, root + typeSub);
				foreach (var (nameId, nameSub) in nameEntries) {
					if (nameId != resourceId) {
						continue;
					}

					var langEntries = ReadDirectoryEntries(stream, root + nameSub);
					foreach (var (_, dataSub) in langEntries) {
						// 数据条目：RVA + Size
						stream.Position = root + dataSub;
						InnoBinaryReader dataReader = new(stream);
						var dataRva = dataReader.ReadUInt32();
						if (dataRva == 0) {
							continue;
						}

						// RVA → 文件偏移
						var fileOffset = RvaToFileOffset(stream, dataRva, sectionTableOffset, numberOfSections);
						if (fileOffset >= 0) {
							return fileOffset;
						}
					}
				}
			}

			return -1;
		}

		/// <summary>将 RVA 转换为文件偏移。</summary>
		static private long RvaToFileOffset(Stream stream, uint rva, long sectionTableOffset, int numberOfSections) {
			InnoBinaryReader reader = new(stream);
			for (var i = 0; i < numberOfSections; i++) {
				stream.Position = sectionTableOffset + i * 40;
				reader.Skip(8);
				var virtualSize = reader.ReadUInt32();
				var virtualAddress = reader.ReadUInt32();
				var sizeOfRawData = reader.ReadUInt32();
				var pointerToRawData = reader.ReadUInt32();
				if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, sizeOfRawData)) {
					return rva - virtualAddress + pointerToRawData;
				}
			}

			return -1;
		}

		/// <summary>读取资源目录的条目（ID 与子目录/数据偏移对）。</summary>
		static private (uint Id, long Offset)[] ReadDirectoryEntries(Stream stream, long directoryOffset) {
			stream.Position = directoryOffset + 12;
			InnoBinaryReader reader = new(stream);
			var namedCount = reader.ReadUInt16();
			var idCount = reader.ReadUInt16();
			var count = namedCount + idCount;
			var result = new (uint, long)[count];
			for (var i = 0; i < count; i++) {
				var name = reader.ReadUInt32();
				var offsetToData = reader.ReadUInt32();
				long value = offsetToData & 0x7FFFFFFF;
				result[i] = (name, value);
			}

			return result;
		}
	}
}