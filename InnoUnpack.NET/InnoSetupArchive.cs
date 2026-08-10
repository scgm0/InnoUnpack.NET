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
		if (_fileStats is null) {
			var count = 0;
			ulong size = 0;
			foreach (var file in EnumerateFiles()) {
				count++;
				size += file.Size;
			}

			_fileStats = (count, size);
		}

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
			return Open(stream, options);
		} catch {
			stream.Dispose();
			throw;
		}
	}

	/// <summary>从流打开安装包。</summary>
	/// <param name="stream">安装包数据流。</param>
	/// <param name="options">打开选项。</param>
	/// <param name="leaveOpen">关闭 archive 时是否保留流不关闭。</param>
	public static InnoSetupArchive Open(Stream stream, InnoOpenOptions? options = null, bool leaveOpen = false) {
		ArgumentNullException.ThrowIfNull(stream);
		options ??= new();

		if (!stream.CanSeek) {
			throw new ArgumentException("安装包流必须支持查找（Seek）", nameof(stream));
		}

		var offsets = SignatureFinder.Find(stream);
		stream.Position = (long)offsets.HeaderOffset;
		var info = InnoSetupInfo.Load(stream, options.ForceCodepage);

		// chunk 数据区从头部解析结束处开始（签名搜索路径无 offsets 表时）
		var dataOffset = offsets.DataOffset != 0 ? (long)offsets.DataOffset : info.DataOffset;
		InnoFilenameConverter converter = new(options.PathMappings);
		var slices = SliceReader.CreateEmbedded(stream, (ulong)dataOffset);

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

			if (entry.Type == InnoFileType.UninstallerExe) {
				continue; // 卸载程序文件
			}

			var data = Info.DataEntries[entry.Location];
			var sourceName = entry.Source.Length > 0 ? entry.Source : Path.GetFileName(entry.Destination);
			yield return new() {
				SourceName = sourceName,
				Destination = entry.Destination,
				Path = _converter.Convert(entry.Destination),
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

	/// <summary>按数据版本选择指令解码模式。</summary>
	static private ExeFilterStream.Mode FilterModeFor(InnoVersion version) {
		if (version < new InnoVersion(5, 2, 0, 0, version.IsUnicode, version.IsIsx, false, true)) {
			return ExeFilterStream.Mode.Legacy;
		}

		if (version < new InnoVersion(5, 3, 9, 0, version.IsUnicode, version.IsIsx, false, true)) {
			return ExeFilterStream.Mode.Blocked;
		}

		return ExeFilterStream.Mode.BlockedFlip;
	}

	/// <summary>
	///     提取整个安装包到目录。
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

		ulong extracted = 0;
		var filesExtracted = 0;
		foreach (var file in files) {
			if (!options.ExtractTemporaryFiles
				&& (file.Options & InnoFileOptions.DeleteAfterInstall) != 0) {
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

			Directory.CreateDirectory(Path.GetDirectoryName(target)!);

			if (File.Exists(target) && !options.Overwrite) {
				extracted += file.Size;
				filesExtracted++;
				ReportProgress(options, extracted, filesExtracted, file.Path);
				continue;
			}

			using (var source = OpenFile(file))
			using (FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None)) {
				var buffer = new byte[81920];
				var remaining = (long)file.Size;
				using var hasher = options.VerifyChecksums
					? FileHasher.Create(file.DataEntry.Checksum.Type)
					: null;
				while (remaining > 0) {
					var toRead = (int)Math.Min(buffer.Length, remaining);
					var n = source.Read(buffer, 0, toRead);
					if (n <= 0) {
						throw new InnoFormatException($"文件数据不完整：{file.Path}");
					}

					output.Write(buffer, 0, n);
					hasher?.Update(buffer.AsSpan(0, n));
					remaining -= n;
					extracted += (ulong)n;
					ReportProgress(options, extracted, filesExtracted, file.Path);
				}

				if (hasher is not null && !hasher.Verify(file.DataEntry.Checksum)) {
					throw new InnoFormatException($"文件校验和不匹配（数据可能已损坏）：{file.Path}");
				}
			}

			filesExtracted++;
			ReportProgress(options, extracted, filesExtracted, file.Path);

			if (options.PreserveTimestamps && file.Timestamp != default) {
				try {
					File.SetLastWriteTimeUtc(target, file.Timestamp);
				} catch (IOException) {
					// 忽略时间戳设置失败（如权限或文件系统限制）
				} catch (UnauthorizedAccessException) { }
			}
		}

		options.Progress?.Report(new(extracted, filesExtracted, null));
	}

	/// <summary>
	///     异步提取整个安装包到目录。
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

		ulong extracted = 0;
		var filesExtracted = 0;
		foreach (var file in files) {
			cancellationToken.ThrowIfCancellationRequested();

			if (!options.ExtractTemporaryFiles
				&& (file.Options & InnoFileOptions.DeleteAfterInstall) != 0) {
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

			Directory.CreateDirectory(Path.GetDirectoryName(target)!);

			if (File.Exists(target) && !options.Overwrite) {
				extracted += file.Size;
				filesExtracted++;
				ReportProgress(options, extracted, filesExtracted, file.Path);
				continue;
			}

			await using (var source = OpenFile(file))
			await using (FileStream output = new(target,
				FileMode.Create,
				FileAccess.Write,
				FileShare.None,
				81920,
				FileOptions.Asynchronous | FileOptions.SequentialScan)) {
				var buffer = new byte[81920];
				var remaining = (long)file.Size;
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

					await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
					hasher?.Update(buffer.AsSpan(0, n));
					remaining -= n;
					extracted += (ulong)n;
					ReportProgress(options, extracted, filesExtracted, file.Path);
				}

				if (hasher is not null && !hasher.Verify(file.DataEntry.Checksum)) {
					throw new InnoFormatException($"文件校验和不匹配（数据可能已损坏）：{file.Path}");
				}
			}

			filesExtracted++;
			ReportProgress(options, extracted, filesExtracted, file.Path);

			if (options.PreserveTimestamps && file.Timestamp != default) {
				try {
					File.SetLastWriteTimeUtc(target, file.Timestamp);
				} catch (IOException) {
					// 忽略时间戳设置失败（如权限或文件系统限制）
				} catch (UnauthorizedAccessException) { }
			}
		}

		options.Progress?.Report(new(extracted, filesExtracted, null));
	}

	static private void ReportProgress(ExtractionOptions options, ulong extracted, int filesExtracted, string? currentFile) {
		options.Progress?.Report(new(extracted, filesExtracted, currentFile));
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
	///     限制读取范围的文件数据流（持有 chunk 的所有权）。
	/// </summary>
	sealed private class FileSliceStream(Stream inner, ChunkReader chunk, long length) : Stream {
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
				chunk.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}