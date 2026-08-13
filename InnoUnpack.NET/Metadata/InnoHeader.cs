using InnoUnpack.NET.Reading;

namespace InnoUnpack.NET.Metadata;

/// <summary>Windows 版本信息（winver 字段）。</summary>
public readonly record struct InnoWindowsVersion(byte Major, byte Minor, ushort Build);

/// <summary>Windows 版本范围。</summary>
public readonly record struct InnoWindowsVersionRange(InnoWindowsVersion Begin, InnoWindowsVersion End);

/// <summary>
///     主 setup 头部（对应 TSetupHeader）。
///     解析时先按版本读取所有原始字段，再使用确定的代码页进行字符串解码。
/// </summary>
public sealed class InnoHeader {
	/// <summary>应用名称。</summary>
	public string AppName { get; private set; } = string.Empty;

	/// <summary>应用带版本名称。</summary>
	public string AppVersionedName { get; private set; } = string.Empty;

	/// <summary>应用 ID。</summary>
	public string AppId { get; private set; } = string.Empty;

	/// <summary>应用版权。</summary>
	public string AppCopyright { get; private set; } = string.Empty;

	/// <summary>应用发布者。</summary>
	public string AppPublisher { get; private set; } = string.Empty;

	/// <summary>发布者 URL。</summary>
	public string AppPublisherUrl { get; private set; } = string.Empty;

	/// <summary>支持电话（5.1.13+）。</summary>
	public string AppSupportPhone { get; private set; } = string.Empty;

	/// <summary>支持 URL。</summary>
	public string AppSupportUrl { get; private set; } = string.Empty;

	/// <summary>更新 URL。</summary>
	public string AppUpdatesUrl { get; private set; } = string.Empty;

	/// <summary>应用版本。</summary>
	public string AppVersion { get; private set; } = string.Empty;

	/// <summary>默认安装目录名。</summary>
	public string DefaultDirName { get; private set; } = string.Empty;

	/// <summary>默认开始菜单组名。</summary>
	public string DefaultGroupName { get; private set; } = string.Empty;

	/// <summary>setup 数据文件名基名（如 "setup"）。</summary>
	public string BaseFilename { get; private set; } = string.Empty;

	/// <summary>卸载文件目录。</summary>
	public string UninstallFilesDir { get; private set; } = string.Empty;

	/// <summary>卸载程序名称。</summary>
	public string UninstallName { get; private set; } = string.Empty;

	/// <summary>卸载图标。</summary>
	public string UninstallIcon { get; private set; } = string.Empty;

	/// <summary>应用互斥名。</summary>
	public string AppMutex { get; private set; } = string.Empty;

	/// <summary>默认用户名。</summary>
	public string DefaultUserName { get; private set; } = string.Empty;

	/// <summary>默认组织名。</summary>
	public string DefaultUserOrganisation { get; private set; } = string.Empty;

	/// <summary>默认序列号。</summary>
	public string DefaultSerial { get; private set; } = string.Empty;

	/// <summary>应用自述文件。</summary>
	public string AppReadmeFile { get; private set; } = string.Empty;

	/// <summary>应用联系方式。</summary>
	public string AppContact { get; private set; } = string.Empty;

	/// <summary>应用备注。</summary>
	public string AppComments { get; private set; } = string.Empty;

	/// <summary>修改路径。</summary>
	public string AppModifyPath { get; private set; } = string.Empty;

	/// <summary>创建卸载注册表键。</summary>
	public string CreateUninstallRegistryKey { get; private set; } = string.Empty;

	/// <summary>是否可卸载。</summary>
	public string Uninstallable { get; private set; } = string.Empty;

	/// <summary>关闭应用过滤器（5.5.0+）。</summary>
	public string CloseApplicationsFilter { get; private set; } = string.Empty;

	/// <summary>关闭应用过滤器排除项（6.4.2+）。</summary>
	public string CloseApplicationsFilterExcludes { get; private set; } = string.Empty;

	/// <summary>setup 互斥名（5.5.6+）。</summary>
	public string SetupMutex { get; private set; } = string.Empty;

	/// <summary>环境变更（5.6.1+）。</summary>
	public string ChangesEnvironment { get; private set; } = string.Empty;

	/// <summary>关联变更（5.6.1+）。</summary>
	public string ChangesAssociations { get; private set; } = string.Empty;

	/// <summary>允许架构表达式（6.3.0+）。</summary>
	public string ArchitecturesAllowedExpr { get; private set; } = string.Empty;

	/// <summary>64 位模式安装架构表达式（6.3.0+）。</summary>
	public string ArchitecturesInstalledIn64BitModeExpr { get; private set; } = string.Empty;

	/// <summary>7-Zip 库文件名（6.5.0+）。</summary>
	public string SevenZipLibraryName { get; private set; } = string.Empty;

	/// <summary>UsePreviousAppDir 表达式（6.7.0+）。</summary>
	public string UsePreviousAppDir { get; private set; } = string.Empty;

	/// <summary>UsePreviousGroup 表达式（6.7.0+）。</summary>
	public string UsePreviousGroup { get; private set; } = string.Empty;

	/// <summary>UsePreviousSetupType 表达式（6.7.0+）。</summary>
	public string UsePreviousSetupType { get; private set; } = string.Empty;

	/// <summary>UsePreviousTasks 表达式（6.7.0+）。</summary>
	public string UsePreviousTasks { get; private set; } = string.Empty;

	/// <summary>UsePreviousUserInfo 表达式（6.7.0+）。</summary>
	public string UsePreviousUserInfo { get; private set; } = string.Empty;

	/// <summary>许可协议文本。</summary>
	public string LicenseText { get; private set; } = string.Empty;

	/// <summary>安装前信息文本。</summary>
	public string InfoBefore { get; private set; } = string.Empty;

	/// <summary>安装后信息文本。</summary>
	public string InfoAfter { get; private set; } = string.Empty;

	/// <summary>编译的 Pascal 脚本字节码。</summary>
	public byte[] CompiledCode { get; private set; } = [];

	/// <summary>各表条目数量。</summary>
	public int LanguageCount { get; private set; }

	public int MessageCount { get; private set; }
	public int PermissionCount { get; private set; }
	public int TypeCount { get; private set; }
	public int ComponentCount { get; private set; }
	public int TaskCount { get; private set; }
	public int DirectoryCount { get; private set; }
	public int IssigKeyCount { get; private set; }
	public int FileCount { get; private set; }
	public int DataEntryCount { get; private set; }
	public int IconCount { get; private set; }
	public int IniEntryCount { get; private set; }
	public int RegistryEntryCount { get; private set; }
	public int DeleteEntryCount { get; private set; }
	public int UninstallDeleteEntryCount { get; private set; }
	public int RunEntryCount { get; private set; }
	public int UninstallRunEntryCount { get; private set; }

	/// <summary>编译脚本版本（7.0.0.3+）。</summary>
	public uint CompiledCodeVersion { get; private set; }

	/// <summary>要求的最小 Windows 版本。</summary>
	public InnoWindowsVersionRange MinWindowsVersion { get; private set; }

	/// <summary>向导背景色。</summary>
	public uint BackColor { get; private set; }

	/// <summary>向导背景渐变色。</summary>
	public uint BackColor2 { get; private set; }

	/// <summary>向导图片背景色。</summary>
	public uint ImageBackColor { get; private set; }

	/// <summary>小向导图片背景色。</summary>
	public uint SmallImageBackColor { get; private set; }

	/// <summary>向导图片动态深色背景色（6.6.0+）。</summary>
	public uint ImageBackColorDynamicDark { get; private set; }

	/// <summary>小向导图片动态深色背景色（6.6.0+）。</summary>
	public uint SmallImageBackColorDynamicDark { get; private set; }

	/// <summary>向导背景色（6.7.0+）。</summary>
	public uint WizardBackColor { get; private set; }

	/// <summary>向导动态深色背景色（6.7.0+）。</summary>
	public uint WizardBackColorDynamicDark { get; private set; }

	/// <summary>向导图片透明度（6.6.1+，0-255）。</summary>
	public byte WizardImageOpacity { get; private set; } = 0xFF;

	/// <summary>向导背景图片透明度（6.7.0+）。</summary>
	public byte WizardBackImageOpacity { get; private set; } = 0xFF;

	/// <summary>向导样式。</summary>
	public InnoWizardStyle WizardStyle { get; private set; } = InnoWizardStyle.Classic;

	/// <summary>向导深色样式（6.6.0+）。</summary>
	public InnoWizardDarkStyle WizardDarkStyle { get; private set; } = InnoWizardDarkStyle.Light;

	/// <summary>向导浅色控件样式（6.7.0+）。</summary>
	public InnoWizardLightControlStyling WizardLightControlStyling { get; private set; } = InnoWizardLightControlStyling.All;

	/// <summary>向导缩放百分比 X。</summary>
	public uint WizardResizePercentX { get; private set; }

	/// <summary>向导缩放百分比 Y。</summary>
	public uint WizardResizePercentY { get; private set; }

	/// <summary>图片 alpha 格式。</summary>
	public InnoAlphaFormat ImageAlphaFormat { get; private set; } = InnoAlphaFormat.Ignored;

	/// <summary>密码校验类型。</summary>
	public InnoPasswordType PasswordType { get; internal set; } = InnoPasswordType.None;

	/// <summary>密码校验数据。</summary>
	public byte[] PasswordCheck { get; internal set; } = [];

	/// <summary>密码盐。</summary>
	public byte[] PasswordSalt { get; internal set; } = [];

	/// <summary>所需额外磁盘空间。</summary>
	public long ExtraDiskSpaceRequired { get; private set; }

	/// <summary>每张磁盘的切片数。</summary>
	public int SlicesPerDisk { get; private set; } = 1;

	/// <summary>安装模式。</summary>
	public InnoInstallMode InstallMode { get; private set; } = InnoInstallMode.Normal;

	/// <summary>卸载日志模式。</summary>
	public InnoUninstallLogMode UninstallLogMode { get; private set; } = InnoUninstallLogMode.New;

	/// <summary>卸载程序样式。</summary>
	public InnoWizardStyle UninstallStyle { get; private set; } = InnoWizardStyle.Classic;

	/// <summary>目录存在警告。</summary>
	public InnoAutoBool DirExistsWarning { get; private set; } = InnoAutoBool.Auto;

	/// <summary>所需权限级别。</summary>
	public InnoPrivilegesRequired PrivilegesRequired { get; private set; }

	/// <summary>显示语言对话框。</summary>
	public InnoAutoBool ShowLanguageDialog { get; private set; } = InnoAutoBool.Auto;

	/// <summary>语言检测方式。</summary>
	public InnoLanguageDetection LanguageDetection { get; private set; } = InnoLanguageDetection.UILanguage;

	/// <summary>数据压缩方法。</summary>
	public InnoCompressionMethod Compression { get; private set; } = InnoCompressionMethod.Zlib;

	/// <summary>允许的架构。</summary>
	public InnoArchitecture ArchitecturesAllowed { get; private set; } = InnoArchitecture.Unknown;

	/// <summary>64 位模式安装的架构。</summary>
	public InnoArchitecture ArchitecturesInstalledIn64BitMode { get; private set; } = InnoArchitecture.Unknown;

	/// <summary>禁用目录页。</summary>
	public InnoAutoBool DisableDirPage { get; private set; } = InnoAutoBool.Auto;

	/// <summary>禁用程序组页。</summary>
	public InnoAutoBool DisableProgramGroupPage { get; private set; } = InnoAutoBool.Auto;

	/// <summary>卸载程序显示大小。</summary>
	public ulong UninstallDisplaySize { get; private set; }

	/// <summary>主选项标志。</summary>
	public InnoHeaderOptions Options { get; internal set; }

	/// <summary>6.0.0+ 的附加选项标志。</summary>
	public InnoHeaderOptions2 Options2 { get; private set; }

	/// <summary>ANSI 安装包的 lead-byte 表（256 位）。</summary>
	public bool[] LeadBytes { get; private set; } = new bool[256];

	/// <summary>按版本解析主头部（不进行字符串解码）。</summary>
	internal static Raw Parse(InnoBinaryReader reader, InnoVersion version) {
		Raw raw = new();
		VersionGates v = new(version);

		raw.AppName = reader.ReadStringBytes();
		raw.AppVersionedName = reader.ReadStringBytes();
		if (v.Ge130) {
			raw.AppId = reader.ReadStringBytes();
		}

		raw.AppCopyright = reader.ReadStringBytes();
		if (v.Ge130) {
			raw.AppPublisher = reader.ReadStringBytes();
			raw.AppPublisherUrl = reader.ReadStringBytes();
		}

		if (v.Ge5113) {
			raw.AppSupportPhone = reader.ReadStringBytes();
		}

		if (v.Ge130) {
			raw.AppSupportUrl = reader.ReadStringBytes();
			raw.AppUpdatesUrl = reader.ReadStringBytes();
			raw.AppVersion = reader.ReadStringBytes();
		}

		raw.DefaultDirName = reader.ReadStringBytes();
		raw.DefaultGroupName = reader.ReadStringBytes();
		raw.BaseFilename = reader.ReadStringBytes();
		if (v is { Ge130: true, Ge525: false }) {
			// 1.3.0-5.2.4：license/info 文本位于此处（5.2.5+ 移至尾部）
			raw.LicenseText = reader.ReadStringBytes();
			raw.InfoBefore = reader.ReadStringBytes();
			raw.InfoAfter = reader.ReadStringBytes();
		}

		raw.UninstallFilesDir = reader.ReadStringBytes();
		if (v.Ge136) {
			raw.UninstallName = reader.ReadStringBytes();
			raw.UninstallIcon = reader.ReadStringBytes();
		}

		if (v.Ge1314) {
			raw.AppMutex = reader.ReadStringBytes();
		}

		if (v.Ge300) {
			raw.DefaultUserName = reader.ReadStringBytes();
			raw.DefaultUserOrganisation = reader.ReadStringBytes();
		}

		if (v.Ge400) {
			raw.DefaultSerial = reader.ReadStringBytes();
		}

		if (v is { Ge400: true, Ge525: false }) {
			// 4.0.0-5.2.4：compiled_code 位于此（5.2.5+ 移至尾部）
			raw.CompiledCode = reader.ReadStringBytes();
		}

		if (v.Ge424) {
			raw.AppReadmeFile = reader.ReadStringBytes();
			raw.AppContact = reader.ReadStringBytes();
			raw.AppComments = reader.ReadStringBytes();
			raw.AppModifyPath = reader.ReadStringBytes();
		}

		if (v.Ge538) {
			raw.CreateUninstallRegistryKey = reader.ReadStringBytes();
		}

		if (v.Ge5310) {
			raw.Uninstallable = reader.ReadStringBytes();
		}

		if (v.Ge550) {
			raw.CloseApplicationsFilter = reader.ReadStringBytes();
		}

		if (v.Ge556) {
			raw.SetupMutex = reader.ReadStringBytes();
		}

		if (v.Ge561) {
			raw.ChangesEnvironment = reader.ReadStringBytes();
			raw.ChangesAssociations = reader.ReadStringBytes();
		}

		if (v.Ge630) {
			raw.ArchitecturesAllowedExpr = reader.ReadStringBytes();
			raw.ArchitecturesInstalledIn64BitModeExpr = reader.ReadStringBytes();
		}

		if (v.Ge642) {
			raw.CloseApplicationsFilterExcludes = reader.ReadStringBytes();
		}

		if (v.Ge650) {
			raw.SevenZipLibraryName = reader.ReadStringBytes();
		}

		if (v.Ge670) {
			raw.UsePreviousAppDir = reader.ReadStringBytes();
			raw.UsePreviousGroup = reader.ReadStringBytes();
			raw.UsePreviousSetupType = reader.ReadStringBytes();
			raw.UsePreviousTasks = reader.ReadStringBytes();
			raw.UsePreviousUserInfo = reader.ReadStringBytes();
		}

		if (v.Ge525) {
			// 5.2.5+：license/info 文本位于此处（ANSI 编码）
			raw.LicenseText = reader.ReadStringBytes();
			raw.InfoBefore = reader.ReadStringBytes();
			raw.InfoAfter = reader.ReadStringBytes();
		}

		if (v.Ge525) {
			raw.CompiledCode = reader.ReadStringBytes();
		}

		// lead bytes（仅 ANSI 安装包）
		if (!version.IsUnicode) {
			var leadBytes = reader.ReadBytes(32);
			for (var i = 0; i < 256; i++) {
				raw.LeadBytes[i] = (leadBytes[i >> 3] & 1 << (i & 7)) != 0;
			}
		}

		raw.LanguageCount = ReadCount(reader);
		if (v.Ge421) {
			raw.MessageCount = ReadCount(reader);
		}

		if (v.Ge410) {
			raw.PermissionCount = ReadCount(reader);
		}

		raw.TypeCount = ReadCount(reader);
		raw.ComponentCount = ReadCount(reader);
		raw.TaskCount = ReadCount(reader);
		raw.DirectoryCount = ReadCount(reader);
		if (v.Ge650) {
			raw.IssigKeyCount = ReadCount(reader);
		}

		raw.FileCount = ReadCount(reader);
		raw.DataEntryCount = ReadCount(reader);
		raw.IconCount = ReadCount(reader);
		raw.IniEntryCount = ReadCount(reader);
		raw.RegistryEntryCount = ReadCount(reader);
		raw.DeleteEntryCount = ReadCount(reader);
		raw.UninstallDeleteEntryCount = ReadCount(reader);
		raw.RunEntryCount = ReadCount(reader);
		raw.UninstallRunEntryCount = ReadCount(reader);

		if (v.Ge7003) {
			raw.CompiledCodeVersion = reader.ReadUInt32();
		}

		// winver
		raw.WinVersion = ReadWindowsVersionRange(reader, v.Ge1319);

		if (v.Lt6401) {
			raw.BackColor = reader.ReadUInt32();
		}

		if (v is { Ge133: true, Lt6401: true }) {
			raw.BackColor2 = reader.ReadUInt32();
		}

		if (v.Lt557) {
			raw.ImageBackColor = reader.ReadUInt32();
		}

		if (v.Ge600) {
			if (v.Ge660) {
				// 6.6.0+：WizardStyle 被移除，新增 WizardDarkStyle
				raw.WizardResizePercentX = reader.ReadUInt32();
				raw.WizardResizePercentY = reader.ReadUInt32();
				raw.WizardDarkStyleValue = reader.ReadByte();
			} else {
				raw.WizardStyleValue = reader.ReadByte();
				raw.WizardResizePercentX = reader.ReadUInt32();
				raw.WizardResizePercentY = reader.ReadUInt32();
			}
		}

		if (v.Ge557) {
			raw.ImageAlphaFormatValue = reader.ReadByte();
		}

		if (v is { Ge200: true, Ge504: false }) {
			// 2.0.0-5.0.3：small_image_back_color（4.2.x 存在）
			raw.SmallImageBackColor = reader.ReadUInt32();
		}

		if (v.Ge652) {
			raw.ImageBackColor = reader.ReadUInt32();
			raw.SmallImageBackColor = reader.ReadUInt32();
		}

		if (v.Ge670) {
			raw.WizardBackColor = reader.ReadUInt32();
		}

		if (v.Ge660) {
			raw.ImageBackColorDynamicDark = reader.ReadUInt32();
			raw.SmallImageBackColorDynamicDark = reader.ReadUInt32();
		}

		if (v.Ge670) {
			raw.WizardBackColorDynamicDark = reader.ReadUInt32();
		}

		if (v.Ge661) {
			raw.WizardImageOpacity = reader.ReadByte();
		}

		if (v.Ge670) {
			raw.WizardBackImageOpacity = reader.ReadByte();
			raw.WizardLightControlStylingValue = reader.ReadByte();
		}

		// 密码字段
		if (v.Ge650) {
			// 6.5.0+：加密元数据移至独立的 TSetupEncryptionHeader
			raw.PasswordType = InnoPasswordType.None;
		} else if (v.Ge640) {
			raw.PasswordType = InnoPasswordType.Pbkdf2Sha256XChaCha20;
			raw.PasswordCheck = reader.ReadBytes(4);
			raw.PasswordSalt = reader.ReadBytes(44);
		} else if (v.Ge539) {
			raw.PasswordType = InnoPasswordType.Sha1;
			raw.PasswordCheck = reader.ReadBytes(20);
			var salt = reader.ReadBytes(8);
			raw.PasswordSalt = new byte[14 + salt.Length];
			"PasswordCheckHash"u8.CopyTo(raw.PasswordSalt);
			Array.Copy(salt, 0, raw.PasswordSalt, 14, salt.Length);
		} else if (v.Ge420) {
			raw.PasswordType = InnoPasswordType.Md5;
			raw.PasswordCheck = reader.ReadBytes(16);
			var salt = reader.ReadBytes(8);
			raw.PasswordSalt = new byte[14 + salt.Length];
			"PasswordCheckHash"u8.CopyTo(raw.PasswordSalt);
			Array.Copy(salt, 0, raw.PasswordSalt, 14, salt.Length);
		} else {
			raw.PasswordType = InnoPasswordType.Crc32;
			raw.PasswordCheck = BitConverter.GetBytes(reader.ReadUInt32());
		}

		if (v.Ge400) {
			raw.ExtraDiskSpaceRequired = reader.ReadInt64();
			raw.SlicesPerDisk = reader.ReadInt32();
		} else {
			raw.ExtraDiskSpaceRequired = reader.ReadInt32();
			raw.SlicesPerDisk = 1;
		}

		if (v is { Ge200: true, Lt500: true }) {
			raw.InstallMode = (InnoInstallMode)reader.ReadByte();
		}

		if (v.Ge130) {
			raw.UninstallLogMode = (InnoUninstallLogMode)reader.ReadByte();
		}

		if (v.Ge500) {
			raw.UninstallStyle = InnoWizardStyle.Modern;
		} else if (v.Ge200) {
			raw.UninstallStyle = (InnoWizardStyle)reader.ReadByte();
		}

		if (v.Ge136) {
			raw.DirExistsWarning = (InnoAutoBool)reader.ReadByte();
		}

		if (v.Ge537) {
			raw.PrivilegesRequired = (InnoPrivilegesRequired)reader.ReadByte();
		} else if (v.Ge304) {
			raw.PrivilegesRequired = (InnoPrivilegesRequired)reader.ReadByte();
		}

		if (v.Ge570) {
			_ = reader.ReadByte(); // privileges_required_override_allowed
		}

		if (v.Ge410) {
			raw.ShowLanguageDialog = (InnoAutoBool)reader.ReadByte();
			raw.LanguageDetection = (InnoLanguageDetection)reader.ReadByte();
		}

		if (v.Ge539) {
			raw.Compression = (InnoCompressionMethod)reader.ReadByte();
		} else if (v.Ge426) {
			raw.Compression = (InnoCompressionMethod)reader.ReadByte();
		} else if (v.Ge425) {
			raw.Compression = (InnoCompressionMethod)reader.ReadByte();
		} else if (v.Ge415) {
			raw.Compression = (InnoCompressionMethod)reader.ReadByte();
		}

		if (v.Ge630) {
			// 6.3.0+：架构以表达式存储
		} else if (v.Ge560) {
			raw.ArchitecturesAllowed = (InnoArchitecture)reader.ReadByte();
			raw.ArchitecturesInstalledIn64BitMode = (InnoArchitecture)reader.ReadByte();
		} else if (v.Ge510) {
			raw.ArchitecturesAllowed = (InnoArchitecture)reader.ReadByte();
			raw.ArchitecturesInstalledIn64BitMode = (InnoArchitecture)reader.ReadByte();
		} else {
			raw.ArchitecturesAllowed = InnoArchitecture.Unknown | InnoArchitecture.X86 | InnoArchitecture.Amd64 |
				InnoArchitecture.Ia64;
			raw.ArchitecturesInstalledIn64BitMode = InnoArchitecture.Unknown | InnoArchitecture.X86 | InnoArchitecture.Amd64 |
				InnoArchitecture.Ia64;
		}

		if (v.Ge533) {
			raw.DisableDirPage = (InnoAutoBool)reader.ReadByte();
			raw.DisableProgramGroupPage = (InnoAutoBool)reader.ReadByte();
		}

		if (v.Ge550) {
			raw.UninstallDisplaySize = reader.ReadUInt64();
		} else if (v.Ge536) {
			raw.UninstallDisplaySize = reader.ReadUInt32();
		}

		// BlackBox 变体：多余 1 字节
		if (version is { Major: 5, Minor: 3, Patch: 10, Revision: 1 } or { Major: 5, Minor: 4, Patch: 2, Revision: 1 } or
			{ Major: 5, Minor: 5, Patch: 0, Revision: 1 }) {
			reader.ReadByte();
		}

		LoadFlags(reader, version, raw);

		// 旧版本回填逻辑
		if (v.Lt304) {
			raw.PrivilegesRequired =
				(raw.Options & 1UL << 19) != 0 ? InnoPrivilegesRequired.Admin : InnoPrivilegesRequired.None;
		}

		if (v.Lt4010) {
			raw.ShowLanguageDialog = (raw.Options & 1UL << 44) != 0 ? InnoAutoBool.Yes : InnoAutoBool.No;
			raw.LanguageDetection =
				(raw.Options & 1UL << 45) != 0 ? InnoLanguageDetection.Locale : InnoLanguageDetection.UILanguage;
		}

		if (v.Lt415) {
			raw.Compression = (raw.Options & 1UL << 37) != 0 ? InnoCompressionMethod.BZip2 : InnoCompressionMethod.Zlib;
		}

		if (v.Lt533) {
			raw.DisableDirPage = (raw.Options & 1UL << 3) != 0 ? InnoAutoBool.Yes : InnoAutoBool.No;
			raw.DisableProgramGroupPage = (raw.Options & 1UL << 5) != 0 ? InnoAutoBool.Yes : InnoAutoBool.No;
		}

		return raw;
	}

	/// <summary>将解析结果按指定代码页解码为公开模型。</summary>
	internal static InnoHeader FromRaw(Raw raw, int codepage) {
		InnoHeader header = new();
		bool[]? lead = null;
		if (codepage != InnoStringDecoder.CpUtf16Le) {
			lead = raw.LeadBytes;
		}

		header.AppName = Decode(raw.AppName, codepage);
		header.AppVersionedName = Decode(raw.AppVersionedName, codepage);
		header.AppId = Decode(raw.AppId, codepage);
		header.AppCopyright = Decode(raw.AppCopyright, codepage);
		header.AppPublisher = Decode(raw.AppPublisher, codepage);
		header.AppPublisherUrl = Decode(raw.AppPublisherUrl, codepage);
		header.AppSupportPhone = Decode(raw.AppSupportPhone, codepage);
		header.AppSupportUrl = Decode(raw.AppSupportUrl, codepage);
		header.AppUpdatesUrl = Decode(raw.AppUpdatesUrl, codepage);
		header.AppVersion = Decode(raw.AppVersion, codepage);
		header.DefaultDirName = Decode(raw.DefaultDirName, codepage, lead);
		header.DefaultGroupName = Decode(raw.DefaultGroupName, codepage);
		header.BaseFilename = Decode(raw.BaseFilename, codepage, lead);
		header.UninstallFilesDir = Decode(raw.UninstallFilesDir, codepage, lead);
		header.UninstallName = Decode(raw.UninstallName, codepage, lead);
		header.UninstallIcon = Decode(raw.UninstallIcon, codepage, lead);
		header.AppMutex = Decode(raw.AppMutex, codepage, lead);
		header.DefaultUserName = Decode(raw.DefaultUserName, codepage);
		header.DefaultUserOrganisation = Decode(raw.DefaultUserOrganisation, codepage);
		header.DefaultSerial = Decode(raw.DefaultSerial, codepage);
		header.AppReadmeFile = Decode(raw.AppReadmeFile, codepage, lead);
		header.AppContact = Decode(raw.AppContact, codepage);
		header.AppComments = Decode(raw.AppComments, codepage);
		header.AppModifyPath = Decode(raw.AppModifyPath, codepage, lead);
		header.CreateUninstallRegistryKey = Decode(raw.CreateUninstallRegistryKey, codepage, lead);
		header.Uninstallable = Decode(raw.Uninstallable, codepage);
		header.CloseApplicationsFilter = Decode(raw.CloseApplicationsFilter, codepage);
		header.CloseApplicationsFilterExcludes = Decode(raw.CloseApplicationsFilterExcludes, codepage);
		header.SetupMutex = Decode(raw.SetupMutex, codepage, lead);
		header.ChangesEnvironment = Decode(raw.ChangesEnvironment, codepage);
		header.ChangesAssociations = Decode(raw.ChangesAssociations, codepage);
		header.ArchitecturesAllowedExpr = Decode(raw.ArchitecturesAllowedExpr, codepage);
		header.ArchitecturesInstalledIn64BitModeExpr = Decode(raw.ArchitecturesInstalledIn64BitModeExpr, codepage);
		header.SevenZipLibraryName = Decode(raw.SevenZipLibraryName, codepage);
		header.UsePreviousAppDir = Decode(raw.UsePreviousAppDir, codepage);
		header.UsePreviousGroup = Decode(raw.UsePreviousGroup, codepage);
		header.UsePreviousSetupType = Decode(raw.UsePreviousSetupType, codepage);
		header.UsePreviousTasks = Decode(raw.UsePreviousTasks, codepage);
		header.UsePreviousUserInfo = Decode(raw.UsePreviousUserInfo, codepage);
		header.LicenseText = Decode(raw.LicenseText, InnoStringDecoder.CpWindows1252);
		header.InfoBefore = Decode(raw.InfoBefore, InnoStringDecoder.CpWindows1252);
		header.InfoAfter = Decode(raw.InfoAfter, InnoStringDecoder.CpWindows1252);
		header.CompiledCode = raw.CompiledCode;

		header.LanguageCount = raw.LanguageCount;
		header.MessageCount = raw.MessageCount;
		header.PermissionCount = raw.PermissionCount;
		header.TypeCount = raw.TypeCount;
		header.ComponentCount = raw.ComponentCount;
		header.TaskCount = raw.TaskCount;
		header.DirectoryCount = raw.DirectoryCount;
		header.IssigKeyCount = raw.IssigKeyCount;
		header.FileCount = raw.FileCount;
		header.DataEntryCount = raw.DataEntryCount;
		header.IconCount = raw.IconCount;
		header.IniEntryCount = raw.IniEntryCount;
		header.RegistryEntryCount = raw.RegistryEntryCount;
		header.DeleteEntryCount = raw.DeleteEntryCount;
		header.UninstallDeleteEntryCount = raw.UninstallDeleteEntryCount;
		header.RunEntryCount = raw.RunEntryCount;
		header.UninstallRunEntryCount = raw.UninstallRunEntryCount;
		header.CompiledCodeVersion = raw.CompiledCodeVersion;

		header.MinWindowsVersion = raw.WinVersion;
		header.BackColor = raw.BackColor;
		header.BackColor2 = raw.BackColor2;
		header.ImageBackColor = raw.ImageBackColor;
		header.SmallImageBackColor = raw.SmallImageBackColor;
		header.ImageBackColorDynamicDark = raw.ImageBackColorDynamicDark;
		header.SmallImageBackColorDynamicDark = raw.SmallImageBackColorDynamicDark;
		header.WizardBackColor = raw.WizardBackColor;
		header.WizardBackColorDynamicDark = raw.WizardBackColorDynamicDark;
		header.WizardImageOpacity = raw.WizardImageOpacity;
		header.WizardBackImageOpacity = raw.WizardBackImageOpacity;
		header.WizardStyle = raw.WizardStyleValue == 1 ? InnoWizardStyle.Modern : InnoWizardStyle.Classic;
		header.WizardDarkStyle = (InnoWizardDarkStyle)raw.WizardDarkStyleValue;
		header.WizardLightControlStyling = (InnoWizardLightControlStyling)raw.WizardLightControlStylingValue;
		header.WizardResizePercentX = raw.WizardResizePercentX;
		header.WizardResizePercentY = raw.WizardResizePercentY;
		header.ImageAlphaFormat = (InnoAlphaFormat)raw.ImageAlphaFormatValue;

		header.PasswordType = raw.PasswordType;
		header.PasswordCheck = raw.PasswordCheck;
		header.PasswordSalt = raw.PasswordSalt;
		header.ExtraDiskSpaceRequired = raw.ExtraDiskSpaceRequired;
		header.SlicesPerDisk = raw.SlicesPerDisk;
		header.InstallMode = raw.InstallMode;
		header.UninstallLogMode = raw.UninstallLogMode;
		header.UninstallStyle = raw.UninstallStyle;
		header.DirExistsWarning = raw.DirExistsWarning;
		header.PrivilegesRequired = raw.PrivilegesRequired;
		header.ShowLanguageDialog = raw.ShowLanguageDialog;
		header.LanguageDetection = raw.LanguageDetection;
		header.Compression = raw.Compression;
		header.ArchitecturesAllowed = raw.ArchitecturesAllowed;
		header.ArchitecturesInstalledIn64BitMode = raw.ArchitecturesInstalledIn64BitMode;
		header.DisableDirPage = raw.DisableDirPage;
		header.DisableProgramGroupPage = raw.DisableProgramGroupPage;
		header.UninstallDisplaySize = raw.UninstallDisplaySize;
		header.Options = (InnoHeaderOptions)(raw.Options & 0x7FFFFFFFFFFFFFFFUL);
		header.Options2 = (InnoHeaderOptions2)raw.Options2;
		header.LeadBytes = raw.LeadBytes;
		return header;
	}

	static private string Decode(byte[] data, int codepage, bool[]? leadBytes = null) {
		return InnoStringDecoder.Decode(data, codepage, leadBytes);
	}

	static private int ReadCount(InnoBinaryReader reader) { return reader.ReadInt32(); }

	/// <summary>
	///     读取完整的 windows_version_range（每侧：win_version + nt_version + nt_service_pack）。
	/// </summary>
	static private InnoWindowsVersionRange ReadWindowsVersionRange(InnoBinaryReader reader, bool hasBuild) {
		return new(ReadOne(), ReadOne());

		InnoWindowsVersion ReadOne() {
			var build = hasBuild ? reader.ReadUInt16() : (ushort)0;
			var minor = reader.ReadByte();
			var major = reader.ReadByte();
			_ = reader.ReadUInt16(); // nt_version build
			_ = reader.ReadByte(); // nt_version minor
			_ = reader.ReadByte(); // nt_version major
			_ = reader.ReadUInt16(); // nt_service_pack（major+minor，5.3.19 前也存在）
			return new(major, minor, build);
		}
	}

	/// <summary>读取主选项标志位（对应 load_flags）。</summary>
	static private void LoadFlags(InnoBinaryReader reader, InnoVersion version, Raw raw) {
		VersionGates v = new(version);
		StoredFlagReader flags = new(reader);

		flags.Add(0); // DisableStartupPrompt
		if (v.Lt5310) {
			flags.Add(1); // Uninstallable
		}

		flags.Add(2); // CreateAppDir
		if (v.Lt533) {
			flags.Add(3); // DisableDirPage
		}

		if (v.Lt136) {
			flags.Add(4); // DisableDirExistsWarning
		}

		if (v.Lt533) {
			flags.Add(5); // DisableProgramGroupPage
		}

		flags.Add(6); // AllowNoIcons
		if (v.Lt300 || v.Ge303) {
			flags.Add(7); // AlwaysRestart
		}

		if (v.Lt133) {
			flags.Add(8); // BackSolid
		}

		flags.Add(9); // AlwaysUsePersonalGroup
		if (v.Lt6401) {
			flags.Add(10); // WindowVisible
			flags.Add(11); // WindowShowCaption
			flags.Add(12); // WindowResizable
			flags.Add(13); // WindowStartMaximized
		}

		flags.Add(14); // EnableDirDoesntExistWarning
		if (v.Lt412) {
			flags.Add(15); // DisableAppendDir
		}

		flags.Add(16); // Password
		if (v.Ge126) {
			flags.Add(17); // AllowRootDirectory
		}

		if (v.Ge1214) {
			flags.Add(18); // DisableFinishedPage
		}

		if (v.Lt304) {
			flags.Add(19); // AdminPrivilegesRequired
		}

		if (v.Lt300) {
			flags.Add(20); // AlwaysCreateUninstallIcon
		}

		if (v.Lt136) {
			flags.Add(21); // OverwriteUninstRegEntries
		}

		if (v.Lt561) {
			flags.Add(22); // ChangesAssociations
		}

		if (v is { Ge130: true, Lt538: true }) {
			flags.Add(23); // CreateUninstallRegKey
		}

		if (v is { Ge131: true, Lt670: true }) {
			flags.Add(24); // UsePreviousAppDir
		}

		if (v is { Ge133: true, Lt6401: true }) {
			flags.Add(25); // BackColorHorizontal
		}

		if (v is { Ge1310: true, Lt670: true }) {
			flags.Add(26); // UsePreviousGroup
		}

		if (v.Ge1320) {
			flags.Add(27); // UpdateUninstallLogAppName
		}

		if (v is { Ge200: true, Lt670: true }) {
			flags.Add(28); // UsePreviousSetupType
		}

		if (v.Ge200) {
			flags.Add(29); // DisableReadyMemo
			flags.Add(30); // AlwaysShowComponentsList
			flags.Add(31); // FlatComponentsList
			flags.Add(32); // ShowComponentSizes
			if (v.Lt670) {
				flags.Add(33); // UsePreviousTasks
			}

			flags.Add(34); // DisableReadyPage
		}

		if (v.Ge207) {
			flags.Add(35); // AlwaysShowDirOnReadyPage
			flags.Add(36); // AlwaysShowGroupOnReadyPage
		}

		if (v is { Ge2017: true, Lt415: true }) {
			flags.Add(37); // BzipUsed
		}

		if (v.Ge2018) {
			flags.Add(38); // AllowUNCPath
		}

		if (v.Ge300) {
			flags.Add(39); // UserInfoPage
			if (v.Lt670) {
				flags.Add(40); // UsePreviousUserInfo
			}
		}

		if (v.Ge301) {
			flags.Add(41); // UninstallRestartComputer
		}

		if (v.Ge303) {
			flags.Add(42); // RestartIfNeededByRun
		}

		if (v.Ge400) {
			flags.Add(43); // ShowTasksTreeLines
		}

		if (v is { Ge400: true, Lt4010: true }) {
			flags.Add(44); // ShowLanguageDialog
		}

		if (v is { Ge401: true, Lt4010: true }) {
			flags.Add(45); // DetectLanguageUsingLocale
		}

		if (v.Ge409) {
			flags.Add(46); // AllowCancelDuringInstall
		} else {
			raw.Options |= 1UL << 46;
		}

		if (v.Ge413) {
			flags.Add(47); // WizardImageStretch
		}

		if (v.Ge418) {
			flags.Add(48); // AppendDefaultDirName
			flags.Add(49); // AppendDefaultGroupName
		}

		if (v is { Ge422: true, Lt650: true }) {
			flags.Add(50); // EncryptionUsed
		}

		if (v is { Ge504: true, Lt561: true }) {
			flags.Add(51); // ChangesEnvironment
		}

		if (v.Ge517 && !version.IsUnicode) {
			flags.Add(52); // ShowUndisplayableLanguages
		}

		if (v.Ge5113) {
			flags.Add(53); // SetupLogging
		}

		if (v.Ge521) {
			flags.Add(54); // SignedUninstaller
		}

		if (v.Ge538) {
			flags.Add(55); // UsePreviousLanguage
		}

		if (v.Ge539) {
			flags.Add(56); // DisableWelcomePage
		}

		if (v.Ge550) {
			flags.Add(57); // CloseApplications
			flags.Add(58); // RestartApplications
			flags.Add(59); // AllowNetworkDrive
		} else {
			raw.Options |= 1UL << 59;
		}

		if (v.Ge557) {
			flags.Add(60); // ForceCloseApplications
		}

		if (v.Ge600) {
			flags.Add(61); // AppNameHasConsts
			flags.Add(62); // UsePreviousPrivileges
			if (v.Lt660) {
				flags.Add(63); // WizardResizable
			}
		}

		if (v.Ge630) {
			flags.Add(64); // UninstallLogging
		}

		if (v.Ge660) {
			flags.Add(65); // WizardModern
			flags.Add(66); // WizardBorderStyled
			flags.Add(67); // WizardKeepAspectRatio
			if (v.Lt670) {
				flags.Add(68); // WizardLightButtonsUnstyled
			}
		}

		if (v.Ge670) {
			flags.Add(69); // RedirectionGuard
			flags.Add(70); // WizardBevelsHidden
			flags.DiscardPaddingTo(8); // 标志集固定 8 字节
		}

		var (low, high) = flags.GetResult();
		raw.Options |= low;
		raw.Options2 |= high;
	}

	/// <summary>解析期使用的原始字符串容器。</summary>
	internal sealed class Raw {
		public byte[] AppComments = [];
		public byte[] AppContact = [];
		public byte[] AppCopyright = [];
		public byte[] AppId = [];
		public byte[] AppModifyPath = [];
		public byte[] AppMutex = [];
		public byte[] AppName = [];
		public byte[] AppPublisher = [];
		public byte[] AppPublisherUrl = [];
		public byte[] AppReadmeFile = [];
		public byte[] AppSupportPhone = [];
		public byte[] AppSupportUrl = [];
		public byte[] AppUpdatesUrl = [];
		public byte[] AppVersion = [];
		public byte[] AppVersionedName = [];
		public InnoArchitecture ArchitecturesAllowed = InnoArchitecture.Unknown;
		public byte[] ArchitecturesAllowedExpr = [];
		public InnoArchitecture ArchitecturesInstalledIn64BitMode = InnoArchitecture.Unknown;
		public byte[] ArchitecturesInstalledIn64BitModeExpr = [];

		public uint BackColor;
		public uint BackColor2;
		public byte[] BaseFilename = [];
		public byte[] ChangesAssociations = [];
		public byte[] ChangesEnvironment = [];
		public byte[] CloseApplicationsFilter = [];
		public byte[] CloseApplicationsFilterExcludes = [];
		public byte[] CompiledCode = [];
		public uint CompiledCodeVersion;
		public int ComponentCount;
		public InnoCompressionMethod Compression = InnoCompressionMethod.Zlib;
		public byte[] CreateUninstallRegistryKey = [];
		public int DataEntryCount;
		public byte[] DefaultDirName = [];
		public byte[] DefaultGroupName = [];
		public byte[] DefaultSerial = [];
		public byte[] DefaultUserName = [];
		public byte[] DefaultUserOrganisation = [];
		public int DeleteEntryCount;
		public InnoAutoBool DirExistsWarning = InnoAutoBool.Auto;
		public int DirectoryCount;
		public InnoAutoBool DisableDirPage = InnoAutoBool.Auto;
		public InnoAutoBool DisableProgramGroupPage = InnoAutoBool.Auto;

		public long ExtraDiskSpaceRequired;
		public int FileCount;
		public int IconCount;
		public byte ImageAlphaFormatValue;
		public uint ImageBackColor;
		public uint ImageBackColorDynamicDark;
		public byte[] InfoAfter = [];
		public byte[] InfoBefore = [];
		public int IniEntryCount;
		public InnoInstallMode InstallMode = InnoInstallMode.Normal;
		public int IssigKeyCount;

		public int LanguageCount;
		public InnoLanguageDetection LanguageDetection = InnoLanguageDetection.UILanguage;
		public bool[] LeadBytes = new bool[256];
		public byte[] LicenseText = [];
		public int MessageCount;

		public ulong Options;
		public ulong Options2;
		public byte[] PasswordCheck = [];
		public byte[] PasswordSalt = [];

		public InnoPasswordType PasswordType = InnoPasswordType.None;
		public int PermissionCount;
		public InnoPrivilegesRequired PrivilegesRequired;
		public int RegistryEntryCount;
		public int RunEntryCount;
		public byte[] SetupMutex = [];
		public byte[] SevenZipLibraryName = [];
		public InnoAutoBool ShowLanguageDialog = InnoAutoBool.Auto;
		public int SlicesPerDisk = 1;
		public uint SmallImageBackColor;
		public uint SmallImageBackColorDynamicDark;
		public int TaskCount;
		public int TypeCount;
		public int UninstallDeleteEntryCount;
		public ulong UninstallDisplaySize;
		public byte[] UninstallFilesDir = [];
		public byte[] UninstallIcon = [];
		public InnoUninstallLogMode UninstallLogMode = InnoUninstallLogMode.New;
		public byte[] UninstallName = [];
		public int UninstallRunEntryCount;
		public InnoWizardStyle UninstallStyle = InnoWizardStyle.Classic;
		public byte[] Uninstallable = [];
		public byte[] UsePreviousAppDir = [];
		public byte[] UsePreviousGroup = [];
		public byte[] UsePreviousSetupType = [];
		public byte[] UsePreviousTasks = [];
		public byte[] UsePreviousUserInfo = [];

		public InnoWindowsVersionRange WinVersion;
		public uint WizardBackColor;
		public uint WizardBackColorDynamicDark;
		public byte WizardBackImageOpacity = 0xFF;
		public byte WizardDarkStyleValue;
		public byte WizardImageOpacity = 0xFF;
		public byte WizardLightControlStylingValue;
		public uint WizardResizePercentX;
		public uint WizardResizePercentY;

		public uint WizardStyleValue;
	}

	/// <summary>内部：按位读取的标志集（字节序为小端，位从 LSB 到 MSB）。</summary>
	sealed private class StoredFlagReader(InnoBinaryReader reader) {
		private int _bits;
		private byte _current;
		private ulong _flags;
		private ulong _flagsHigh;

		public void Add(int flagIndex) {
			if ((_bits & 7) == 0) {
				_current = reader.ReadByte();
			}

			if ((_current & 1 << (_bits & 7)) != 0) {
				if (flagIndex < 64) {
					_flags |= 1UL << flagIndex;
				} else {
					_flagsHigh |= 1UL << flagIndex - 64;
				}
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

		/// <summary>返回低 64 位与高 64 位标志。</summary>
		public (ulong Low, ulong High) GetResult() { return (_flags, _flagsHigh); }
	}

	/// <summary>版本门控条件集合（避免重复比较）。</summary>
	internal readonly struct VersionGates(InnoVersion version) {

		public bool Ge126 => Ge(1, 2, 6);
		public bool Ge125 => Ge(1, 2, 5);
		public bool Ge130 => Ge(1, 3, 0);
		public bool Ge131 => Ge(1, 3, 1);
		public bool Ge133 => Ge(1, 3, 3);
		public bool Ge136 => Ge(1, 3, 6);
		public bool Ge1214 => Ge(1, 2, 14);
		public bool Ge1310 => Ge(1, 3, 10);
		public bool Ge1314 => Ge(1, 3, 14);
		public bool Ge1319 => Ge(1, 3, 19);
		public bool Ge1320 => Ge(1, 3, 20);
		public bool Ge1321 => Ge(1, 3, 21);
		public bool Ge1325 => Ge(1, 3, 25);
		public bool Ge200 => Ge(2, 0, 0);
		public bool Ge2011 => Ge(2, 0, 11);
		public bool Ge2017 => Ge(2, 0, 17);
		public bool Ge2018 => Ge(2, 0, 18);
		public bool Ge205 => Ge(2, 0, 5);
		public bool Ge207 => Ge(2, 0, 7);
		public bool Ge300 => Ge(3, 0, 0);
		public bool Ge301 => Ge(3, 0, 1);
		public bool Ge303 => Ge(3, 0, 3);
		public bool Ge304 => Ge(3, 0, 4);
		public bool Ge305 => Ge(3, 0, 5);
		public bool Ge400 => Ge(4, 0, 0);
		public bool Ge401 => Ge(4, 0, 1);
		public bool Ge405 => Ge(4, 0, 5);
		public bool Ge409 => Ge(4, 0, 9);
		public bool Ge4010 => Ge(4, 0, 10);
		public bool Ge410 => Ge(4, 1, 0);
		public bool Ge412 => Ge(4, 1, 2);
		public bool Ge413 => Ge(4, 1, 3);
		public bool Ge415 => Ge(4, 1, 5);
		public bool Ge416 => Ge(4, 1, 6);
		public bool Ge418 => Ge(4, 1, 8);
		public bool Ge420 => Ge(4, 2, 0);
		public bool Ge421 => Ge(4, 2, 1);
		public bool Ge422 => Ge(4, 2, 2);
		public bool Ge424 => Ge(4, 2, 4);
		public bool Ge425 => Ge(4, 2, 5);
		public bool Ge426 => Ge(4, 2, 6);
		public bool Ge500 => Ge(5, 0, 0);
		public bool Ge503 => Ge(5, 0, 3);
		public bool Ge504 => Ge(5, 0, 4);
		public bool Ge510 => Ge(5, 1, 0);
		public bool Ge512 => Ge(5, 1, 2);
		public bool Ge517 => Ge(5, 1, 7);
		public bool Ge5113 => Ge(5, 1, 13);
		public bool Ge520 => Ge(5, 2, 0);
		public bool Ge521 => Ge(5, 2, 1);
		public bool Ge523 => Ge(5, 2, 3);
		public bool Ge525 => Ge(5, 2, 5);
		public bool Ge533 => Ge(5, 3, 3);
		public bool Ge536 => Ge(5, 3, 6);
		public bool Ge537 => Ge(5, 3, 7);
		public bool Ge538 => Ge(5, 3, 8);
		public bool Ge539 => Ge(5, 3, 9);
		public bool Ge5310 => Ge(5, 3, 10);
		public bool Ge560 => Ge(5, 6, 0);
		public bool Lt5310 => !Ge5310;
		public bool Ge550 => Ge(5, 5, 0);
		public bool Ge556 => Ge(5, 5, 6);
		public bool Ge557 => Ge(5, 5, 7);
		public bool Ge561 => Ge(5, 6, 1);
		public bool Ge570 => Ge(5, 7, 0);
		public bool Ge600 => Ge(6, 0, 0);
		public bool Ge630 => Ge(6, 3, 0);
		public bool Ge640 => Ge(6, 4, 0);
		public bool Ge6401 => Ge(6, 4, 0, 1);
		public bool Ge642 => Ge(6, 4, 2);
		public bool Ge643 => Ge(6, 4, 3);
		public bool Ge650 => Ge(6, 5, 0);
		public bool Ge652 => Ge(6, 5, 2);
		public bool Ge660 => Ge(6, 6, 0);
		public bool Ge661 => Ge(6, 6, 1);
		public bool Ge670 => Ge(6, 7, 0);
		public bool Ge7003 => Ge(7, 0, 0, 3);

		// 脚本条目解析所需的补充版本门控
		public bool Ge403 => Ge(4, 0, 3);
		public bool Ge4011 => Ge(4, 0, 11);
		public bool Ge5110 => Ge(5, 1, 10);
		public bool Ge535 => Ge(5, 3, 5);
		public bool Ge542 => Ge(5, 4, 2);
		public bool Ge610 => Ge(6, 1, 0);
		public bool Ge7001 => Ge(7, 0, 0, 1);

		public bool Lt133 => !Ge133;
		public bool Lt136 => !Ge136;
		public bool Lt300 => !Ge300;
		public bool Lt304 => !Ge304;
		public bool Lt400 => !Ge400;
		public bool Lt401 => !Ge401;
		public bool Lt4010 => !Ge4010;
		public bool Lt412 => !Ge412;
		public bool Lt415 => !Ge415;
		public bool Lt500 => !Ge500;
		public bool Lt422 => !Ge422;
		public bool Lt410 => !Ge410;
		public bool Lt530 => !Ge530;
		public bool Ge530 => Ge(5, 3, 0);
		public bool Lt533 => !Ge533;
		public bool Lt538 => !Ge538;
		public bool Lt557 => !Ge557;
		public bool Lt561 => !Ge561;
		public bool Lt630 => !Ge630;
		public bool Lt640 => !Ge640;
		public bool Lt6401 => !Ge6401;
		public bool Lt643 => !Ge643;
		public bool Lt650 => !Ge650;
		public bool Lt660 => !Ge660;
		public bool Lt670 => !Ge670;
		public bool Lt7003 => !Ge7003;

		private bool Ge(uint a, uint b, uint c, uint d = 0) {
			return version.CompareTo(new(a, b, c, d, version.IsUnicode, version.IsIsx, false, true)) >= 0;
		}
	}
}