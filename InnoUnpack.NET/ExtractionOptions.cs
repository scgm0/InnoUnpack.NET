namespace InnoUnpack.NET;

/// <summary>
///     提取选项。
/// </summary>
public sealed class ExtractionOptions {
	/// <summary>是否保留文件时间戳（默认 true）。</summary>
	public bool PreserveTimestamps { get; set; } = true;

	/// <summary>是否校验文件校验和（MD5/SHA1/SHA256，默认 true）。</summary>
	public bool VerifyChecksums { get; set; } = true;

	/// <summary>是否创建目录表中的目录条目（默认 true）。</summary>
	public bool CreateDirectories { get; set; } = true;

	/// <summary>是否提取临时文件（DeleteAfterInstall，默认 true）。</summary>
	public bool ExtractTemporaryFiles { get; set; } = true;

	/// <summary>是否覆盖已存在的文件（默认 true）。</summary>
	public bool Overwrite { get; set; } = true;

	/// <summary>提取进度事件（绝对进度，同步触发）。</summary>
	public event Action<ExtractionProgress>? ProgressChanged;

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