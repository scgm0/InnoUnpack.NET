using InnoUnpack.NET.Metadata;

namespace InnoUnpack.NET;

/// <summary>
///     打开安装包的选项。
/// </summary>
public sealed class InnoOpenOptions {
	/// <summary>
	///     ANSI 安装包强制使用的代码页（Windows 代码页号），0 表示自动推断。
	///     Unicode 安装包忽略此选项。
	/// </summary>
	public int ForceCodepage { get; set; }

	/// <summary>
	///     安装包密码（用于解密加密安装包的文件数据）。
	///     密码错误时 <see cref="InnoSetupArchive.Open(string, InnoOpenOptions)" /> 抛出
	///     <see cref="InnoFormatException" />；未提供密码时打开成功，但提取加密文件会抛出
	///     <see cref="InnoUnsupportedException" />。
	/// </summary>
	public string? Password { get; set; }

	/// <summary>
	///     额外的路径变量映射（如 {"app": ""} 将 {app} 展开为根目录）。
	///     未映射的变量保持名称本身（与 innoextract 行为一致）。
	/// </summary>
	public IReadOnlyDictionary<string, string>? PathMappings { get; set; }
}

/// <summary>
///     安装包中的文件条目（公开视图）。
/// </summary>
public sealed class InnoArchiveFile {
	/// <summary>源文件名。</summary>
	public string SourceName { get; internal init; } = string.Empty;

	/// <summary>存储的目标路径（可能包含 {app} 等常量，Windows 风格分隔符）。</summary>
	public string Destination { get; internal set; } = string.Empty;

	/// <summary>展开变量后的输出相对路径。</summary>
	public string Path { get; internal init; } = string.Empty;

	/// <summary>解压后大小（字节）。</summary>
	public ulong Size { get; internal init; }

	/// <summary>文件时间戳（UTC）。</summary>
	public DateTime Timestamp { get; internal init; }

	/// <summary>文件版本（64 位）。</summary>
	public ulong FileVersion { get; internal set; }

	/// <summary>选项标志。</summary>
	public InnoFileOptions Options { get; internal init; }

	/// <summary>文件类型。</summary>
	public InnoFileType Type { get; internal set; } = InnoFileType.UserFile;

	/// <summary>内部：原始文件条目。</summary>
	internal InnoFileEntry Entry { get; set; } = new();

	/// <summary>内部：对应的数据条目。</summary>
	internal InnoDataEntry DataEntry { get; init; } = new();
}