using InnoUnpack.NET.Reading;

namespace InnoUnpack.NET.Metadata;

/// <summary>
///     文件条目（对应 TSetupFileEntry）。
/// </summary>
public sealed class InnoFileEntry {
	/// <summary>源文件名（不含路径）。</summary>
	public string Source { get; internal set; } = string.Empty;

	/// <summary>目标路径（可能包含 {app} 等常量，使用 Windows 风格分隔符）。</summary>
	public string Destination { get; internal set; } = string.Empty;

	/// <summary>安装字体名。</summary>
	public string InstallFontName { get; internal set; } = string.Empty;

	/// <summary>强名称程序集名（5.2.5+）。</summary>
	public string StrongAssemblyName { get; internal set; } = string.Empty;

	/// <summary>排除表达式（6.5.0+）。</summary>
	public string Excludes { get; internal set; } = string.Empty;

	/// <summary>ISSig 下载源（6.5.0+）。</summary>
	public string DownloadSource { get; internal set; } = string.Empty;

	/// <summary>ISSig 下载用户名（6.5.0+）。</summary>
	public string DownloadUser { get; internal set; } = string.Empty;

	/// <summary>ISSig 下载密码（6.5.0+）。</summary>
	public string DownloadPassword { get; internal set; } = string.Empty;

	/// <summary>解压归档密码（6.5.0+）。</summary>
	public string ArchivePassword { get; internal set; } = string.Empty;

	/// <summary>组件条件（用分号分隔的表达式）。</summary>
	public string Components { get; internal set; } = string.Empty;

	/// <summary>任务条件。</summary>
	public string Tasks { get; internal set; } = string.Empty;

	/// <summary>语言条件。</summary>
	public string Languages { get; internal set; } = string.Empty;

	/// <summary>Check 函数条件。</summary>
	public string Check { get; internal set; } = string.Empty;

	/// <summary>AfterInstall 函数。</summary>
	public string AfterInstall { get; internal set; } = string.Empty;

	/// <summary>BeforeInstall 函数。</summary>
	public string BeforeInstall { get; internal set; } = string.Empty;

	/// <summary>所需最小 Windows 版本。</summary>
	public InnoWindowsVersionRange MinVersion { get; internal set; }

	/// <summary>指向数据条目表（<see cref="InnoSetupInfo.DataEntries" />）的索引。</summary>
	public int Location { get; internal set; } = -1;

	/// <summary>文件属性。</summary>
	public uint Attributes { get; internal set; }

	/// <summary>外部文件大小。</summary>
	public ulong ExternalSize { get; internal set; }

	/// <summary>权限（POSIX 风格，-1 表示未指定）。</summary>
	public short Permission { get; internal set; } = -1;

	/// <summary>选项标志。</summary>
	public InnoFileOptions Options { get; internal set; }

	/// <summary>文件类型。</summary>
	public InnoFileType Type { get; internal set; } = InnoFileType.UserFile;

	/// <summary>文件验证方式（6.5.0+）。</summary>
	public InnoFileVerification Verification { get; internal set; } = InnoFileVerification.None;

	/// <summary>ISSig 校验和（6.5.0+，SHA-256）。</summary>
	public InnoChecksum Checksum { get; internal set; } = InnoChecksum.None;

	/// <summary>内部：解析文件条目。</summary>
	internal static InnoFileEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage, bool[]? leadBytes) {
		InnoHeader.VersionGates v = new(version);
		InnoFileEntry entry = new() {
			Source = ReadString(reader, codepage, leadBytes),
			Destination = ReadString(reader, codepage, leadBytes),
			InstallFontName = ReadString(reader, codepage, leadBytes)
		};

		if (v.Ge525) {
			entry.StrongAssemblyName = ReadString(reader, codepage, leadBytes);
		}

		// 条件数据
		if (v.Ge200) {
			entry.Components = ReadString(reader, codepage);
			entry.Tasks = ReadString(reader, codepage);
		}

		if (v.Ge401) {
			entry.Languages = ReadString(reader, codepage);
		}

		if (v.Ge400) {
			entry.Check = ReadString(reader, codepage);
		}

		if (v.Ge410) {
			entry.AfterInstall = ReadString(reader, codepage);
			entry.BeforeInstall = ReadString(reader, codepage);
		}

		// 6.5.0+：新增字符串与验证块
		if (v.Ge650) {
			entry.Excludes = ReadString(reader, codepage, leadBytes);
			entry.DownloadSource = ReadString(reader, codepage, leadBytes);
			entry.DownloadUser = ReadString(reader, codepage, leadBytes);
			entry.DownloadPassword = ReadString(reader, codepage, leadBytes);
			entry.ArchivePassword = ReadString(reader, codepage, leadBytes);
			_ = reader.ReadStringBytes(); // ISSigAllowedKeys（ANSI 字符串）
			var sha256 = reader.ReadBytes(32);
			var verification = reader.ReadByte();
			entry.Checksum = new(InnoChecksumType.Sha256, sha256);
			entry.Verification = (InnoFileVerification)verification;
		}

		// 版本数据（winver：build u16 + minor u8 + major u8，各两组 + service pack 2 字节）
		entry.MinVersion = ReadWindowsVersionRange(reader);

		entry.Location = reader.ReadInt32();
		entry.Attributes = reader.ReadUInt32();
		entry.ExternalSize = v.Ge400 ? reader.ReadUInt64() : reader.ReadUInt32();

		if (v.Ge410) {
			entry.Permission = unchecked((short)reader.ReadUInt16());
		}

		// 7.0.0.3+：目标架构改为单独的字节枚举（位于 Options 之前）
		if (v.Ge7003) {
			var bitness = reader.ReadByte();
			if (bitness == 1) {
				entry.Options |= InnoFileOptions.Bits32;
			} else if (bitness == 2) {
				entry.Options |= InnoFileOptions.Bits64;
			}
		}

		FlagReader flags = new(reader);
		flags.Add(0); // ConfirmOverwrite
		flags.Add(1); // NeverUninstall
		flags.Add(2); // RestartReplace
		flags.Add(3); // DeleteAfterInstall
		flags.Add(4); // RegisterServer
		flags.Add(5); // RegisterTypeLib
		flags.Add(6); // SharedFile
		flags.Add(7); // CompareTimeStamp
		flags.Add(8); // FontIsNotTrueType
		if (v.Ge125) flags.Add(9); // SkipIfSourceDoesntExist
		if (v.Ge126) flags.Add(10); // OverwriteReadOnly
		if (v.Ge1321) flags.Add(11); // OverwriteSameVersion
		if (v.Ge1321) flags.Add(12); // CustomDestName
		if (v.Ge1325) flags.Add(13); // OnlyIfDestFileExists
		if (v.Ge205) flags.Add(14); // NoRegError
		if (v.Ge301) flags.Add(15); // UninsRestartDelete
		if (v.Ge305) flags.Add(16); // OnlyIfDoesntExist
		if (v.Ge305) flags.Add(17); // IgnoreVersion
		if (v.Ge305) flags.Add(18); // PromptIfOlder
		if (v.Ge400) flags.Add(19); // DontCopy
		if (v.Ge405) flags.Add(20); // UninsRemoveReadOnly
		if (v.Ge418) flags.Add(21); // RecurseSubDirsExternal
		if (v.Ge421) flags.Add(22); // ReplaceSameVersionIfContentsDiffer
		if (v.Ge425) flags.Add(23); // DontVerifyChecksum
		if (v.Ge503) flags.Add(24); // UninsNoSharedFilePrompt
		if (v.Ge510) flags.Add(25); // CreateAllSubDirs
		if (v is { Ge512: true, Lt7003: true }) {
			flags.Add(26); // Bits32
			flags.Add(27); // Bits64
		}

		if (v.Ge520) {
			flags.Add(28); // ExternalSizePreset
			flags.Add(29); // SetNtfsCompression
			flags.Add(30); // UnsetNtfsCompression
		}

		if (v.Ge525) flags.Add(31); // GacInstall
		if (v.Ge650) {
			flags.Add(32); // Download
			flags.Add(33); // ExtractArchive
		}

		if (v.Ge670) {
			flags.DiscardPaddingTo(8); // 标志集固定 8 字节
		}

		entry.Options |= (InnoFileOptions)flags.GetResult();

		if (v.Ge500) {
			entry.Type = (InnoFileType)reader.ReadByte();
		} else if (v.Ge400) {
			var type = reader.ReadByte();
			entry.Type = type switch {
				0 => InnoFileType.UserFile, 1 => InnoFileType.UninstallerExe, _ => InnoFileType.RegServerExe
			};
		}

		return entry;
	}

	static private string ReadString(InnoBinaryReader reader, int codepage, bool[]? leadBytes = null) {
		return InnoStringDecoder.Decode(reader.ReadStringBytes(), codepage, leadBytes);
	}

	/// <summary>读取完整的 windows_version_range（每侧：win_version + nt_version + nt_service_pack）。</summary>
	static private InnoWindowsVersionRange ReadWindowsVersionRange(InnoBinaryReader reader) {
		InnoWindowsVersion ReadOne() {
			var build = reader.ReadUInt16();
			var minor = reader.ReadByte();
			var major = reader.ReadByte();
			_ = reader.ReadUInt16(); // nt_version build
			_ = reader.ReadByte(); // nt_version minor
			_ = reader.ReadByte(); // nt_version major
			_ = reader.ReadUInt16(); // nt_service_pack（major+minor）
			return new(major, minor, build);
		}

		return new(ReadOne(), ReadOne());
	}

	/// <summary>内部：按位读取的标志集（Delphi set 布局：3 字节时补齐为 4 字节）。</summary>
	sealed private class FlagReader(InnoBinaryReader reader) {
		private int _bits;
		private byte _current;
		private ulong _flags;

		public void Add(int flagIndex) {
			if ((_bits & 7) == 0) {
				_current = reader.ReadByte();
			}

			if ((_current & 1 << (_bits & 7)) != 0) {
				_flags |= 1UL << flagIndex;
			}

			_bits++;
		}

		/// <summary>将已读取位总数补足到指定字节数（跳过填充字节）。</summary>
		public void DiscardPaddingTo(int targetBytes) {
			while (_bits < targetBytes * 8) {
				if ((_bits & 7) == 0) {
					_ = reader.ReadByte();
				}

				_bits++;
			}
		}

		/// <summary>返回标志值（3 字节标志集按 Delphi set 布局补齐为 4 字节，与 innoextract 一致）。</summary>
		public ulong GetResult() {
			if (_bits is > 16 and <= 24) {
				_ = reader.ReadByte();
			}

			return _flags;
		}
	}
}