namespace InnoUnpack.NET;

/// <summary>
///     提取选项。
/// </summary>
public sealed class ExtractionOptions {
	/// <summary>是否保留文件时间戳（默认 true）。</summary>
	public bool PreserveTimestamps { get; set; } = true;

	/// <summary>是否校验文件校验和（MD5/SHA1/SHA256/CRC32/Adler32，默认 true）。</summary>
	public bool VerifyChecksums { get; set; } = true;

	/// <summary>是否创建目录表中的目录条目（默认 true）。</summary>
	public bool CreateDirectories { get; set; } = true;

	/// <summary>
	///     是否应用目录条目的权限与属性（默认 false）：POSIX 权限在非 Windows 平台应用，
	///     Windows 文件属性在 Windows 平台应用；在提取完成后应用（避免只读目录阻碍文件写入）。
	///     默认关闭：权限/属性可能使输出目录只读或隐藏，影响后续覆盖提取与清理；
	///     需要精确还原安装器元数据时显式开启。
	/// </summary>
	public bool ApplyDirectoryAttributes { get; set; }

	/// <summary>是否提取临时文件（DeleteAfterInstall，默认 true）。</summary>
	public bool ExtractTemporaryFiles { get; set; } = true;

	/// <summary>是否覆盖已存在的文件（默认 true）。</summary>
	public bool Overwrite { get; set; } = true;

	/// <summary>
	///     逐文件提取过滤：返回 false 的文件跳过提取（不参与进度统计——不计入已处理文件数、
	///     不触发进度事件；进度总数应使用 <see cref="InnoSetupArchive.EnumerateFiles(Func{InnoArchiveFile, bool})" />
	///     计算，完成时 <see cref="ExtractionProgress.FilesExtracted" /> 与之严格一致）。
	///     提取过程中对每个文件调用一次；为 null 时提取全部文件。
	///     可基于 <see cref="InnoArchiveFile" /> 的任意属性判断：
	///     输出路径（<c>f.Path.StartsWith("app/")</c>）、安装器原始路径
	///     （<c>f.Destination.StartsWith("{app}\\")</c>）、源文件名（<c>f.SourceName</c>）、
	///     大小（<c>f.Size</c>）等。
	/// </summary>
	public Func<InnoArchiveFile, bool>? FileFilter { get; set; }

	/// <summary>
	///     输出路径映射：返回替换默认输出路径的相对路径（可含子目录或重命名），null 表示使用默认路径。
	///     映射结果同样经过路径安全校验；不安全（绝对路径或逃逸输出目录）时回退默认路径。
	///     与 <see cref="InnoOpenOptions.PathMappings" /> 的区别：后者在打开时作用于 {app} 等常量，
	///     本映射在提取时作用于已展开的每个文件（可与 <see cref="FileFilter" /> 组合实现"只提取
	///     app 到指定文件夹"）。
	/// </summary>
	public Func<InnoArchiveFile, string?>? OutputPathMapper { get; set; }

	/// <summary>提取进度事件（绝对进度，同步触发）。</summary>
	public event Action<ExtractionProgress>? ProgressChanged;

	/// <summary>
	///     并行提取的最大 chunk 组并发数（默认 1 = 串行）。
	///     仅对非固体压缩（每文件独立 chunk）的安装包有收益：固体包全部文件共享一个 chunk，
	///     解码状态串行传递，无法并行。
	///     每个并发 worker 持有独立的切片读取器与解码器，内存约为
	///     workers ×（LZMA2 字典 8MiB 起 + 缓冲 ~1MiB）；大字典安装包（64MiB 级）需相应调低。
	///     并行时进度事件可能在多个线程交错触发（绝对累计值不变，调用方需线程安全）；
	///     通过 <see cref="InnoSetupArchive.Open(string, InnoOpenOptions)" /> 打开时并行生效，
	///     通过 <see cref="InnoSetupArchive.Open(System.IO.Stream, InnoOpenOptions, bool)" /> 打开时回退为串行。
	/// </summary>
	public int MaxParallelism { get; set; } = 1;

	/// <summary>触发进度事件（提取引擎内部调用）。</summary>
	internal void RaiseProgressChanged(ulong bytesExtracted, int filesExtracted, string? currentFileName) {
		ProgressChanged?.Invoke(new(bytesExtracted, filesExtracted, currentFileName));
	}
}

/// <summary>
///     提取进度信息
/// </summary>
/// <param name="BytesExtracted">当前已提取（含跳过与已存在文件计入）的字节数。</param>
/// <param name="FilesExtracted">当前已处理的文件数。</param>
/// <param name="CurrentFileName">当前正在提取的文件路径（null 表示提取开始/结束）。</param>
public readonly record struct ExtractionProgress(ulong BytesExtracted, int FilesExtracted, string? CurrentFileName);