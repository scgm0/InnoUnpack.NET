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
///     安装包的完整元数据信息（主头部、语言、目录、文件、数据条目与脚本条目表）。
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

	/// <summary>自定义消息表（[CustomMessages] 段）。</summary>
	public IReadOnlyList<InnoMessageEntry> Messages { get; internal set; } = [];

	/// <summary>权限表（[Permissions] 段）。</summary>
	public IReadOnlyList<InnoPermissionEntry> Permissions { get; internal set; } = [];

	/// <summary>setup 类型表（[Types] 段）。</summary>
	public IReadOnlyList<InnoTypeEntry> Types { get; internal set; } = [];

	/// <summary>组件表（[Components] 段）。</summary>
	public IReadOnlyList<InnoComponentEntry> Components { get; internal set; } = [];

	/// <summary>任务表（[Tasks] 段）。</summary>
	public IReadOnlyList<InnoTaskEntry> Tasks { get; internal set; } = [];

	/// <summary>图标表（[Icons] 段）。</summary>
	public IReadOnlyList<InnoIconEntry> Icons { get; internal set; } = [];

	/// <summary>INI 条目表（[INI] 段）。</summary>
	public IReadOnlyList<InnoIniEntry> IniEntries { get; internal set; } = [];

	/// <summary>注册表条目表（[Registry] 段）。</summary>
	public IReadOnlyList<InnoRegistryEntry> RegistryEntries { get; internal set; } = [];

	/// <summary>安装删除条目表（[InstallDelete] 段）。</summary>
	public IReadOnlyList<InnoDeleteEntry> DeleteEntries { get; internal set; } = [];

	/// <summary>卸载删除条目表（[UninstallDelete] 段）。</summary>
	public IReadOnlyList<InnoDeleteEntry> UninstallDeleteEntries { get; internal set; } = [];

	/// <summary>运行条目表（[Run] 段）。</summary>
	public IReadOnlyList<InnoRunEntry> RunEntries { get; internal set; } = [];

	/// <summary>卸载运行条目表（[UninstallRun] 段）。</summary>
	public IReadOnlyList<InnoRunEntry> UninstallRunEntries { get; internal set; } = [];

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
				throw lastError;
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
		ValidateCounts(headerRaw);

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

		// messages / permissions / types / components / tasks
		info.Messages = ParseEntries(blockReader, version, headerRaw.MessageCount,
			(r, v) => InnoMessageEntry.Parse(r, v, codepage, languages));
		info.Permissions = ParseEntries(blockReader, version, headerRaw.PermissionCount, InnoPermissionEntry.Parse);
		info.Types = ParseEntries(blockReader, version, headerRaw.TypeCount, (r, v) => InnoTypeEntry.Parse(r, v, codepage));
		info.Components = ParseEntries(blockReader, version, headerRaw.ComponentCount, (r, v) => InnoComponentEntry.Parse(r, v, codepage));
		info.Tasks = ParseEntries(blockReader, version, headerRaw.TaskCount, (r, v) => InnoTaskEntry.Parse(r, v, codepage));

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

		// icons / ini / registry / delete / uninstall delete / run / uninstall run
		info.Icons = ParseEntries(blockReader, version, headerRaw.IconCount, (r, v) => InnoIconEntry.Parse(r, v, codepage, leadBytes));
		info.IniEntries = ParseEntries(blockReader, version, headerRaw.IniEntryCount, (r, v) => InnoIniEntry.Parse(r, v, codepage, leadBytes));
		info.RegistryEntries = ParseEntries(blockReader, version, headerRaw.RegistryEntryCount, (r, v) => InnoRegistryEntry.Parse(r, v, codepage, leadBytes));
		info.DeleteEntries = ParseEntries(blockReader, version, headerRaw.DeleteEntryCount, (r, v) => InnoDeleteEntry.Parse(r, v, codepage, leadBytes));
		info.UninstallDeleteEntries = ParseEntries(blockReader, version, headerRaw.UninstallDeleteEntryCount, (r, v) => InnoDeleteEntry.Parse(r, v, codepage, leadBytes));
		info.RunEntries = ParseEntries(blockReader, version, headerRaw.RunEntryCount, (r, v) => InnoRunEntry.Parse(r, v, codepage, leadBytes));
		info.UninstallRunEntries = ParseEntries(blockReader, version, headerRaw.UninstallRunEntryCount, (r, v) => InnoRunEntry.Parse(r, v, codepage, leadBytes));

		// 向导图片与辅助 DLL
		SkipWizardImages(blockReader, version, info.Header);

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

	/// <summary>防止损坏/恶意头部的条目计数导致过量内存分配。</summary>
	static private void ValidateCounts(InnoHeader.Raw headerRaw) {
		ValidateCount(headerRaw.LanguageCount, nameof(headerRaw.LanguageCount));
		ValidateCount(headerRaw.MessageCount, nameof(headerRaw.MessageCount));
		ValidateCount(headerRaw.PermissionCount, nameof(headerRaw.PermissionCount));
		ValidateCount(headerRaw.TypeCount, nameof(headerRaw.TypeCount));
		ValidateCount(headerRaw.ComponentCount, nameof(headerRaw.ComponentCount));
		ValidateCount(headerRaw.TaskCount, nameof(headerRaw.TaskCount));
		ValidateCount(headerRaw.DirectoryCount, nameof(headerRaw.DirectoryCount));
		ValidateCount(headerRaw.IssigKeyCount, nameof(headerRaw.IssigKeyCount));
		ValidateCount(headerRaw.FileCount, nameof(headerRaw.FileCount));
		ValidateCount(headerRaw.IconCount, nameof(headerRaw.IconCount));
		ValidateCount(headerRaw.IniEntryCount, nameof(headerRaw.IniEntryCount));
		ValidateCount(headerRaw.RegistryEntryCount, nameof(headerRaw.RegistryEntryCount));
		ValidateCount(headerRaw.DeleteEntryCount, nameof(headerRaw.DeleteEntryCount));
		ValidateCount(headerRaw.UninstallDeleteEntryCount, nameof(headerRaw.UninstallDeleteEntryCount));
		ValidateCount(headerRaw.RunEntryCount, nameof(headerRaw.RunEntryCount));
		ValidateCount(headerRaw.UninstallRunEntryCount, nameof(headerRaw.UninstallRunEntryCount));
		ValidateCount(headerRaw.DataEntryCount, nameof(headerRaw.DataEntryCount));
	}

	static private void ValidateCount(int count, string name) {
		const int maxEntryCount = 10_000_000; // 远超真实安装包的条目数（通常 < 20 万）
		if (count < 0 || count > maxEntryCount) {
			throw new InnoFormatException($"头部字段 {name} 无效：{count}");
		}
	}

	static private List<T> ParseEntries<T>(
		InnoBinaryReader reader,
		InnoVersion version,
		int count,
		Func<InnoBinaryReader, InnoVersion, T> parser) {
		List<T> entries = new(count);
		for (var i = 0; i < count; i++) {
			entries.Add(parser(reader, version));
		}
		return entries;
	}

	static private void SkipWizardImages(InnoBinaryReader reader, InnoVersion version, InnoHeader header) {
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
			reader.Skip(wlen);
		}
	}

}