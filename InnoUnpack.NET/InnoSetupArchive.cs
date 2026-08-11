using InnoUnpack.NET.Compression;
using InnoUnpack.NET.Metadata;
using InnoUnpack.NET.Reading;

namespace InnoUnpack.NET;

/// <summary>
///     Inno Setup 安装包解压器（跨平台，纯托管实现）。
///     支持 Inno Setup 4.0 至 7.x 的安装包，包括全部压缩算法
///     （stored / zlib / bzip2 / lzma1 / lzma2）与多磁盘安装包。
/// </summary>
/// <example>
///     <code>
/// using var archive = InnoSetupArchive.Open("setup.exe");
/// foreach (var file in archive.EnumerateFiles())
/// {
///     Console.WriteLine($"{file.Path} ({file.Size} bytes)");
/// }
/// archive.ExtractToDirectory("output");
/// </code>
/// </example>
public sealed class InnoSetupArchive : IDisposable, IAsyncDisposable {
	private readonly InnoFilenameConverter _converter;
	private readonly InnoCrypto? _crypto;
	private readonly bool _leaveOpen;
	private readonly SliceReader _slices;
	private readonly Stream _stream;

	private (int Count, ulong Size)? _fileStats;

	private InnoSetupArchive(
		Stream stream,
		bool leaveOpen,
		SliceReader slices,
		InnoSetupInfo info,
		InnoFilenameConverter converter,
		InnoCrypto? crypto) {
		_stream = stream;
		_leaveOpen = leaveOpen;
		_slices = slices;
		Info = info;
		_converter = converter;
		_crypto = crypto;
	}

	/// <summary>安装包元数据。</summary>
	public InnoSetupInfo Info { get; }

	/// <summary>
	///     可提取的文件总数（与 <see cref="EnumerateFiles" /> 一致，含过滤后的全部条目）。
	///     首次访问时计算并缓存。
	/// </summary>
	public int FileCount => GetFileStats().Count;

	/// <summary>
	///     可提取的文件总大小（字节）。
	///     首次访问时计算并缓存。
	/// </summary>
	public ulong TotalFileSize => GetFileStats().Size;

	public ValueTask DisposeAsync() {
		Dispose();
		return ValueTask.CompletedTask;
	}

	public void Dispose() {
		_slices.Dispose();
		if (!_leaveOpen) {
			_stream.Dispose();
		}
	}

	private (int Count, ulong Size) GetFileStats() {
		if (_fileStats is not null) {
			return _fileStats.Value;
		}

		var count = 0;
		ulong size = 0;
		foreach (var file in EnumerateFiles()) {
			count++;
			size += file.Size;
		}

		_fileStats = (count, size);
		return _fileStats.Value;
	}

	/// <summary>
	///     检测流是否为受支持的 Inno Setup 安装包（无副作用，不改变流位置）。
	/// </summary>
	public static bool IsInnoSetup(Stream stream) {
		if (!stream.CanSeek) {
			return false;
		}

		var position = stream.Position;
		try {
			var offsets = SignatureFinder.Find(stream);
			stream.Position = (long)offsets.HeaderOffset;
			var signature = new byte[InnoVersionParser.SignatureSize];
			var n = stream.Read(signature, 0, signature.Length);
			if (n < 12) {
				return false;
			}

			var version = InnoVersionParser.Parse(signature);
			return version is { Major: >= 4, Is16Bit: false };
		} catch (InnoFormatException) {
			return false;
		} catch (IOException) {
			return false;
		} finally {
			stream.Position = position;
		}
	}

	/// <summary>
	///     异步检测流是否为受支持的 Inno Setup 安装包（无副作用，不改变流位置）。
	///     格式解析为阻塞式 IO，在后台线程执行以避免阻塞调用方。
	/// </summary>
	public static Task<bool> IsInnoSetupAsync(Stream stream, CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(stream);
		return Task.Run(() => IsInnoSetup(stream), cancellationToken);
	}

	/// <summary>打开安装包文件。</summary>
	/// <exception cref="InnoFormatException">文件不是 Inno Setup 安装包或数据损坏。</exception>
	/// <exception cref="InnoUnsupportedException">安装包版本或特性不受支持。</exception>
	public static InnoSetupArchive Open(string path, InnoOpenOptions? options = null) {
		FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		try {
			return OpenCore(stream, options, leaveOpen: false, installerPath: Path.GetFullPath(path));
		} catch {
			stream.Dispose();
			throw;
		}
	}

	/// <summary>从流打开安装包。</summary>
	/// <param name="stream">安装包数据流。</param>
	/// <param name="options">打开选项。</param>
	/// <param name="leaveOpen">关闭 archive 时是否保留流不关闭。</param>
	/// <exception cref="InnoUnsupportedException">
	///     多磁盘安装包（数据在外部 setup-N.bin 切片中）必须使用 <see cref="Open(string, InnoOpenOptions)"/> 打开。
	/// </exception>
	public static InnoSetupArchive Open(Stream stream, InnoOpenOptions? options = null, bool leaveOpen = false)
		=> OpenCore(stream, options, leaveOpen, installerPath: null);

	static private InnoSetupArchive OpenCore(Stream stream, InnoOpenOptions? options, bool leaveOpen, string? installerPath) {
		ArgumentNullException.ThrowIfNull(stream);
		options ??= new();

		if (!stream.CanSeek) {
			throw new ArgumentException("安装包流必须支持查找（Seek）", nameof(stream));
		}

		var offsets = SignatureFinder.Find(stream);
		stream.Position = (long)offsets.HeaderOffset;
		var info = InnoSetupInfo.Load(stream, options.ForceCodepage);

		SliceReader slices;
		if (offsets.DataOffset != 0) {
			// 单文件安装包：数据内嵌于 exe 中
			slices = SliceReader.CreateEmbedded(stream, offsets.DataOffset);
		} else {
			// 多磁盘安装包：数据位于外部 setup-N.bin 切片中
			if (installerPath is null) {
				throw new InnoUnsupportedException("多磁盘安装包（数据在外部 setup-N.bin 切片中）需要从文件路径打开");
			}
			var dir = Path.GetDirectoryName(installerPath)
				?? throw new InnoFormatException($"无法解析安装包路径：{installerPath}");
			var basename = Path.GetFileNameWithoutExtension(installerPath);
			var basename2 = info.Header.BaseFilename;
			// 4.1.7 之前优先使用头部记录的基础文件名（与 innoextract 一致）
			if (info.Version < new InnoVersion(4, 1, 7, 0, info.Version.IsUnicode, info.Version.IsIsx, false, true)
				&& !string.IsNullOrEmpty(basename2)) {
				(basename, basename2) = (basename2, basename);
			}
			slices = SliceReader.CreateExternal(dir, basename, basename2, info.Header.SlicesPerDisk);
		}

		var converter = new InnoFilenameConverter(options.PathMappings);

		// 校验密码（错误会抛出 InnoFormatException），并建立解密上下文
		var crypto = InnoCrypto.Create(info, options.Password);
		return new(stream, leaveOpen, slices, info, converter, crypto);
	}

	/// <summary>异步打开安装包文件。</summary>
	/// <exception cref="InnoFormatException">文件不是 Inno Setup 安装包或数据损坏。</exception>
	/// <exception cref="InnoUnsupportedException">安装包版本或特性不受支持。</exception>
	public static async Task<InnoSetupArchive> OpenAsync(
		string path,
		InnoOpenOptions? options = null,
		CancellationToken cancellationToken = default) {
		FileStream stream = new(path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.Read,
			4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		try {
			return await OpenAsync(stream, options, cancellationToken: cancellationToken).ConfigureAwait(false);
		} catch {
			await stream.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>异步从流打开安装包。</summary>
	/// <remarks>
	///     头部与元数据解析为阻塞式 IO（流式二进制读取），在后台线程执行以避免阻塞调用方；
	///     返回的实例可直接用于后续异步提取。
	/// </remarks>
	public static Task<InnoSetupArchive> OpenAsync(
		Stream stream,
		InnoOpenOptions? options = null,
		bool leaveOpen = false,
		CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(stream);
		return Task.Run(() => Open(stream, options, leaveOpen), cancellationToken);
	}

	/// <summary>
	///     列出安装包中的所有文件。
	/// </summary>
	/// <remarks>
	///     路径通过 <see cref="InnoFilenameConverter" /> 展开变量（{app} → "app"）并清理。
	/// </remarks>
	public IEnumerable<InnoArchiveFile> EnumerateFiles() {
		foreach (var entry in Info.Files) {
			if (entry.Location < 0 || entry.Location >= Info.DataEntries.Count) {
				continue; // 外部文件（不包含数据）
			}

			var data = Info.DataEntries[entry.Location];

			// 卸载程序（UninstExe）：5.x+ 无包内数据（由安装器生成），4.x 有数据则提取
			if (entry.Type == InnoFileType.UninstallerExe && data.FileSize == 0) {
				continue;
			}

			// UninstExe 无目标路径时使用 Inno Setup 的标准卸载程序名
			var destination = entry.Destination.Length > 0
				? entry.Destination
				: Path.Combine("{app}", "unins000.exe");
			var sourceName = entry.Source.Length > 0 ? entry.Source : Path.GetFileName(destination);
			yield return new() {
				SourceName = sourceName,
				Destination = destination,
				Path = _converter.Convert(destination),
				Size = data.FileSize,
				Timestamp = data.Timestamp,
				FileVersion = data.FileVersion,
				Options = entry.Options,
				Type = entry.Type,
				Entry = entry,
				DataEntry = data
			};
		}
	}

	/// <summary>
	///     打开文件的解压数据流（调用方负责释放）。
	/// </summary>
	/// <exception cref="InnoUnsupportedException">文件数据已加密且未提供密码。</exception>
	public Stream OpenFile(InnoArchiveFile file) {
		ArgumentNullException.ThrowIfNull(file);
		var chunk = ChunkReader.Open(_slices, file.DataEntry, _crypto);
		try {
			if (file.DataEntry.FileOffset > 0) {
				SkipBytes(chunk.Stream, (long)file.DataEntry.FileOffset);
			}

			Stream stream = new FileSliceStream(chunk.Stream, chunk, (long)file.DataEntry.FileSize);
			if ((file.DataEntry.Options & InnoDataOptions.CallInstructionOptimized) != 0) {
				// 反转调用指令优化（4.1.8+ 默认启用），还原原始可执行文件
				stream = ExeFilterStream.Create(stream, FilterModeFor(Info.Version));
			}

			return stream;
		} catch {
			chunk.Dispose();
			throw;
		}
	}

	/// <summary>
	///     从共享 chunk 解压流创建文件数据流（不持有 chunk 所有权）：
	///     限制读取范围为文件大小，并按需套用指令优化还原过滤器。
	/// </summary>
	private Stream CreateFileSource(Stream chunkStream, InnoArchiveFile file) {
		Stream stream = new FileSliceStream(chunkStream, null, (long)file.Size);
		if ((file.DataEntry.Options & InnoDataOptions.CallInstructionOptimized) != 0) {
			// 反转调用指令优化（4.1.8+ 默认启用），还原原始可执行文件
			stream = ExeFilterStream.Create(stream, FilterModeFor(Info.Version));
		}

		return stream;
	}

	/// <summary>按数据版本选择指令解码模式。</summary>
	static private ExeFilterStream.Mode FilterModeFor(InnoVersion version) {
		if (version < InnoVersion.From(5, 2, 0, version)) {
			return ExeFilterStream.Mode.Legacy;
		}

		if (version < InnoVersion.From(5, 3, 9, version)) {
			return ExeFilterStream.Mode.Blocked;
		}

		return ExeFilterStream.Mode.BlockedFlip;
	}

	/// <summary>
	///     提取整个安装包到目录。
	///     文件按 chunk 分组提取：同一 chunk 只解码一次（安装包通常为固体压缩，
	///     全部文件共享一个 chunk，逐文件重开会导致数十倍冗余解码）。
	///     写出顺序为 chunk/数据偏移顺序（与 innoextract 一致）。
	///     进度为绝对进度（当前字节数/文件数），总数可通过 <see cref="FileCount" /> 与
	///     <see cref="TotalFileSize" /> 获取，由调用方自行计算百分比。
	/// </summary>
	/// <exception cref="InnoUnsupportedException">存在加密文件且未提供密码支持。</exception>
	public void ExtractToDirectory(string outputDirectory, ExtractionOptions? options = null) {
		ArgumentNullException.ThrowIfNull(outputDirectory);
		options ??= new();
		var outputRoot = Path.GetFullPath(outputDirectory);
		Directory.CreateDirectory(outputRoot);

		if (options.CreateDirectories) {
			CreateDirectories(outputRoot);
		}

		List<InnoArchiveFile> files = [.. EnumerateFiles()];

		var (extracted, filesExtracted) = ExtractByChunk(files, outputRoot, options);
		options.RaiseProgressChanged(extracted, filesExtracted, null);
	}

	/// <summary>
	///     异步提取整个安装包到目录。
	///     文件按 chunk 分组提取：同一 chunk 只解码一次（安装包通常为固体压缩，
	///     全部文件共享一个 chunk，逐文件重开会导致数十倍冗余解码）。
	///     写出顺序为 chunk/数据偏移顺序（与 innoextract 一致）。
	///     文件数据写入使用异步 IO；解压与校验在调用线程上执行。
	///     进度为绝对进度（当前字节数/文件数），总数可通过 <see cref="FileCount" /> 与
	///     <see cref="TotalFileSize" /> 获取，由调用方自行计算百分比。
	/// </summary>
	/// <exception cref="InnoUnsupportedException">存在加密文件且未提供密码支持。</exception>
	public async Task ExtractToDirectoryAsync(
		string outputDirectory,
		ExtractionOptions? options = null,
		CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(outputDirectory);
		options ??= new();
		var outputRoot = Path.GetFullPath(outputDirectory);
		Directory.CreateDirectory(outputRoot);

		if (options.CreateDirectories) {
			CreateDirectories(outputRoot);
		}

		List<InnoArchiveFile> files = [.. EnumerateFiles()];

		var (extracted, filesExtracted) = await ExtractByChunkAsync(files, outputRoot, options, cancellationToken).ConfigureAwait(false);
		options.RaiseProgressChanged(extracted, filesExtracted, null);
	}

	/// <summary>
	///     按 chunk 分组批量提取（同步）：同一 chunk 只打开/解码一次，
	///     组内文件按数据偏移（<see cref="InnoDataEntry.FileOffset" />）顺序流式取出。
	/// </summary>
	internal (ulong Extracted, int FilesExtracted) ExtractByChunk(
		List<InnoArchiveFile> files, string outputRoot, ExtractionOptions options) {
		ulong extracted = 0;
		var filesExtracted = 0;

		foreach (var group in files
			         .GroupBy(file => (file.DataEntry.FirstSlice, file.DataEntry.Offset))
			         .OrderBy(g => g.Key.FirstSlice)
			         .ThenBy(g => g.Key.Offset)) {
			var chunk = ChunkReader.Open(_slices, group.First().DataEntry, _crypto);
			var chunkStream = chunk.Stream;
			long chunkPos = 0;
			// 提取缓冲：整个 chunk 批次复用一份（解压+写盘共用），避免每文件分配
			var buffer = new byte[81920];
			try {
				foreach (var file in group.OrderBy(f => f.DataEntry.FileOffset)) {
					if (ShouldSkipTemporary(file, options)) {
						filesExtracted++;
						ReportProgress(options, extracted, filesExtracted, file.Path);
						continue;
					}

					var target = ResolveOutputPath(outputRoot, file.Path);
					if (target is null) {
						filesExtracted++; // 不安全路径，跳过
						ReportProgress(options, extracted, filesExtracted, file.Path);
						continue;
					}

					EnsureParentDirectory(target);

					if (File.Exists(target) && !options.Overwrite) {
						extracted += file.Size;
						filesExtracted++;
						ReportProgress(options, extracted, filesExtracted, file.Path);
						continue;
					}

					// 定位到文件数据起点：组内偏移递增，只需前跳
					var fileOffset = (long)file.DataEntry.FileOffset;
					if (fileOffset < chunkPos) {
						// 防御：偏移回退（如同一数据被多个文件条目引用），重新打开 chunk
						chunk.Dispose();
						chunk = ChunkReader.Open(_slices, file.DataEntry, _crypto);
						chunkStream = chunk.Stream;
						chunkPos = 0;
					}

					if (fileOffset > chunkPos) {
						SkipBytes(chunkStream, fileOffset - chunkPos);
						chunkPos = fileOffset;
					}

					using (var source = CreateFileSource(chunkStream, file))
					using (var output = File.OpenHandle(target, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.SequentialScan)) {
						var remaining = (long)file.Size;
						long writeOffset = 0;
						using var hasher = options.VerifyChecksums
							? FileHasher.Create(file.DataEntry.Checksum.Type)
							: null;
						while (remaining > 0) {
							var toRead = (int)Math.Min(buffer.Length, remaining);
							var n = source.Read(buffer, 0, toRead);
							if (n <= 0) {
								throw new InnoFormatException($"文件数据不完整：{file.Path}");
							}

							// 直接 OS 写入（无 FileStream 内部缓冲分配）
							RandomAccess.Write(output, buffer.AsSpan(0, n), writeOffset);
							writeOffset += n;
							hasher?.Update(buffer.AsSpan(0, n));
							remaining -= n;
							extracted += (ulong)n;
							ReportProgress(options, extracted, filesExtracted, file.Path);
						}

						if (hasher is not null && !hasher.Verify(file.DataEntry.Checksum)) {
							throw new InnoFormatException($"文件校验和不匹配（数据可能已损坏）：{file.Path}");
						}
					}

					chunkPos += (long)file.Size;
					filesExtracted++;
					ReportProgress(options, extracted, filesExtracted, file.Path);
					SetTimestamp(target, file.Timestamp, options);
				}
			} finally {
				chunk.Dispose();
			}
		}

		return (extracted, filesExtracted);
	}

	/// <summary>
	///     按 chunk 分组批量提取（异步）：同一 chunk 只打开/解码一次，
	///     组内文件按数据偏移（<see cref="InnoDataEntry.FileOffset" />）顺序流式取出。
	/// </summary>
	internal async Task<(ulong Extracted, int FilesExtracted)> ExtractByChunkAsync(
		List<InnoArchiveFile> files,
		string outputRoot,
		ExtractionOptions options,
		CancellationToken cancellationToken) {
		ulong extracted = 0;
		var filesExtracted = 0;

		foreach (var group in files
			         .GroupBy(file => (file.DataEntry.FirstSlice, file.DataEntry.Offset))
			         .OrderBy(g => g.Key.FirstSlice)
			         .ThenBy(g => g.Key.Offset)) {
			var chunk = ChunkReader.Open(_slices, group.First().DataEntry, _crypto);
			var chunkStream = chunk.Stream;
			long chunkPos = 0;
			// 提取缓冲：整个 chunk 批次复用一份（解压+写盘共用），避免每文件分配
			var buffer = new byte[81920];
			try {
				foreach (var file in group.OrderBy(f => f.DataEntry.FileOffset)) {
					cancellationToken.ThrowIfCancellationRequested();

					if (ShouldSkipTemporary(file, options)) {
						filesExtracted++;
						ReportProgress(options, extracted, filesExtracted, file.Path);
						continue;
					}

					var target = ResolveOutputPath(outputRoot, file.Path);
					if (target is null) {
						filesExtracted++; // 不安全路径，跳过
						ReportProgress(options, extracted, filesExtracted, file.Path);
						continue;
					}

					EnsureParentDirectory(target);

					if (File.Exists(target) && !options.Overwrite) {
						extracted += file.Size;
						filesExtracted++;
						ReportProgress(options, extracted, filesExtracted, file.Path);
						continue;
					}

					// 定位到文件数据起点：组内偏移递增，只需前跳
					var fileOffset = (long)file.DataEntry.FileOffset;
					if (fileOffset < chunkPos) {
						// 防御：偏移回退（如同一数据被多个文件条目引用），重新打开 chunk
						chunk.Dispose();
						chunk = ChunkReader.Open(_slices, file.DataEntry, _crypto);
						chunkStream = chunk.Stream;
						chunkPos = 0;
					}

					if (fileOffset > chunkPos) {
						SkipBytes(chunkStream, fileOffset - chunkPos);
						chunkPos = fileOffset;
					}

					await using (var source = CreateFileSource(chunkStream, file))
					using (var output = File.OpenHandle(target,
						FileMode.Create,
						FileAccess.Write,
						FileShare.None,
						FileOptions.Asynchronous | FileOptions.SequentialScan)) {
						var remaining = (long)file.Size;
						long writeOffset = 0;
						using var hasher = options.VerifyChecksums
							? FileHasher.Create(file.DataEntry.Checksum.Type)
							: null;
						while (remaining > 0) {
							cancellationToken.ThrowIfCancellationRequested();
							var toRead = (int)Math.Min(buffer.Length, remaining);
							var n = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
							if (n <= 0) {
								throw new InnoFormatException($"文件数据不完整：{file.Path}");
							}

							// 直接 OS 异步写入（无 FileStream 内部缓冲分配）
							await RandomAccess.WriteAsync(output, buffer.AsMemory(0, n), writeOffset, cancellationToken).ConfigureAwait(false);
							writeOffset += n;
							hasher?.Update(buffer.AsSpan(0, n));
							remaining -= n;
							extracted += (ulong)n;
							ReportProgress(options, extracted, filesExtracted, file.Path);
						}

						if (hasher is not null && !hasher.Verify(file.DataEntry.Checksum)) {
							throw new InnoFormatException($"文件校验和不匹配（数据可能已损坏）：{file.Path}");
						}
					}

					chunkPos += (long)file.Size;
					filesExtracted++;
					ReportProgress(options, extracted, filesExtracted, file.Path);
					SetTimestamp(target, file.Timestamp, options);
				}
			} finally {
				chunk.Dispose();
			}
		}

		return (extracted, filesExtracted);
	}

	static private bool ShouldSkipTemporary(InnoArchiveFile file, ExtractionOptions options)
		=> !options.ExtractTemporaryFiles && (file.Options & InnoFileOptions.DeleteAfterInstall) != 0;

	static private void EnsureParentDirectory(string target) {
		var dir = Path.GetDirectoryName(target)
			?? throw new InnoFormatException($"无法解析目标路径：{target}");
		Directory.CreateDirectory(dir);
	}

	static private void SetTimestamp(string target, DateTime timestamp, ExtractionOptions options) {
		if (!options.PreserveTimestamps || timestamp == default) {
			return;
		}

		try {
			File.SetLastWriteTimeUtc(target, timestamp);
		} catch (IOException) {
			// 忽略时间戳设置失败（如权限或文件系统限制）
		} catch (UnauthorizedAccessException) { }
	}

	static private void ReportProgress(ExtractionOptions options, ulong extracted, int filesExtracted, string? currentFile) {
		options.RaiseProgressChanged(extracted, filesExtracted, currentFile);
	}

	private void CreateDirectories(string outputRoot) {
		foreach (var directory in Info.Directories) {
			if ((directory.Options & InnoDirectoryOptions.DeleteAfterInstall) != 0) {
				continue; // 临时目录
			}

			var path = _converter.Convert(directory.Name);
			var target = ResolveOutputPath(outputRoot, path);
			if (target is not null) {
				Directory.CreateDirectory(target);
			}
		}
	}

	/// <summary>
	///     将相对路径解析为输出根目录内的绝对路径。
	///     返回 null 表示路径不安全（逃逸或绝对路径）。
	/// </summary>
	static private string? ResolveOutputPath(string outputRoot, string relativePath) {
		if (string.IsNullOrEmpty(relativePath)) {
			return null;
		}

		if (Path.IsPathRooted(relativePath)) {
			return null;
		}

		try {
			var combined = Path.GetFullPath(Path.Combine(outputRoot, relativePath));
			if (combined == outputRoot ||
				!combined.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
				return null;
			}

			return combined;
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			return null;
		}
	}

	static private void SkipBytes(Stream stream, long count) {
		var buffer = new byte[81920];
		while (count > 0) {
			var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
			if (n <= 0) {
				throw new InnoFormatException("chunk 数据不完整，无法跳过");
			}

			count -= n;
		}
	}

	/// <summary>
	///     限制读取范围的文件数据流（chunk 为 null 时不持有 chunk 所有权，
	///     供按 chunk 批量提取时共享同一解压流使用）。
	/// </summary>
	sealed private class FileSliceStream(Stream inner, ChunkReader? chunk, long length) : Stream {
		private long _remaining = length;

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }

		public override int Read(byte[] buffer, int offset, int count) {
			if (_remaining <= 0) {
				return 0;
			}

			count = (int)Math.Min(count, _remaining);
			var n = inner.Read(buffer, offset, count);
			_remaining -= n;
			return n;
		}

		public override int Read(Span<byte> buffer) {
			if (_remaining <= 0) {
				return 0;
			}

			if (buffer.Length > _remaining) {
				buffer = buffer[..(int)_remaining];
			}

			var n = inner.Read(buffer);
			_remaining -= n;
			return n;
		}

		public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

		public override void SetLength(long value) { throw new NotSupportedException(); }

		public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

		override protected void Dispose(bool disposing) {
			if (disposing) {
				chunk?.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}