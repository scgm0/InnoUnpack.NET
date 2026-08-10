using InnoUnpack.NET.Reading;

namespace InnoUnpack.NET.Metadata;

/// <summary>
///     6.5.0+ 安装包的加密头（TSetupEncryptionHeader），位于版本串之后、第一个数据块之前。
///     当前版本只解析并校验，不提供解密。
/// </summary>
public sealed class InnoEncryptionHeader {
	/// <summary>是否使用了加密。</summary>
	public bool Encrypted { get; internal set; }

	/// <summary>KDF 盐（16 字节）。</summary>
	public byte[] KdfSalt { get; internal set; } = [];

	/// <summary>KDF 迭代次数。</summary>
	public uint KdfIterations { get; internal set; }

	/// <summary>基础 nonce 的随机起始偏移。</summary>
	public ulong RandomXorStartOffset { get; internal set; }

	/// <summary>基础 nonce 的随机起始切片。</summary>
	public uint RandomXorFirstSlice { get; internal set; }

	/// <summary>剩余随机数据（12 字节）。</summary>
	public byte[] RemainingRandom { get; internal set; } = [];

	/// <summary>密码测试值（4 字节）。</summary>
	public byte[] PasswordTest { get; internal set; } = [];
}

/// <summary>
///     安装包的完整元数据信息（主头部、语言、目录、文件与数据条目表）。
/// </summary>
public sealed class InnoSetupInfo {
	/// <summary>setup 数据版本。</summary>
	public InnoVersion Version { get; internal set; }

	/// <summary>主头部。</summary>
	public InnoHeader Header { get; internal set; } = new();

	/// <summary>语言表。</summary>
	public IReadOnlyList<InnoLanguageEntry> Languages { get; internal set; } = [];

	/// <summary>目录表。</summary>
	public IReadOnlyList<InnoDirectoryEntry> Directories { get; internal set; } = [];

	/// <summary>文件表。</summary>
	public IReadOnlyList<InnoFileEntry> Files { get; internal set; } = [];

	/// <summary>数据条目表（描述文件数据的 chunk 位置）。</summary>
	public IReadOnlyList<InnoDataEntry> DataEntries { get; internal set; } = [];

	/// <summary>6.5.0+ 安装包的加密头（非 6.5.0+ 为 null）。</summary>
	public InnoEncryptionHeader? EncryptionHeader { get; internal set; }

	/// <summary>字符串解码使用的代码页（Unicode 安装包为 1200/UTF-16LE）。</summary>
	public int Codepage { get; internal set; } = InnoStringDecoder.CpWindows1252;

	/// <summary>文件数据（chunk）区在流中的起始偏移。</summary>
	internal long DataOffset { get; set; }

	/// <summary>是否为加密安装包（文件数据已加密）。</summary>
	public bool IsEncrypted =>
		(Header.Options & InnoHeaderOptions.EncryptionUsed) != 0
		|| EncryptionHeader?.Encrypted == true;

	/// <summary>
	///     从底层流解析安装包元数据。
	/// </summary>
	/// <param name="stream">安装包流（已定位到 setup 数据起始处）。</param>
	/// <param name="forceCodepage">强制使用的代码页（ANSI 安装包），0 表示自动推断。</param>
	internal static InnoSetupInfo Load(Stream stream, int forceCodepage) {
		var start = stream.Position;

		var version = ReadVersion(stream);
		if (version.Is16Bit || version.Major < 4) {
			throw new InnoUnsupportedException(
				$"不支持的 Inno Setup 版本：{version}（仅支持 4.0 及以上）");
		}

		var ambiguous = !version.IsKnown || InnoVersionParser.IsAmbiguous(version);
		Exception? lastError = null;

		InnoVersion? current = version;
		while (current is not null) {
			stream.Position = start;
			_ = ReadVersion(stream);

			try {
				var info = TryLoad(stream, current.Value, forceCodepage);
				info.Version = current.Value;
				return info;
			} catch (Exception ex) when (ex is InnoFormatException or InnoUnsupportedException) {
				lastError = ex;
				if (!ambiguous) {
					throw;
				}
			}

			var next = InnoVersionParser.Next(current.Value);
			if (next is null || next == current.Value) {
				throw lastError ?? new InnoFormatException("无法解析安装包元数据");
			}

			current = next;
		}

		throw lastError ?? new InnoFormatException("无法解析安装包元数据");
	}

	static private InnoVersion ReadVersion(Stream stream) {
		var signature = new byte[InnoVersionParser.SignatureSize];
		var n = stream.Read(signature, 0, signature.Length);
		if (n < 12) {
			throw new InnoFormatException("无法读取版本签名（文件过短）");
		}

		return InnoVersionParser.Parse(signature);
	}

	static private InnoEncryptionHeader ReadEncryptionHeader(InnoBinaryReader reader) {
		var expectedCrc = reader.ReadUInt32();
		Crc32 checksum = new();

		var useBytes = reader.ReadBytes(1);
		checksum.Update(useBytes);
		var encrypted = useBytes[0] != 0;

		InnoEncryptionHeader header = new() { Encrypted = encrypted };

		var salt = reader.ReadBytes(16);
		checksum.Update(salt);
		header.KdfSalt = salt;

		var iterations = reader.ReadBytes(4);
		checksum.Update(iterations);
		header.KdfIterations = BitConverter.ToUInt32(iterations, 0);

		var xorStart = reader.ReadBytes(8);
		checksum.Update(xorStart);
		header.RandomXorStartOffset = BitConverter.ToUInt64(xorStart, 0);

		var xorSlice = reader.ReadBytes(4);
		checksum.Update(xorSlice);
		header.RandomXorFirstSlice = BitConverter.ToUInt32(xorSlice, 0);

		var remaining = reader.ReadBytes(12);
		checksum.Update(remaining);
		header.RemainingRandom = remaining;

		var passwordTest = reader.ReadBytes(4);
		checksum.Update(passwordTest);
		header.PasswordTest = passwordTest;

		if (checksum.GetValue() != expectedCrc) {
			throw new InnoFormatException("加密头 CRC32 校验失败");
		}

		return header;
	}

	static private InnoSetupInfo TryLoad(Stream stream, InnoVersion version, int forceCodepage) {
		InnoSetupInfo info = new() { Version = version };
		InnoBinaryReader reader = new(stream);

		// 6.5.0+：读取独立加密头
		if (version >= InnoVersion.From(6, 5, 0, version)) {
			info.EncryptionHeader = ReadEncryptionHeader(reader);
		}

		using var block = BlockReader.Create(reader, stream, version);
		InnoBinaryReader blockReader = new(block.Stream);

		var headerRaw = InnoHeader.Parse(blockReader, version);

		// 语言表（单遍解析，名称暂不解码）
		List<InnoLanguageEntry> languages = new(headerRaw.LanguageCount);
		for (var i = 0; i < headerRaw.LanguageCount; i++) {
			languages.Add(InnoLanguageEntry.Parse(blockReader, version, InnoStringDecoder.CpWindows1252));
		}

		// 确定代码页
		int codepage;
		if (version.IsUnicode) {
			codepage = InnoStringDecoder.CpUtf16Le;
		} else if (forceCodepage != 0) {
			codepage = forceCodepage;
		} else if (languages.Count == 0) {
			codepage = InnoStringDecoder.CpWindows1252;
		} else {
			codepage = (int)languages[0].Codepage;
			foreach (var language in languages) {
				if (language.Codepage == InnoStringDecoder.CpWindows1252) {
					codepage = InnoStringDecoder.CpWindows1252;
					break;
				}
			}
		}

		info.Codepage = codepage;

		// 解码主头部与语言名称
		info.Header = InnoHeader.FromRaw(headerRaw, codepage);
		foreach (var language in languages) {
			language.DecodeName(codepage);
		}

		info.Languages = languages;

		// 跳过：messages、permissions、types、components、tasks
		SkipEntries(blockReader, version, headerRaw.MessageCount, SkipMessageEntry);
		SkipEntries(blockReader, version, headerRaw.PermissionCount, SkipPermissionEntry);
		SkipEntries(blockReader, version, headerRaw.TypeCount, SkipTypeEntry);
		SkipEntries(blockReader, version, headerRaw.ComponentCount, SkipComponentEntry);
		SkipEntries(blockReader, version, headerRaw.TaskCount, SkipTaskEntry);

		// 目录表
		var leadBytes = codepage == InnoStringDecoder.CpUtf16Le ? null : headerRaw.LeadBytes;
		List<InnoDirectoryEntry> directories = new(headerRaw.DirectoryCount);
		for (var i = 0; i < headerRaw.DirectoryCount; i++) {
			directories.Add(InnoDirectoryEntry.Parse(blockReader, version, codepage, leadBytes));
		}

		info.Directories = directories;

		// ISSig 键表（6.5.0+）：PublicX/PublicY/RuntimeID 三个字符串
		for (var i = 0; i < headerRaw.IssigKeyCount; i++) {
			blockReader.SkipStringBytes();
			blockReader.SkipStringBytes();
			blockReader.SkipStringBytes();
		}

		// 文件表
		List<InnoFileEntry> files = new(headerRaw.FileCount);
		for (var i = 0; i < headerRaw.FileCount; i++) {
			files.Add(InnoFileEntry.Parse(blockReader, version, codepage, leadBytes));
		}

		info.Files = files;

		// 跳过：icons、ini、registry、delete、uninstall delete、run、uninstall run
		SkipEntries(blockReader, version, headerRaw.IconCount, SkipIconEntry);
		SkipEntries(blockReader, version, headerRaw.IniEntryCount, SkipIniEntry);
		SkipEntries(blockReader, version, headerRaw.RegistryEntryCount, SkipRegistryEntry);
		SkipEntries(blockReader, version, headerRaw.DeleteEntryCount, SkipDeleteEntry);
		SkipEntries(blockReader, version, headerRaw.UninstallDeleteEntryCount, SkipDeleteEntry);
		SkipEntries(blockReader, version, headerRaw.RunEntryCount, SkipRunEntry);
		SkipEntries(blockReader, version, headerRaw.UninstallRunEntryCount, SkipRunEntry);

		// 向导图片与辅助 DLL
		SkipWizardImages(blockReader, version, info.Header, leadBytes);

		// 主块必须正好结束
		if (block.Stream.ReadByte() != -1) {
			throw new InnoFormatException("主头部块末尾存在未知数据");
		}

		// 次块：数据条目
		using var dataBlock = BlockReader.Create(reader, stream, version);
		info.DataOffset = dataBlock.PhysicalEnd;
		InnoBinaryReader dataReader = new(dataBlock.Stream);
		List<InnoDataEntry> dataEntries = new(headerRaw.DataEntryCount);
		for (var i = 0; i < headerRaw.DataEntryCount; i++) {
			dataEntries.Add(InnoDataEntry.Parse(dataReader, version, info.Header.Compression));
		}

		info.DataEntries = dataEntries;

		if (dataBlock.Stream.ReadByte() != -1) {
			throw new InnoFormatException("数据条目块末尾存在未知数据");
		}

		return info;
	}

	static private void SkipEntries(
		InnoBinaryReader reader,
		InnoVersion version,
		int count,
		Action<InnoBinaryReader, InnoVersion> skipper) {
		for (var i = 0; i < count; i++) {
			skipper(reader, version);
		}
	}

	static private void SkipWizardImages(InnoBinaryReader reader, InnoVersion version, InnoHeader header, bool[]? leadBytes) {
		InnoHeader.VersionGates v = new(version);
		SkipWizardImageGroup(reader, version);
		if (v.Ge200) {
			SkipWizardImageGroup(reader, version);
		}

		if (v.Ge670) {
			SkipWizardImageGroup(reader, version); // back 图像组
		}

		if (v.Ge660) {
			SkipWizardImageGroup(reader, version);
			SkipWizardImageGroup(reader, version);
			if (v.Ge670) {
				SkipWizardImageGroup(reader, version);
			}
		}

		// decompressor DLL
		var hasDecompressorDll =
			header.Compression == InnoCompressionMethod.BZip2
			|| header.Compression == InnoCompressionMethod.Lzma1 && version is { Major: 4, Minor: 1, Patch: 5 }
			|| header.Compression == InnoCompressionMethod.Zlib && v.Ge426;
		if (hasDecompressorDll) {
			reader.SkipStringBytes();
		}

		// 6.5.0+：7-Zip 解码 DLL（仅当 SevenZipLibraryName 非空）
		if (v.Ge650 && header.SevenZipLibraryName.Length > 0) {
			reader.SkipStringBytes();
		}

		// decrypt DLL（EncryptionUsed 且 < 6.4.0）
		if ((header.Options & InnoHeaderOptions.EncryptionUsed) != 0 && v.Lt640) {
			reader.SkipStringBytes();
		}
	}

	static private void SkipWizardImageGroup(InnoBinaryReader reader, InnoVersion version) {
		var count = 1;
		if (version >= InnoVersion.From(5, 6, 0, version)) {
			var wc = reader.ReadBytes(4);
			count = BitConverter.ToInt32(wc, 0);
		}

		for (var i = 0; i < count; i++) {
			var wl = reader.ReadBytes(4);
			var wlen = BitConverter.ToInt32(wl, 0);
			var wb = reader.ReadBytes(wlen);
		}
	}

	static private void SkipMessageEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes(); // name
		reader.SkipStringBytes(); // value
		_ = reader.ReadInt32(); // language
	}

	static private void SkipPermissionEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes();
		// permissions 数组
	}

	static private void SkipTypeEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes(); // name
		reader.SkipStringBytes(); // description
		if (Ge(version, 4, 0, 1)) {
			reader.SkipStringBytes(); // languages
		}

		if (Ge(version, 4, 0, 0)) {
			reader.SkipStringBytes(); // check
		}

		ReadWindowsVersionRange(reader);
		_ = reader.ReadByte(); // options
		if (Ge(version, 4, 0, 3)) {
			_ = reader.ReadByte(); // type
		}

		if (Ge(version, 4, 0, 0)) {
			_ = reader.ReadUInt64(); // size
		} else {
			_ = reader.ReadUInt32();
		}
	}

	static private void SkipComponentEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes(); // name
		reader.SkipStringBytes(); // description
		reader.SkipStringBytes(); // types
		if (Ge(version, 4, 0, 1)) {
			reader.SkipStringBytes(); // languages
		}

		if (Ge(version, 4, 0, 0)) {
			reader.SkipStringBytes(); // check
		}

		if (Ge(version, 4, 0, 0)) {
			_ = reader.ReadUInt64(); // extra_disk_space
		} else {
			_ = reader.ReadUInt32();
		}

		if (Ge(version, 4, 0, 0)) {
			if (Ge(version, 6, 7, 0)) {
				_ = reader.ReadByte(); // level（6.7.0 起为 Byte）
			} else {
				_ = reader.ReadInt32();
			}

			_ = reader.ReadByte(); // used
		}

		ReadWindowsVersionRange(reader);
		_ = reader.ReadByte(); // options
		if (Ge(version, 4, 0, 0)) {
			_ = reader.ReadUInt64(); // size
		} else if (Ge(version, 2, 0, 0)) {
			_ = reader.ReadUInt32();
		}
	}

	static private void SkipTaskEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes(); // name
		reader.SkipStringBytes(); // description
		reader.SkipStringBytes(); // group_description
		reader.SkipStringBytes(); // components
		if (Ge(version, 4, 0, 1)) {
			reader.SkipStringBytes(); // languages
		}

		if (Ge(version, 4, 0, 0)) {
			reader.SkipStringBytes(); // check
		}

		if (Ge(version, 4, 0, 0)) {
			if (Ge(version, 6, 7, 0)) {
				_ = reader.ReadByte(); // level（Byte）
			} else {
				_ = reader.ReadInt32();
			}

			_ = reader.ReadByte(); // used
		}

		ReadWindowsVersionRange(reader);
		_ = reader.ReadByte(); // options
	}

	static private void SkipIconEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes(); // name
		reader.SkipStringBytes(); // filename
		reader.SkipStringBytes(); // parameters
		reader.SkipStringBytes(); // working_dir
		reader.SkipStringBytes(); // icon_file
		reader.SkipStringBytes(); // comment
		SkipConditions(reader, version);
		if (Ge(version, 5, 3, 5)) {
			reader.SkipStringBytes(); // app_user_model_id
		}

		if (Ge(version, 6, 1, 0)) {
			_ = reader.ReadBytes(16); // app_user_model_toast_activator_clsid
		}

		ReadWindowsVersionRange(reader);
		_ = reader.ReadInt32(); // icon_index
		if (Ge(version, 1, 3, 24)) {
			_ = reader.ReadInt32(); // show_command
		}

		if (Ge(version, 1, 3, 15)) {
			_ = reader.ReadByte(); // close_on_exit
		}

		if (Ge(version, 2, 0, 7)) {
			_ = reader.ReadUInt16(); // hotkey
		}

		SkipFlagReader flags = new(reader);
		flags.Add(); // NeverUninstall
		if (!Ge(version, 1, 3, 26)) flags.Add(); // RunMinimized
		flags.Add(); // CreateOnlyIfFileExists
		flags.Add(); // UseAppPaths
		if (Ge(version, 5, 0, 3) && !Ge(version, 6, 3, 0)) flags.Add(); // FolderShortcut
		if (Ge(version, 5, 4, 2)) flags.Add(); // ExcludeFromShowInNewInstall
		if (Ge(version, 5, 5, 0)) flags.Add(); // PreventPinning
		if (Ge(version, 6, 1, 0)) flags.Add(); // HasAppUserModelToastActivatorCLSID
	}

	static private void SkipIniEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes(); // inifile
		reader.SkipStringBytes(); // section
		reader.SkipStringBytes(); // key
		reader.SkipStringBytes(); // value
		SkipConditions(reader, version);
		ReadWindowsVersionRange(reader);
		_ = reader.ReadByte(); // options
	}

	static private void SkipRegistryEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes(); // key
		reader.SkipStringBytes(); // name
		reader.SkipStringBytes(); // value
		SkipConditions(reader, version);
		ReadWindowsVersionRange(reader);
		_ = reader.ReadUInt32(); // hive
		if (Ge(version, 4, 1, 0)) {
			_ = reader.ReadUInt16(); // permission
		}

		_ = reader.ReadByte(); // type
		if (Ge(version, 7, 0, 0, 3)) {
			_ = reader.ReadByte(); // bitness
		}

		SkipFlagReader flags = new(reader);
		flags.Add(); // CreateValueIfDoesntExist
		flags.Add(); // UninsDeleteValue
		flags.Add(); // UninsClearValue
		flags.Add(); // UninsDeleteEntireKey
		flags.Add(); // UninsDeleteEntireKeyIfEmpty
		if (Ge(version, 1, 2, 6)) flags.Add(); // PreserveStringType
		if (Ge(version, 1, 3, 9)) {
			flags.Add();
			flags.Add();
		} // DeleteKey, DeleteValue

		if (Ge(version, 1, 3, 12)) flags.Add(); // NoError
		if (Ge(version, 1, 3, 16)) flags.Add(); // DontCreateKey
		if (Ge(version, 5, 1, 0) && !Ge(version, 7, 0, 0, 3)) {
			flags.Add();
			flags.Add();
		} // Bits32, Bits64
	}

	static private void SkipDeleteEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes(); // name
		SkipConditions(reader, version);
		ReadWindowsVersionRange(reader);
		_ = reader.ReadByte(); // type
	}

	static private void SkipRunEntry(InnoBinaryReader reader, InnoVersion version) {
		reader.SkipStringBytes(); // name
		reader.SkipStringBytes(); // parameters
		reader.SkipStringBytes(); // working_dir
		if (Ge(version, 1, 3, 9)) {
			reader.SkipStringBytes(); // run_once_id
		}

		if (Ge(version, 2, 0, 2)) {
			reader.SkipStringBytes(); // status_message
		}

		if (Ge(version, 5, 1, 13)) {
			reader.SkipStringBytes(); // verb
		}

		if (Ge(version, 2, 0, 0)) {
			reader.SkipStringBytes(); // description
		}

		SkipConditions(reader, version);
		if (Ge(version, 7, 0, 0, 1)) {
			reader.SkipStringBytes(); // on_log
		}

		ReadWindowsVersionRange(reader);
		if (Ge(version, 1, 3, 24)) {
			_ = reader.ReadInt32(); // show_command
		}

		_ = reader.ReadByte(); // wait
		if (Ge(version, 7, 0, 0, 3)) {
			_ = reader.ReadByte(); // bitness
		}

		SkipFlagReader flags = new(reader);
		if (Ge(version, 1, 2, 3)) flags.Add(); // ShellExec
		if (Ge(version, 1, 3, 9)) flags.Add(); // SkipIfDoesntExist
		if (Ge(version, 2, 0, 0)) {
			flags.Add();
			flags.Add();
			flags.Add();
			flags.Add();
		} // PostInstall, Unchecked, SkipIfSilent, SkipIfNotSilent

		if (Ge(version, 2, 0, 8)) flags.Add(); // HideWizard
		if (Ge(version, 5, 1, 10) && !Ge(version, 7, 0, 0, 3)) {
			flags.Add();
			flags.Add();
		} // Bits32, Bits64

		if (Ge(version, 5, 2, 0)) flags.Add(); // RunAsOriginalUser
		if (Ge(version, 6, 1, 0)) flags.Add(); // DontLogParameters
		if (Ge(version, 6, 3, 0)) flags.Add(); // LogOutput
	}

	/// <summary>跳过条目条件字符串（components/tasks/languages/check/after_install/before_install）。</summary>
	static private void SkipConditions(InnoBinaryReader reader, InnoVersion version) {
		if (Ge(version, 2, 0, 0)) {
			reader.SkipStringBytes(); // components
			reader.SkipStringBytes(); // tasks
		}

		if (Ge(version, 4, 0, 1)) {
			reader.SkipStringBytes(); // languages
		}

		if (Ge(version, 4, 0, 0)) {
			reader.SkipStringBytes(); // check
		}

		if (Ge(version, 4, 1, 0)) {
			reader.SkipStringBytes(); // after_install
			reader.SkipStringBytes(); // before_install
		}
	}

	/// <summary>读取完整的 windows_version_range（每侧 10 字节）。</summary>
	static private void ReadWindowsVersionRange(InnoBinaryReader reader) { reader.Skip(20); }

	static private bool Ge(InnoVersion version, uint a, uint b, uint c, uint d = 0) {
		return version.CompareTo(new(a, b, c, d, version.IsUnicode, version.IsIsx, false, true)) >= 0;
	}

	/// <summary>按位跳过标志字节。</summary>
	sealed private class SkipFlagReader(InnoBinaryReader reader) {
		private int _bits;

		public void Add() {
			if ((_bits & 7) == 0) {
				_ = reader.ReadByte();
			}

			_bits++;
		}
	}
}