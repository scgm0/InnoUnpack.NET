namespace InnoUnpack.NET.Metadata;

/// <summary>
///     数据块/文件数据块使用的压缩方法。
/// </summary>
public enum InnoCompressionMethod {
	/// <summary>未压缩（原样存储）。</summary>
	Stored,

	/// <summary>zlib (deflate)。</summary>
	Zlib,

	/// <summary>bzip2。</summary>
	BZip2,

	/// <summary>LZMA1。</summary>
	Lzma1,

	/// <summary>LZMA2。</summary>
	Lzma2
}

/// <summary>
///     数据块的加密方法。
/// </summary>
public enum InnoEncryptionMethod {
	/// <summary>未加密。</summary>
	Plaintext,

	/// <summary>ARC4 + MD5 密钥派生（4.2.2 – 5.3.8）。</summary>
	Arc4Md5,

	/// <summary>ARC4 + SHA1 密钥派生（5.3.9 – 6.3.x）。</summary>
	Arc4Sha1,

	/// <summary>XChaCha20 + PBKDF2-SHA256（6.4+）。</summary>
	XChaCha20
}

/// <summary>安装包主选项标志（对应 TSetupHeaderOption）。</summary>
[Flags]
public enum InnoHeaderOptions : ulong {
	None = 0,
	DisableStartupPrompt = 1 << 0,
	Uninstallable = 1 << 1,
	CreateAppDir = 1 << 2,
	DisableDirPage = 1 << 3,
	DisableDirExistsWarning = 1 << 4,
	DisableProgramGroupPage = 1 << 5,
	AllowNoIcons = 1 << 6,
	AlwaysRestart = 1 << 7,
	BackSolid = 1 << 8,
	AlwaysUsePersonalGroup = 1 << 9,
	WindowVisible = 1 << 10,
	WindowShowCaption = 1 << 11,
	WindowResizable = 1 << 12,
	WindowStartMaximized = 1 << 13,
	EnableDirDoesntExistWarning = 1 << 14,
	DisableAppendDir = 1 << 15,
	Password = 1 << 16,
	AllowRootDirectory = 1 << 17,
	DisableFinishedPage = 1 << 18,
	AdminPrivilegesRequired = 1 << 19,
	AlwaysCreateUninstallIcon = 1 << 20,
	OverwriteUninstRegEntries = 1 << 21,
	ChangesAssociations = 1 << 22,
	CreateUninstallRegKey = 1 << 23,
	UsePreviousAppDir = 1 << 24,
	BackColorHorizontal = 1 << 25,
	UsePreviousGroup = 1 << 26,
	UpdateUninstallLogAppName = 1 << 27,
	UsePreviousSetupType = 1 << 28,
	DisableReadyMemo = 1 << 29,
	AlwaysShowComponentsList = 1 << 30,
	FlatComponentsList = 1L << 31,
	ShowComponentSizes = 1L << 32,
	UsePreviousTasks = 1L << 33,
	DisableReadyPage = 1L << 34,
	AlwaysShowDirOnReadyPage = 1L << 35,
	AlwaysShowGroupOnReadyPage = 1L << 36,
	BzipUsed = 1L << 37,
	AllowUncPath = 1L << 38,
	UserInfoPage = 1L << 39,
	UsePreviousUserInfo = 1L << 40,
	UninstallRestartComputer = 1L << 41,
	RestartIfNeededByRun = 1L << 42,
	ShowTasksTreeLines = 1L << 43,
	ShowLanguageDialog = 1L << 44,
	DetectLanguageUsingLocale = 1L << 45,
	AllowCancelDuringInstall = 1L << 46,
	WizardImageStretch = 1L << 47,
	AppendDefaultDirName = 1L << 48,
	AppendDefaultGroupName = 1L << 49,
	EncryptionUsed = 1L << 50,
	ChangesEnvironment = 1L << 51,
	ShowUndisplayableLanguages = 1L << 52,
	SetupLogging = 1L << 53,
	SignedUninstaller = 1L << 54,
	UsePreviousLanguage = 1L << 55,
	DisableWelcomePage = 1L << 56,
	CloseApplications = 1L << 57,
	RestartApplications = 1L << 58,
	AllowNetworkDrive = 1L << 59,
	ForceCloseApplications = 1L << 60,
	AppNameHasConsts = 1L << 61,
	UsePreviousPrivileges = 1L << 62,
	WizardResizable = 1UL << 63
}

/// <summary>6.0.0+ 的附加选项标志。</summary>
[Flags]
public enum InnoHeaderOptions2 {
	None = 0,
	UninstallLogging = 1 << 0,
	WizardModern = 1 << 1,
	WizardBorderStyled = 1 << 2,
	WizardKeepAspectRatio = 1 << 3,
	WizardLightButtonsUnstyled = 1 << 4,
	RedirectionGuard = 1 << 5,
	WizardBevelsHidden = 1 << 6
}

/// <summary>需要提升的权限级别。</summary>
public enum InnoPrivilegesRequired {
	None,
	PowerUser,
	Admin,
	Lowest
}

/// <summary>向导样式。</summary>
public enum InnoWizardStyle { Classic, Modern }

/// <summary>向导深色样式（6.6.0+）。</summary>
public enum InnoWizardDarkStyle { Light, Dark, Dynamic }

/// <summary>向导浅色控件样式（6.7.0+）。</summary>
public enum InnoWizardLightControlStyling { All, AllButButtons, OnlyRequired }

/// <summary>安装模式（/SILENT 相关）。</summary>
public enum InnoInstallMode { Normal, Silent, VerySilent }

/// <summary>卸载日志模式。</summary>
public enum InnoUninstallLogMode { Append, New, Overwrite }

/// <summary>语言检测方式。</summary>
public enum InnoLanguageDetection { UILanguage, Locale, None }

/// <summary>图片 alpha 格式。</summary>
public enum InnoAlphaFormat { Ignored, Defined, Premultiplied }

/// <summary>三态布尔。</summary>
public enum InnoAutoBool { Auto, No, Yes }

/// <summary>支持的操作系统架构。</summary>
[Flags]
public enum InnoArchitecture {
	Unknown = 1 << 0,
	X86 = 1 << 1,
	Amd64 = 1 << 2,
	Ia64 = 1 << 3,
	Arm32 = 1 << 4,
	Arm64 = 1 << 5
}

/// <summary>文件条目选项（对应 TSetupFileEntryOption）。</summary>
[Flags]
public enum InnoFileOptions : ulong {
	None = 0,
	ConfirmOverwrite = 1 << 0,
	NeverUninstall = 1 << 1,
	RestartReplace = 1 << 2,
	DeleteAfterInstall = 1 << 3,
	RegisterServer = 1 << 4,
	RegisterTypeLib = 1 << 5,
	SharedFile = 1 << 6,
	CompareTimeStamp = 1 << 7,
	FontIsNotTrueType = 1 << 8,
	SkipIfSourceDoesntExist = 1 << 9,
	OverwriteReadOnly = 1 << 10,
	OverwriteSameVersion = 1 << 11,
	CustomDestName = 1 << 12,
	OnlyIfDestFileExists = 1 << 13,
	NoRegError = 1 << 14,
	UninsRestartDelete = 1 << 15,
	OnlyIfDoesntExist = 1 << 16,
	IgnoreVersion = 1 << 17,
	PromptIfOlder = 1 << 18,
	DontCopy = 1 << 19,
	UninsRemoveReadOnly = 1 << 20,
	RecurseSubDirsExternal = 1 << 21,
	ReplaceSameVersionIfContentsDiffer = 1 << 22,
	DontVerifyChecksum = 1 << 23,
	UninsNoSharedFilePrompt = 1 << 24,
	CreateAllSubDirs = 1 << 25,
	Bits32 = 1 << 26,
	Bits64 = 1 << 27,
	ExternalSizePreset = 1 << 28,
	SetNtfsCompression = 1 << 29,
	UnsetNtfsCompression = 1 << 30,
	GacInstall = 1L << 31,
	Download = 1L << 32,
	ExtractArchive = 1L << 33,
	IsReadmeFile = 1L << 34
}

/// <summary>文件条目类型。</summary>
public enum InnoFileType { UserFile, UninstallerExe, RegServerExe }

/// <summary>文件验证方式（6.5.0+）。</summary>
public enum InnoFileVerification { None, Hash, IsSig }

/// <summary>数据条目选项（对应 TSetupFileLocationEntry.Flags）。</summary>
[Flags]
public enum InnoDataOptions {
	None = 0,
	VersionInfoValid = 1 << 0,
	VersionInfoNotValid = 1 << 1,
	BZipped = 1 << 2,
	TimeStampInUtc = 1 << 3,
	IsUninstallerExe = 1 << 4,
	CallInstructionOptimized = 1 << 5,
	Touch = 1 << 6,
	ChunkEncrypted = 1 << 7,
	ChunkCompressed = 1 << 8,
	SolidBreak = 1 << 9,
	Sign = 1 << 10,
	SignOnce = 1 << 11
}

/// <summary>签名模式（6.3.x）。</summary>
public enum InnoSignMode {
	NoSetting,
	Yes,
	Once,
	Check
}

/// <summary>目录条目选项。</summary>
[Flags]
public enum InnoDirectoryOptions {
	None = 0,
	NeverUninstall = 1 << 0,
	DeleteAfterInstall = 1 << 1,
	AlwaysUninstall = 1 << 2,
	SetNtfsCompression = 1 << 3,
	UnsetNtfsCompression = 1 << 4
}

/// <summary>密码校验方式（记录在 header 中）。</summary>
public enum InnoPasswordType {
	None,
	Crc32,
	Md5,
	Sha1,
	Pbkdf2Sha256XChaCha20
}