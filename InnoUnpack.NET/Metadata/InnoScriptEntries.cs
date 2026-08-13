using InnoUnpack.NET.Reading;

namespace InnoUnpack.NET.Metadata;

/// <summary>setup 类型（[Types] 段，4.0.3+ 记录类型字节）。</summary>
public enum InnoSetupTypeKind {
	User = 0,
	DefaultFull = 1,
	DefaultCompact = 2,
	DefaultCustom = 3
}

/// <summary>组件选项标志（对应 TSetupComponentEntry.Flags）。</summary>
[Flags]
public enum InnoComponentOptions {
	None = 0,
	Fixed = 1 << 0,
	Restart = 1 << 1,
	DisableNoUninstallWarning = 1 << 2,
	Exclusive = 1 << 3,
	DontInheritCheck = 1 << 4
}

/// <summary>任务选项标志（对应 TSetupTaskEntry.Flags）。</summary>
[Flags]
public enum InnoTaskOptions {
	None = 0,
	Exclusive = 1 << 0,
	Unchecked = 1 << 1,
	Restart = 1 << 2,
	CheckedOnce = 1 << 3,
	DontInheritCheck = 1 << 4
}

/// <summary>图标选项标志（对应 TSetupIconEntry.Flags）。</summary>
[Flags]
public enum InnoIconOptions {
	None = 0,
	NeverUninstall = 1 << 0,
	CreateOnlyIfFileExists = 1 << 1,
	UseAppPaths = 1 << 2,
	FolderShortcut = 1 << 3,
	ExcludeFromShowInNewInstall = 1 << 4,
	PreventPinning = 1 << 5,
	HasAppUserModelToastActivatorClsid = 1 << 6,
	/// <summary>仅 1.3.26 之前存在（4.0+ 安装包恒为 0）。</summary>
	RunMinimized = 1 << 7
}

/// <summary>图标关闭设置。</summary>
public enum InnoIconCloseSetting {
	NoSetting = 0,
	CloseOnExit = 1,
	DontCloseOnExit = 2
}

/// <summary>INI 条目选项标志。</summary>
[Flags]
public enum InnoIniOptions {
	None = 0,
	CreateKeyIfDoesntExist = 1 << 0,
	UninsDeleteEntry = 1 << 1,
	UninsDeleteEntireSection = 1 << 2,
	UninsDeleteSectionIfEmpty = 1 << 3,
	HasValue = 1 << 4
}

/// <summary>注册表条目选项标志。</summary>
[Flags]
public enum InnoRegistryOptions {
	None = 0,
	CreateValueIfDoesntExist = 1 << 0,
	UninsDeleteValue = 1 << 1,
	UninsClearValue = 1 << 2,
	UninsDeleteEntireKey = 1 << 3,
	UninsDeleteEntireKeyIfEmpty = 1 << 4,
	PreserveStringType = 1 << 5,
	DeleteKey = 1 << 6,
	DeleteValue = 1 << 7,
	NoError = 1 << 8,
	DontCreateKey = 1 << 9,
	Bits32 = 1 << 10,
	Bits64 = 1 << 11
}

/// <summary>注册表根键（hive）。</summary>
public enum InnoRegistryHive {
	Hkcr = 0,
	Hkcu = 1,
	Hklm = 2,
	Hku = 3,
	Hkpd = 4,
	Hkcc = 5,
	Hkdd = 6,
	Unset = 7
}

/// <summary>注册表值类型。</summary>
public enum InnoRegistryValueType {
	None = 0,
	String = 1,
	ExpandString = 2,
	DWord = 3,
	Binary = 4,
	MultiString = 5,
	QWord = 6
}

/// <summary>删除条目的目标类型。</summary>
public enum InnoDeleteTargetType {
	Files = 0,
	FilesAndSubdirs = 1,
	DirIfEmpty = 2
}

/// <summary>运行条目选项标志。</summary>
[Flags]
public enum InnoRunOptions {
	None = 0,
	ShellExec = 1 << 0,
	SkipIfDoesntExist = 1 << 1,
	PostInstall = 1 << 2,
	Unchecked = 1 << 3,
	SkipIfSilent = 1 << 4,
	SkipIfNotSilent = 1 << 5,
	HideWizard = 1 << 6,
	Bits32 = 1 << 7,
	Bits64 = 1 << 8,
	RunAsOriginalUser = 1 << 9,
	DontLogParameters = 1 << 10,
	LogOutput = 1 << 11
}

/// <summary>运行条目的等待条件。</summary>
public enum InnoRunWaitCondition {
	WaitUntilTerminated = 0,
	NoWait = 1,
	WaitUntilIdle = 2
}

/// <summary>
///     自定义消息条目（[CustomMessages] 段，4.2.1+）。
/// </summary>
public sealed class InnoMessageEntry {
	/// <summary>消息名（安装包代码页解码）。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>消息值（按所属语言的代码页解码）。</summary>
	public string Value { get; internal set; } = string.Empty;

	/// <summary>所属语言在语言表（<see cref="InnoSetupInfo.Languages" />）中的索引，-1 表示默认。</summary>
	public int Language { get; internal set; }

	internal static InnoMessageEntry Parse(
		InnoBinaryReader reader,
		InnoVersion version,
		int codepage,
		IReadOnlyList<InnoLanguageEntry> languages) {
		InnoMessageEntry entry = new() {
			Name = InnoEntryReader.ReadString(reader, codepage)
		};

		var valueRaw = reader.ReadStringBytes();
		entry.Language = reader.ReadInt32();

		int valueCodepage;
		if (entry.Language < 0) {
			valueCodepage = codepage;
		} else if (entry.Language >= languages.Count) {
			// 索引越界：与 innoextract 一致，值置空
			entry.Value = string.Empty;
			return entry;
		} else {
			valueCodepage = (int)languages[entry.Language].Codepage;
		}

		entry.Value = InnoStringDecoder.Decode(valueRaw, valueCodepage);
		return entry;
	}
}

/// <summary>
///     权限条目（[Permissions] 段，4.1.0+）。内容为序列化的 TGrantPermissionEntry 数组。
/// </summary>
public sealed class InnoPermissionEntry {
	/// <summary>序列化的 TGrantPermissionEntry 数组（原始字节）。</summary>
	public byte[] Permissions { get; internal set; } = [];

	internal static InnoPermissionEntry Parse(InnoBinaryReader reader, InnoVersion version) {
		return new() { Permissions = reader.ReadStringBytes() };
	}
}

/// <summary>
///     setup 类型条目（[Types] 段）。
/// </summary>
public sealed class InnoTypeEntry {
	/// <summary>类型名。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>描述。</summary>
	public string Description { get; internal set; } = string.Empty;

	/// <summary>语言条件（4.0.1+）。</summary>
	public string Languages { get; internal set; } = string.Empty;

	/// <summary>Check 函数条件。</summary>
	public string Check { get; internal set; } = string.Empty;

	/// <summary>所需最小 Windows 版本。</summary>
	public InnoWindowsVersionRange MinVersion { get; internal set; }

	/// <summary>是否为自定义类型。</summary>
	public bool CustomType { get; internal set; }

	/// <summary>内置类型（4.0.3+，否则为 <see cref="InnoSetupTypeKind.User" />）。</summary>
	public InnoSetupTypeKind Type { get; internal set; } = InnoSetupTypeKind.User;

	/// <summary>类型大小（字节）。</summary>
	public ulong Size { get; internal set; }

	internal static InnoTypeEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage) {
		InnoHeader.VersionGates v = new(version);
		InnoTypeEntry entry = new() {
			Name = InnoEntryReader.ReadString(reader, codepage),
			Description = InnoEntryReader.ReadString(reader, codepage)
		};

		if (v.Ge401) {
			entry.Languages = InnoEntryReader.ReadString(reader, codepage);
		}

		if (v.Ge400) {
			entry.Check = InnoEntryReader.ReadString(reader, codepage);
		}

		entry.MinVersion = InnoEntryReader.ReadWindowsVersionRange(reader);
		entry.CustomType = reader.ReadByte() != 0; // options 字节（CustomSetupType 标志）
		if (v.Ge403) {
			entry.Type = (InnoSetupTypeKind)reader.ReadByte();
		}

		entry.Size = v.Ge400 ? reader.ReadUInt64() : reader.ReadUInt32();
		return entry;
	}
}

/// <summary>
///     组件条目（[Components] 段）。
/// </summary>
public sealed class InnoComponentEntry {
	/// <summary>组件名。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>描述。</summary>
	public string Description { get; internal set; } = string.Empty;

	/// <summary>类型条件。</summary>
	public string Types { get; internal set; } = string.Empty;

	/// <summary>语言条件（4.0.1+）。</summary>
	public string Languages { get; internal set; } = string.Empty;

	/// <summary>Check 函数条件。</summary>
	public string Check { get; internal set; } = string.Empty;

	/// <summary>额外磁盘空间要求（字节）。</summary>
	public ulong ExtraDiskSpaceRequired { get; internal set; }

	/// <summary>层级。</summary>
	public int Level { get; internal set; }

	/// <summary>是否已启用。</summary>
	public bool Used { get; internal set; }

	/// <summary>所需最小 Windows 版本。</summary>
	public InnoWindowsVersionRange MinVersion { get; internal set; }

	/// <summary>选项标志。</summary>
	public InnoComponentOptions Options { get; internal set; }

	/// <summary>组件大小（字节）。</summary>
	public ulong Size { get; internal set; }

	internal static InnoComponentEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage) {
		InnoHeader.VersionGates v = new(version);
		InnoComponentEntry entry = new() {
			Name = InnoEntryReader.ReadString(reader, codepage),
			Description = InnoEntryReader.ReadString(reader, codepage),
			Types = InnoEntryReader.ReadString(reader, codepage)
		};

		if (v.Ge401) {
			entry.Languages = InnoEntryReader.ReadString(reader, codepage);
		}

		if (v.Ge400) {
			entry.Check = InnoEntryReader.ReadString(reader, codepage);
		}

		entry.ExtraDiskSpaceRequired = v.Ge400 ? reader.ReadUInt64() : reader.ReadUInt32();

		if (v.Ge400) {
			entry.Level = v.Ge670 ? reader.ReadByte() : reader.ReadInt32();
			entry.Used = reader.ReadByte() != 0;
		}

		entry.MinVersion = InnoEntryReader.ReadWindowsVersionRange(reader);
		entry.Options = (InnoComponentOptions)reader.ReadByte();
		entry.Size = v.Ge400 ? reader.ReadUInt64() : (v.Ge200 ? reader.ReadUInt32() : 0);
		return entry;
	}
}

/// <summary>
///     任务条目（[Tasks] 段）。
/// </summary>
public sealed class InnoTaskEntry {
	/// <summary>任务名。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>描述。</summary>
	public string Description { get; internal set; } = string.Empty;

	/// <summary>分组描述。</summary>
	public string GroupDescription { get; internal set; } = string.Empty;

	/// <summary>组件条件。</summary>
	public string Components { get; internal set; } = string.Empty;

	/// <summary>语言条件（4.0.1+）。</summary>
	public string Languages { get; internal set; } = string.Empty;

	/// <summary>Check 函数条件。</summary>
	public string Check { get; internal set; } = string.Empty;

	/// <summary>层级。</summary>
	public int Level { get; internal set; }

	/// <summary>是否已启用。</summary>
	public bool Used { get; internal set; }

	/// <summary>所需最小 Windows 版本。</summary>
	public InnoWindowsVersionRange MinVersion { get; internal set; }

	/// <summary>选项标志。</summary>
	public InnoTaskOptions Options { get; internal set; }

	internal static InnoTaskEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage) {
		InnoHeader.VersionGates v = new(version);
		InnoTaskEntry entry = new() {
			Name = InnoEntryReader.ReadString(reader, codepage),
			Description = InnoEntryReader.ReadString(reader, codepage),
			GroupDescription = InnoEntryReader.ReadString(reader, codepage),
			Components = InnoEntryReader.ReadString(reader, codepage)
		};

		if (v.Ge401) {
			entry.Languages = InnoEntryReader.ReadString(reader, codepage);
		}

		if (v.Ge400) {
			entry.Check = InnoEntryReader.ReadString(reader, codepage);
		}

		if (v.Ge400) {
			entry.Level = v.Ge670 ? reader.ReadByte() : reader.ReadInt32();
			entry.Used = reader.ReadByte() != 0;
		}

		entry.MinVersion = InnoEntryReader.ReadWindowsVersionRange(reader);
		entry.Options = (InnoTaskOptions)reader.ReadByte();
		return entry;
	}
}

/// <summary>
///     图标条目（[Icons] 段）。
/// </summary>
public sealed class InnoIconEntry {
	/// <summary>图标名。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>目标文件。</summary>
	public string Filename { get; internal set; } = string.Empty;

	/// <summary>参数。</summary>
	public string Parameters { get; internal set; } = string.Empty;

	/// <summary>工作目录。</summary>
	public string WorkingDirectory { get; internal set; } = string.Empty;

	/// <summary>图标文件。</summary>
	public string IconFile { get; internal set; } = string.Empty;

	/// <summary>注释。</summary>
	public string Comment { get; internal set; } = string.Empty;

	/// <summary>组件条件。</summary>
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

	/// <summary>App User Model ID（5.3.5+）。</summary>
	public string AppUserModelId { get; internal set; } = string.Empty;

	/// <summary>App User Model Toast Activator CLSID（6.1.0+）。</summary>
	public Guid ToastActivatorClsid { get; internal set; }

	/// <summary>所需最小 Windows 版本。</summary>
	public InnoWindowsVersionRange MinVersion { get; internal set; }

	/// <summary>图标索引。</summary>
	public int IconIndex { get; internal set; }

	/// <summary>显示命令。</summary>
	public int ShowCommand { get; internal set; }

	/// <summary>关闭设置。</summary>
	public InnoIconCloseSetting CloseOnExit { get; internal set; }

	/// <summary>快捷键。</summary>
	public ushort Hotkey { get; internal set; }

	/// <summary>选项标志。</summary>
	public InnoIconOptions Options { get; internal set; }

	internal static InnoIconEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage, bool[]? leadBytes) {
		InnoHeader.VersionGates v = new(version);
		InnoIconEntry entry = new() {
			Name = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			Filename = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			Parameters = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			WorkingDirectory = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			IconFile = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			Comment = InnoEntryReader.ReadString(reader, codepage)
		};

		var conditions = InnoEntryReader.ReadConditions(reader, version, codepage);
		entry.Components = conditions.Components;
		entry.Tasks = conditions.Tasks;
		entry.Languages = conditions.Languages;
		entry.Check = conditions.Check;
		entry.AfterInstall = conditions.AfterInstall;
		entry.BeforeInstall = conditions.BeforeInstall;

		if (v.Ge535) {
			entry.AppUserModelId = InnoEntryReader.ReadString(reader, codepage);
		}

		if (v.Ge610) {
			entry.ToastActivatorClsid = new Guid(reader.ReadBytes(16));
		}

		entry.MinVersion = InnoEntryReader.ReadWindowsVersionRange(reader);
		entry.IconIndex = reader.ReadInt32();
		// show_command / close_on_exit / hotkey 引入版本均 < 4.0，对受支持的安装包恒存在
		entry.ShowCommand = reader.ReadInt32();
		entry.CloseOnExit = (InnoIconCloseSetting)reader.ReadByte();
		entry.Hotkey = reader.ReadUInt16();

		FlagReader flags = new(reader);
		flags.Add(0); // NeverUninstall
		// RunMinimized 仅在 < 1.3.26 存在（4.0+ 跳过）
		flags.Add(1); // CreateOnlyIfFileExists
		flags.Add(2); // UseAppPaths
		if (v.Ge503 && !v.Ge630) {
			flags.Add(3); // FolderShortcut
		}

		if (v.Ge542) {
			flags.Add(4); // ExcludeFromShowInNewInstall
		}

		if (v.Ge550) {
			flags.Add(5); // PreventPinning
		}

		if (v.Ge610) {
			flags.Add(6); // HasAppUserModelToastActivatorCLSID
		}

		entry.Options = (InnoIconOptions)flags.GetResult();
		return entry;
	}
}

/// <summary>
///     INI 条目（[INI] 段）。
/// </summary>
public sealed class InnoIniEntry {
	/// <summary>INI 文件（空时默认为 {windows}/WIN.INI）。</summary>
	public string IniFile { get; internal set; } = string.Empty;

	/// <summary>节名。</summary>
	public string Section { get; internal set; } = string.Empty;

	/// <summary>键名。</summary>
	public string Key { get; internal set; } = string.Empty;

	/// <summary>值。</summary>
	public string Value { get; internal set; } = string.Empty;

	/// <summary>组件条件。</summary>
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

	/// <summary>选项标志。</summary>
	public InnoIniOptions Options { get; internal set; }

	internal static InnoIniEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage, bool[]? leadBytes) {
		InnoIniEntry entry = new() {
			IniFile = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			Section = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			Key = InnoEntryReader.ReadString(reader, codepage),
			Value = InnoEntryReader.ReadString(reader, codepage, leadBytes)
		};

		if (entry.IniFile.Length == 0) {
			entry.IniFile = "{windows}/WIN.INI";
		}

		var conditions = InnoEntryReader.ReadConditions(reader, version, codepage);
		entry.Components = conditions.Components;
		entry.Tasks = conditions.Tasks;
		entry.Languages = conditions.Languages;
		entry.Check = conditions.Check;
		entry.AfterInstall = conditions.AfterInstall;
		entry.BeforeInstall = conditions.BeforeInstall;

		entry.MinVersion = InnoEntryReader.ReadWindowsVersionRange(reader);
		entry.Options = (InnoIniOptions)reader.ReadByte();
		return entry;
	}
}

/// <summary>
///     注册表条目（[Registry] 段）。
/// </summary>
public sealed class InnoRegistryEntry {
	/// <summary>注册表键路径。</summary>
	public string Key { get; internal set; } = string.Empty;

	/// <summary>值名（空表示默认值）。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>值（原始字节，按 <see cref="Type" /> 解释）。</summary>
	public byte[] Value { get; internal set; } = [];

	/// <summary>组件条件。</summary>
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

	/// <summary>序列化的权限数组（仅 4.0.11–4.1.0，原始字节）。</summary>
	public byte[] Permissions { get; internal set; } = [];

	/// <summary>所需最小 Windows 版本。</summary>
	public InnoWindowsVersionRange MinVersion { get; internal set; }

	/// <summary>注册表根键。</summary>
	public InnoRegistryHive Hive { get; internal set; }

	/// <summary>权限在权限表（<see cref="InnoSetupInfo.Permissions" />）中的索引，-1 表示未指定。</summary>
	public short Permission { get; internal set; } = -1;

	/// <summary>值类型。</summary>
	public InnoRegistryValueType Type { get; internal set; }

	/// <summary>选项标志。</summary>
	public InnoRegistryOptions Options { get; internal set; }

	internal static InnoRegistryEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage, bool[]? leadBytes) {
		InnoHeader.VersionGates v = new(version);
		InnoRegistryEntry entry = new() {
			Key = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			Name = InnoEntryReader.ReadString(reader, codepage),
			Value = reader.ReadStringBytes()
		};

		var conditions = InnoEntryReader.ReadConditions(reader, version, codepage);
		entry.Components = conditions.Components;
		entry.Tasks = conditions.Tasks;
		entry.Languages = conditions.Languages;
		entry.Check = conditions.Check;
		entry.AfterInstall = conditions.AfterInstall;
		entry.BeforeInstall = conditions.BeforeInstall;

		if (v.Ge4011 && v.Lt410) {
			entry.Permissions = reader.ReadStringBytes();
		}

		entry.MinVersion = InnoEntryReader.ReadWindowsVersionRange(reader);
		entry.Hive = (InnoRegistryHive)(reader.ReadUInt32() & ~0x80000000u);
		if (v.Ge410) {
			entry.Permission = unchecked((short)reader.ReadUInt16());
		}

		entry.Type = (InnoRegistryValueType)reader.ReadByte();

		// 7.0.0.3+：位宽改为独立字节（位于选项标志之前）
		if (v.Ge7003) {
			var bitness = reader.ReadByte();
			if (bitness == 1) {
				entry.Options |= InnoRegistryOptions.Bits32;
			} else if (bitness == 2) {
				entry.Options |= InnoRegistryOptions.Bits64;
			}
		}

		FlagReader flags = new(reader);
		flags.Add(0); // CreateValueIfDoesntExist
		flags.Add(1); // UninsDeleteValue
		flags.Add(2); // UninsClearValue
		flags.Add(3); // UninsDeleteEntireKey
		flags.Add(4); // UninsDeleteEntireKeyIfEmpty
		// 以下标志在 4.0+ 恒存在（引入版本均 < 4.0）
		flags.Add(5); // PreserveStringType
		flags.Add(6); // DeleteKey
		flags.Add(7); // DeleteValue
		flags.Add(8); // NoError
		flags.Add(9); // DontCreateKey
		if (v.Ge510 && !v.Ge7003) {
			flags.Add(10); // Bits32
			flags.Add(11); // Bits64
		}

		entry.Options |= (InnoRegistryOptions)flags.GetResult();
		return entry;
	}
}

/// <summary>
///     删除条目（[InstallDelete] / [UninstallDelete] 段）。
/// </summary>
public sealed class InnoDeleteEntry {
	/// <summary>目标路径。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>组件条件。</summary>
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

	/// <summary>删除目标类型。</summary>
	public InnoDeleteTargetType Type { get; internal set; }

	internal static InnoDeleteEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage, bool[]? leadBytes) {
		InnoDeleteEntry entry = new() {
			Name = InnoEntryReader.ReadString(reader, codepage, leadBytes)
		};

		var conditions = InnoEntryReader.ReadConditions(reader, version, codepage);
		entry.Components = conditions.Components;
		entry.Tasks = conditions.Tasks;
		entry.Languages = conditions.Languages;
		entry.Check = conditions.Check;
		entry.AfterInstall = conditions.AfterInstall;
		entry.BeforeInstall = conditions.BeforeInstall;

		entry.MinVersion = InnoEntryReader.ReadWindowsVersionRange(reader);
		entry.Type = (InnoDeleteTargetType)reader.ReadByte();
		return entry;
	}
}

/// <summary>
///     运行条目（[Run] / [UninstallRun] 段）。
/// </summary>
public sealed class InnoRunEntry {
	/// <summary>程序文件名。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>参数。</summary>
	public string Parameters { get; internal set; } = string.Empty;

	/// <summary>工作目录。</summary>
	public string WorkingDirectory { get; internal set; } = string.Empty;

	/// <summary>RunOnce ID（1.3.9+）。</summary>
	public string RunOnceId { get; internal set; } = string.Empty;

	/// <summary>状态消息（2.0.2+）。</summary>
	public string StatusMessage { get; internal set; } = string.Empty;

	/// <summary>Verb（5.1.13+）。</summary>
	public string Verb { get; internal set; } = string.Empty;

	/// <summary>描述。</summary>
	public string Description { get; internal set; } = string.Empty;

	/// <summary>组件条件。</summary>
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

	/// <summary>日志记录开关（7.0.0.1+）。</summary>
	public string OnLog { get; internal set; } = string.Empty;

	/// <summary>所需最小 Windows 版本。</summary>
	public InnoWindowsVersionRange MinVersion { get; internal set; }

	/// <summary>显示命令。</summary>
	public int ShowCommand { get; internal set; }

	/// <summary>等待条件。</summary>
	public InnoRunWaitCondition Wait { get; internal set; }

	/// <summary>选项标志。</summary>
	public InnoRunOptions Options { get; internal set; }

	internal static InnoRunEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage, bool[]? leadBytes) {
		InnoHeader.VersionGates v = new(version);
		InnoRunEntry entry = new() {
			Name = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			Parameters = InnoEntryReader.ReadString(reader, codepage, leadBytes),
			WorkingDirectory = InnoEntryReader.ReadString(reader, codepage, leadBytes)
		};

		// run_once_id / status_message / description 引入版本均 < 4.0，对受支持的安装包恒存在
		entry.RunOnceId = InnoEntryReader.ReadString(reader, codepage);
		entry.StatusMessage = InnoEntryReader.ReadString(reader, codepage);
		if (v.Ge5113) {
			entry.Verb = InnoEntryReader.ReadString(reader, codepage);
		}

		entry.Description = InnoEntryReader.ReadString(reader, codepage);

		var conditions = InnoEntryReader.ReadConditions(reader, version, codepage);
		entry.Components = conditions.Components;
		entry.Tasks = conditions.Tasks;
		entry.Languages = conditions.Languages;
		entry.Check = conditions.Check;
		entry.AfterInstall = conditions.AfterInstall;
		entry.BeforeInstall = conditions.BeforeInstall;

		if (v.Ge7001) {
			entry.OnLog = InnoEntryReader.ReadString(reader, codepage);
		}

		entry.MinVersion = InnoEntryReader.ReadWindowsVersionRange(reader);
		entry.ShowCommand = reader.ReadInt32();
		entry.Wait = (InnoRunWaitCondition)reader.ReadByte();

		// 7.0.0.3+：位宽改为独立字节
		if (v.Ge7003) {
			var bitness = reader.ReadByte();
			if (bitness == 1) {
				entry.Options |= InnoRunOptions.Bits32;
			} else if (bitness == 2) {
				entry.Options |= InnoRunOptions.Bits64;
			}
		}

		FlagReader flags = new(reader);
		// ShellExec / SkipIfDoesntExist / PostInstall / Unchecked / SkipIfSilent / SkipIfNotSilent / HideWizard
		// 均在 4.0+ 恒存在（引入版本 < 4.0）
		flags.Add(0); // ShellExec
		flags.Add(1); // SkipIfDoesntExist
		flags.Add(2); // PostInstall
		flags.Add(3); // Unchecked
		flags.Add(4); // SkipIfSilent
		flags.Add(5); // SkipIfNotSilent
		flags.Add(6); // HideWizard
		if (v.Ge5110 && !v.Ge7003) {
			flags.Add(7); // Bits32
			flags.Add(8); // Bits64
		}

		if (v.Ge520) {
			flags.Add(9); // RunAsOriginalUser
		}

		if (v.Ge610) {
			flags.Add(10); // DontLogParameters
		}

		if (v.Ge630) {
			flags.Add(11); // LogOutput
		}

		entry.Options |= (InnoRunOptions)flags.GetResult();
		return entry;
	}
}

/// <summary>条目条件字符串（components/tasks/languages/check/after_install/before_install）。</summary>
internal readonly record struct InnoConditions(
	string Components,
	string Tasks,
	string Languages,
	string Check,
	string AfterInstall,
	string BeforeInstall);

/// <summary>条目解析辅助方法。</summary>
internal static class InnoEntryReader {
	/// <summary>读取长度前缀字符串并按代码页解码。</summary>
	internal static string ReadString(InnoBinaryReader reader, int codepage, bool[]? leadBytes = null) =>
		InnoStringDecoder.Decode(reader.ReadStringBytes(), codepage, leadBytes);

	/// <summary>读取完整的 windows_version_range（每侧：win_version + nt_version + nt_service_pack）。</summary>
	internal static InnoWindowsVersionRange ReadWindowsVersionRange(InnoBinaryReader reader) {
		return new(ReadOne(), ReadOne());

		InnoWindowsVersion ReadOne() {
			var build = reader.ReadUInt16();
			var minor = reader.ReadByte();
			var major = reader.ReadByte();
			_ = reader.ReadUInt16(); // nt_version build
			_ = reader.ReadByte(); // nt_version minor
			_ = reader.ReadByte(); // nt_version major
			_ = reader.ReadUInt16(); // nt_service_pack
			return new(major, minor, build);
		}
	}

	/// <summary>读取条目条件字符串（与 innoextract 的 load_condition_data 一致）。</summary>
	internal static InnoConditions ReadConditions(InnoBinaryReader reader, InnoVersion version, int codepage) {
		InnoHeader.VersionGates v = new(version);
		return new(
			v.Ge200 ? ReadString(reader, codepage) : string.Empty,
			v.Ge200 ? ReadString(reader, codepage) : string.Empty,
			v.Ge401 ? ReadString(reader, codepage) : string.Empty,
			v.Ge400 ? ReadString(reader, codepage) : string.Empty,
			v.Ge410 ? ReadString(reader, codepage) : string.Empty,
			v.Ge410 ? ReadString(reader, codepage) : string.Empty);
	}
}

/// <summary>按位读取标志集（位从 LSB 到 MSB，单字节内）。</summary>
internal sealed class FlagReader(InnoBinaryReader reader) {
	private int _bits;
	private byte _current;
	private ulong _flags;

	/// <summary>读取下一位，若置位则将 <paramref name="flagIndex" /> 对应位写入结果。</summary>
	public void Add(int flagIndex) {
		if ((_bits & 7) == 0) {
			_current = reader.ReadByte();
		}

		if ((_current & 1 << (_bits & 7)) != 0) {
			_flags |= 1UL << flagIndex;
		}

		_bits++;
	}

	/// <summary>返回已读取的标志值。</summary>
	public ulong GetResult() { return _flags; }
}
