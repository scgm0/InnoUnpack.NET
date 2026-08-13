using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
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

	/// <summary>并行提取时每个 worker 的独立切片读取器工厂；null（流方式打开）时并行回退为串行。</summary>
	private readonly Func<SliceReader>? _sliceReaderFactory;

	// 池化的校验哈希（MD5/SHA1/SHA256）：跨文件复用，Verify 后经 GetHashAndReset 复位
	private IncrementalHash? _hashPoolMd5;
	private IncrementalHash? _hashPoolSha1;
	private IncrementalHash? _hashPoolSha256;

	private (int Count, ulong Size)? _fileStats;

	private InnoSetupArchive(
		Stream stream,
		bool leaveOpen,
		SliceReader slices,
		InnoSetupInfo info,
		InnoFilenameConverter converter,
		InnoCrypto? crypto,
		Func<SliceReader>? sliceReaderFactory) {
		_stream = stream;
		_leaveOpen = leaveOpen;
		_slices = slices;
		Info = info;
		_converter = converter;
		_crypto = crypto;
		_sliceReaderFactory = sliceReaderFactory;
	}

	/// <summary>安装包元数据。</summary>
	public InnoSetupInfo Info { get; }

	/// <summary>
	///     可提取的文件总数（与 <see cref="EnumerateFiles()" /> 一致，含过滤后的全部条目）。
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
		_hashPoolMd5?.Dispose();
		_hashPoolSha1?.Dispose();
		_hashPoolSha256?.Dispose();
		_slices.Dispose();
		if (!_leaveOpen) {
			_stream.Dispose();
		}
	}

	/// <summary>
	///     创建文件校验器：MD5/SHA1/SHA256 复用池化实例（串行提取时；池非线程安全，
	///     并行路径 <paramref name="usePool" /> 为 false 时每文件新建），
	///     CRC32/Adler32 为轻量托管对象，每文件新建。
	/// </summary>
	private FileHasher? CreateFileHasher(InnoChecksumType type, bool usePool = true) {
		if (!usePool) {
			return FileHasher.Create(type);
		}

		return type switch {
			InnoChecksumType.Md5 => FileHasher.Create(type, _hashPoolMd5 ??= IncrementalHash.CreateHash(HashAlgorithmName.MD5)),
			InnoChecksumType.Sha1 =>
				FileHasher.Create(type, _hashPoolSha1 ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA1)),
			InnoChecksumType.Sha256 => FileHasher.Create(type,
				_hashPoolSha256 ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
			_ => FileHasher.Create(type)
		};
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
		ArgumentNullException.ThrowIfNull(stream);
		if (stream.CanSeek) {
			return IsInnoSetupCore(stream);
		}

		// 非 seekable 流：检测需随机访问 PE 头与签名，缓冲到内存后检测
		using var buffered = new MemoryStream();
		stream.CopyTo(buffered);
		buffered.Position = 0;
		return IsInnoSetupCore(buffered);
	}

	static private bool IsInnoSetupCore(Stream stream) {
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
	public static InnoSetupArchive Open(Stream stream, InnoOpenOptions? options = null, bool leaveOpen = false) =>
		OpenCore(stream, options, leaveOpen, installerPath: null);

	static private InnoSetupArchive OpenCore(Stream stream, InnoOpenOptions? options, bool leaveOpen, string? installerPath) {
		ArgumentNullException.ThrowIfNull(stream);
		options ??= new();

		if (!stream.CanSeek) {
			throw new ArgumentException("安装包流必须支持查找（Seek）", nameof(stream));
		}

		var (headerOffset, dataOffset) = SignatureFinder.Find(stream);
		stream.Position = (long)headerOffset;
		var info = InnoSetupInfo.Load(stream, options.ForceCodepage);

		SliceReader slices;
		Func<SliceReader>? sliceReaderFactory = null;
		if (dataOffset != 0) {
			// 单文件安装包：数据内嵌于 exe 中
			slices = SliceReader.CreateEmbedded(stream, dataOffset);
			if (installerPath is not null) {
				// 并行 worker：独立打开文件句柄（独立 Position，可并发读）
				sliceReaderFactory = () => SliceReader.CreateEmbeddedOwned(
					new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read),
					dataOffset);
			}
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
			// 外部切片模式：每个 worker 独立打开切片文件
			sliceReaderFactory = () => SliceReader.CreateExternal(dir, basename, basename2, info.Header.SlicesPerDisk);
		}

		var converter = new InnoFilenameConverter(options.PathMappings);

		// 校验密码（错误会抛出 InnoFormatException），并建立解密上下文
		var crypto = InnoCrypto.Create(info, options.Password);
		InnoSetupArchive archive = new(stream, leaveOpen, slices, info, converter, crypto, sliceReaderFactory);

		// 解析期预计算文件统计（FileCount/TotalFileSize 不再触发枚举）
		archive.GetFileStats();
		return archive;
	}

	/// <summary>异步打开安装包文件。</summary>
	/// <remarks>
	///     FileStream 创建（Windows 上可能被 Defender 等文件过滤器拦截阻塞）与元数据解析
	///     均在后台线程执行，调用线程不会被阻塞。
	/// </remarks>
	/// <exception cref="InnoFormatException">文件不是 Inno Setup 安装包或数据损坏。</exception>
	/// <exception cref="InnoUnsupportedException">安装包版本或特性不受支持。</exception>
	public static async Task<InnoSetupArchive> OpenAsync(
		string path,
		InnoOpenOptions? options = null,
		CancellationToken cancellationToken = default) {
		// 读取链（切片→解密→解压）全部为同步 Read：异步 FileStream 在 Windows 上会令同步 Read
		// 走 overlapped+等待（纯开销），此处不使用 FileOptions.Asynchronous
		var stream = await Task.Run(
			() => new FileStream(path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				4096,
				FileOptions.SequentialScan),
			cancellationToken).ConfigureAwait(false);
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
	/// <summary>
	///     按过滤条件列出安装包中的文件（与 <see cref="ExtractionOptions.FileFilter" /> 语义一致，
	///     可用于计算过滤后的进度总数）。
	/// </summary>
	public IEnumerable<InnoArchiveFile> EnumerateFiles(Func<InnoArchiveFile, bool> filter) {
		ArgumentNullException.ThrowIfNull(filter);
		foreach (var file in EnumerateFiles()) {
			if (filter(file)) {
				yield return file;
			}
		}
	}

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
				Attributes = entry.Attributes,
				Permission = entry.Permission,
				Options = entry.Options,
				Type = entry.Type,
				Entry = entry,
				DataEntry = data,
				Owner = this
			};
		}
	}

	/// <summary>异步列出安装包中的所有文件（等价于 <c>Task.Run(EnumerateFiles)</c>）。</summary>
	public Task<IReadOnlyList<InnoArchiveFile>> EnumerateFilesAsync(CancellationToken cancellationToken = default) {
		return Task.Run(IReadOnlyList<InnoArchiveFile> () => [.. EnumerateFiles()], cancellationToken);
	}

	/// <summary>异步按过滤条件列出安装包中的文件。</summary>
	public Task<IReadOnlyList<InnoArchiveFile>> EnumerateFilesAsync(
		Func<InnoArchiveFile, bool> filter,
		CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(filter);
		return Task.Run(IReadOnlyList<InnoArchiveFile> () => [.. EnumerateFiles(filter)], cancellationToken);
	}

	/// <summary>
	///     打开文件的解压数据流（调用方负责释放）。
	/// </summary>
	/// <exception cref="InnoUnsupportedException">文件数据已加密且未提供密码。</exception>
	public Stream OpenFile(InnoArchiveFile file) {
		ArgumentNullException.ThrowIfNull(file);
		if (!ReferenceEquals(file.Owner, this)) {
			throw new ArgumentException("文件条目不属于当前安装包", nameof(file));
		}

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
	///     异步打开文件的解压数据流（调用方负责释放）。
	///     解压流创建（chunk 定位/解密/解压初始化）为阻塞式 IO，在后台线程执行以避免阻塞调用方。
	/// </summary>
	/// <exception cref="InnoUnsupportedException">文件数据已加密且未提供密码。</exception>
	public Task<Stream> OpenFileAsync(InnoArchiveFile file, CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(file);
		return Task.Run(() => OpenFile(file), cancellationToken);
	}

	/// <summary>按输出路径查找文件条目（不存在返回 null）。</summary>
	public InnoArchiveFile? FindFile(string path) {
		ArgumentNullException.ThrowIfNull(path);
		foreach (var file in EnumerateFiles()) {
			if (string.Equals(file.Path, path, StringComparison.Ordinal)) {
				return file;
			}
		}

		return null;
	}

	/// <summary>按输出路径打开文件的解压数据流（调用方负责释放）。</summary>
	/// <exception cref="KeyNotFoundException">未找到指定路径的文件。</exception>
	public Stream OpenFile(string path) {
		var file = FindFile(path) ?? throw new KeyNotFoundException($"未找到文件：{path}");
		return OpenFile(file);
	}

	/// <summary>异步按输出路径打开文件的解压数据流（调用方负责释放）。</summary>
	/// <exception cref="KeyNotFoundException">未找到指定路径的文件。</exception>
	public Task<Stream> OpenFileAsync(string path, CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(path);
		return Task.Run(() => OpenFile(path), cancellationToken);
	}

	/// <summary>提取单个文件到目录（保留相对路径，自动创建父目录）。</summary>
	/// <exception cref="KeyNotFoundException">未找到指定路径的文件。</exception>
	public void ExtractFile(
		string path,
		string outputDirectory,
		ExtractionOptions? options = null,
		CancellationToken cancellationToken = default) {
		var file = FindFile(path) ?? throw new KeyNotFoundException($"未找到文件：{path}");
		ArgumentNullException.ThrowIfNull(outputDirectory);
		options ??= new();
		var outputRoot = Path.GetFullPath(outputDirectory);
		Directory.CreateDirectory(outputRoot);
		var (extracted, filesExtracted) = ExtractByChunk([file], outputRoot, options, cancellationToken);
		options.RaiseProgressChanged(extracted, filesExtracted, null);
	}

	/// <summary>
	///     异步提取单个文件到目录（保留相对路径，自动创建父目录）。
	///     解压为 CPU 密集工作，在后台线程执行（等价于 <c>Task.Run(ExtractFile)</c>）。
	/// </summary>
	/// <exception cref="KeyNotFoundException">未找到指定路径的文件。</exception>
	public Task ExtractFileAsync(
		string path,
		string outputDirectory,
		ExtractionOptions? options = null,
		CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(outputDirectory);
		return Task.Run(() => ExtractFile(path, outputDirectory, options, cancellationToken), cancellationToken);
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
	///     提取整个安装包到目录（可取消）。
	///     文件按 chunk 分组提取：同一 chunk 只解码一次（安装包通常为固体压缩，
	///     全部文件共享一个 chunk，逐文件重开会导致数十倍冗余解码）。
	///     写出顺序为 chunk/数据偏移顺序（与 innoextract 一致）。
	///     取消令牌在逐文件与文件内读写块边界检查；取消时抛出
	///     <see cref="OperationCanceledException" />，已完成的文件保留在输出目录。
	///     进度为绝对进度（当前字节数/文件数），总数可通过 <see cref="FileCount" /> 与
	///     <see cref="TotalFileSize" /> 获取，由调用方自行计算百分比。
	/// </summary>
	/// <exception cref="OperationCanceledException">取消令牌已触发。</exception>
	/// <exception cref="InnoUnsupportedException">存在加密文件且未提供密码支持。</exception>
	public void ExtractToDirectory(
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

		var (extracted, filesExtracted) = ExtractByChunk(files, outputRoot, options, cancellationToken);
		if (options.ApplyDirectoryAttributes) {
			ApplyDirectoryAttributes(outputRoot);
		}

		options.RaiseProgressChanged(extracted, filesExtracted, null);
	}

	/// <summary>
	///     异步提取整个安装包到目录。
	///     解压为 CPU 密集工作（LZMA range coder 串行位解码），无法以 I/O 方式真异步；
	///     本方法将整个提取流程卸载到线程池执行（等价于 Task.Run(ExtractToDirectory)），
	///     保证调用线程不被阻塞。取消令牌在逐文件与文件内读写块边界检查。
	///     进度为绝对进度（当前字节数/文件数），总数可通过 <see cref="FileCount" /> 与
	///     <see cref="TotalFileSize" /> 获取，由调用方自行计算百分比。
	///     进度回调在线程池线程触发（<see cref="ExtractionOptions.MaxParallelism" /> &gt; 1 时可能多线程交错），
	///     调用方需自行 marshal 到 UI 线程。
	/// </summary>
	/// <exception cref="InnoUnsupportedException">存在加密文件且未提供密码支持。</exception>
	public Task ExtractToDirectoryAsync(
		string outputDirectory,
		ExtractionOptions? options = null,
		CancellationToken cancellationToken = default) {
		ArgumentNullException.ThrowIfNull(outputDirectory);
		return Task.Run(() => ExtractToDirectory(outputDirectory, options, cancellationToken), cancellationToken);
	}

	/// <summary>
	///     按 chunk（起始切片, 偏移）分组文件，组间按 chunk 位置、组内按数据偏移排序。
	///     手动实现替代 LINQ GroupBy/OrderBy：提取启动期零闭包/排序器分配
	///     （<see cref="List{T}.Sort(Comparison{T})" /> 使用静态 lambda 原地排序）。
	///     组键唯一，组间顺序与 LINQ 版本一致；组内同偏移的重复条目数据相同，非稳定排序不影响结果。
	///     固体压缩的常见形态（全部文件共享一个 chunk）走快速路径：
	///     O(n) 扫描确认后直接原地排序，跳过字典与分组分配。
	/// </summary>
	static private List<KeyValuePair<(uint FirstSlice, ulong Offset), List<InnoArchiveFile>>> GroupFilesByChunk(
		List<InnoArchiveFile> files) {
		// 单 chunk 快速路径：全部文件共享同一 (FirstSlice, Offset) 时无需分组
		var first = files[0].DataEntry;
		var singleChunk = true;
		foreach (var file in files) {
			if (file.DataEntry.FirstSlice != first.FirstSlice || file.DataEntry.Offset != first.Offset) {
				singleChunk = false;
				break;
			}
		}

		if (singleChunk) {
			files.Sort(static (a, b) => a.DataEntry.FileOffset.CompareTo(b.DataEntry.FileOffset));
			return [new((first.FirstSlice, first.Offset), files)];
		}

		var groups = new Dictionary<(uint FirstSlice, ulong Offset), List<InnoArchiveFile>>(files.Count);
		foreach (var file in files) {
			var key = (file.DataEntry.FirstSlice, file.DataEntry.Offset);
			if (!groups.TryGetValue(key, out var chunkFiles)) {
				groups.Add(key, chunkFiles = []);
			}

			chunkFiles.Add(file);
		}

		var orderedGroups = new List<KeyValuePair<(uint FirstSlice, ulong Offset), List<InnoArchiveFile>>>(groups);
		orderedGroups.Sort(static (a, b) => {
			var slice = a.Key.FirstSlice.CompareTo(b.Key.FirstSlice);
			return slice != 0 ? slice : a.Key.Offset.CompareTo(b.Key.Offset);
		});

		foreach (var group in orderedGroups) {
			group.Value.Sort(static (a, b) => a.DataEntry.FileOffset.CompareTo(b.DataEntry.FileOffset));
		}

		return orderedGroups;
	}

	/// <summary>
	///     按 chunk 分组批量提取：同一 chunk 只打开/解码一次，
	///     组内文件按数据偏移（<see cref="InnoDataEntry.FileOffset" />）顺序流式取出。
	///     <see cref="ExtractionOptions.MaxParallelism" /> &gt; 1 且安装包经文件路径打开时，
	///     独立 chunk 组并行处理（固体包仅一组，不受影响）；组间完成顺序不定，进度事件可能交错。
	/// </summary>
	internal (ulong Extracted, int FilesExtracted) ExtractByChunk(
		List<InnoArchiveFile> files,
		string outputRoot,
		ExtractionOptions options,
		CancellationToken cancellationToken = default) {
		return ExtractByChunkGroups(GroupFilesByChunk(files), outputRoot, options, cancellationToken);
	}

	/// <summary>
	///     按给定 chunk 分组批量提取（<see cref="ExtractByChunk" /> 的内部调度入口，
	///     亦供测试以人工分组触发并行路径）。
	///     <see cref="ExtractionOptions.MaxParallelism" /> &gt; 1 且安装包经文件路径打开时，
	///     独立 chunk 组并行处理（固体包仅一组，不受影响）；组间完成顺序不定，进度事件可能交错。
	/// </summary>
	internal (ulong Extracted, int FilesExtracted) ExtractByChunkGroups(
		IReadOnlyList<KeyValuePair<(uint FirstSlice, ulong Offset), List<InnoArchiveFile>>> groups,
		string outputRoot,
		ExtractionOptions options,
		CancellationToken cancellationToken = default) {
		var parallelism = options.MaxParallelism;
		// 目录创建缓存：同一提取批次内已确认存在的目录跳过重复 CreateDirectory（15365 文件级联调用场景可省数万次 syscall）
		var dirCache = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
		if (parallelism <= 1 || groups.Count <= 1 || _sliceReaderFactory is null) {
			// 串行路径（默认）：逐组顺序提取
			long extracted = 0;
			var filesExtracted = 0;
			foreach (var group in groups) {
				ExtractChunkGroup(group.Value,
					outputRoot,
					options,
					cancellationToken,
					_slices,
					parallel: false,
					ref extracted,
					ref filesExtracted,
					dirCache);
			}

			return ((ulong)extracted, filesExtracted);
		}

		// 并行路径：独立 chunk 组并发解码（每 worker 独立切片读取器与解码器）
		long totalBytes = 0;
		var totalFiles = 0;
		Parallel.ForEachAsync(groups,
			new ParallelOptions {
				MaxDegreeOfParallelism = parallelism,
				CancellationToken = cancellationToken
			},
			(group, token) => {
				var workerSlices = _sliceReaderFactory!();
				try {
					ExtractChunkGroup(group.Value,
						outputRoot,
						options,
						token,
						workerSlices,
						parallel: true,
						ref totalBytes,
						ref totalFiles,
						dirCache);
				} finally {
					workerSlices.Dispose();
				}

				return ValueTask.CompletedTask;
			}).GetAwaiter().GetResult();

		return ((ulong)totalBytes, totalFiles);
	}

	/// <summary>
	///     提取单个 chunk 组内的全部文件（组内顺序与数据偏移一致）。
	///     并行模式进度累计使用原子操作（组间交错报告，绝对累计值精确）；
	///     <paramref name="dirCache" /> 为同一提取批次共享的目录创建缓存（线程安全）。
	/// </summary>
	private void ExtractChunkGroup(
		List<InnoArchiveFile> chunkFiles,
		string outputRoot,
		ExtractionOptions options,
		CancellationToken cancellationToken,
		SliceReader slices,
		bool parallel,
		ref long extracted,
		ref int filesExtracted,
		ConcurrentDictionary<string, byte> dirCache) {
		var chunk = ChunkReader.Open(slices, chunkFiles[0].DataEntry, _crypto, options.MaxParallelism);
		var chunkStream = chunk.Stream;
		long chunkPos = 0;
		// 提取缓冲：整个 chunk 批次复用一份（解压+写盘共用），ArrayPool 租用避免每批次分配
		var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
		try {
			foreach (var file in chunkFiles) {
				cancellationToken.ThrowIfCancellationRequested();

				if (options.FileFilter is not null && !options.FileFilter(file)) {
					continue; // 过滤掉的文件不参与进度（总数用 EnumerateFiles(filter) 计算，保持一致）
				}

				if (ShouldSkipTemporary(file, options)) {
					AddProgress(options, parallel, ref extracted, ref filesExtracted, 0, 1, file.Path);
					continue;
				}

				var target = ResolveOutputPath(outputRoot, file.Path);
				if (target is not null && options.OutputPathMapper is not null) {
					var mapped = options.OutputPathMapper(file);
					if (!string.IsNullOrEmpty(mapped)) {
						// 映射路径同样需通过安全校验，不安全时回退默认路径
						target = ResolveOutputPath(outputRoot, mapped) ?? target;
					}
				}

				if (target is null) {
					AddProgress(options, parallel, ref extracted, ref filesExtracted, 0, 1, file.Path); // 不安全路径，跳过
					continue;
				}

				EnsureParentDirectory(target, dirCache);

				if (File.Exists(target) && !options.Overwrite) {
					AddProgress(options, parallel, ref extracted, ref filesExtracted, (long)file.Size, 1, file.Path);
					continue;
				}

				// 定位到文件数据起点：组内偏移递增，只需前跳
				var fileOffset = (long)file.DataEntry.FileOffset;
				if (fileOffset < chunkPos) {
					// 防御：偏移回退（如同一数据被多个文件条目引用），重新打开 chunk
					chunk.Dispose();
					chunk = ChunkReader.Open(slices, file.DataEntry, _crypto, options.MaxParallelism);
					chunkStream = chunk.Stream;
					chunkPos = 0;
				}

				if (fileOffset > chunkPos) {
					SkipBytes(chunkStream, fileOffset - chunkPos);
					chunkPos = fileOffset;
				}

				using (var source = CreateFileSource(chunkStream, file))
				using (var output = File.OpenHandle(target,
					FileMode.Create,
					FileAccess.Write,
					FileShare.None,
					FileOptions.SequentialScan)) {
					var remaining = (long)file.Size;
					long writeOffset = 0;
					// 校验哈希池非线程安全：并行路径每文件新建（usePool = !parallel）
					using var hasher = options.VerifyChecksums
						? CreateFileHasher(file.DataEntry.Checksum.Type, !parallel)
						: null;
					while (remaining > 0) {
						cancellationToken.ThrowIfCancellationRequested();
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
						AddProgress(options, parallel, ref extracted, ref filesExtracted, n, 0, file.Path);
					}

					if (hasher is not null && !hasher.Verify(file.DataEntry.Checksum)) {
						throw new InnoFormatException($"文件校验和不匹配（数据可能已损坏）：{file.Path}");
					}
				}

				chunkPos += (long)file.Size;
				AddProgress(options, parallel, ref extracted, ref filesExtracted, 0, 1, file.Path);
				SetTimestamp(target, file.Timestamp, options);
				if (options.ApplyFileAttributes) {
					ApplyFileAttributes(target, file);
				}
			}
		} finally {
			ArrayPool<byte>.Shared.Return(buffer);
			chunk.Dispose();
		}
	}

	/// <summary>累计进度并触发事件（并行时原子累计，绝对累计值保证精确）。</summary>
	static private void AddProgress(
		ExtractionOptions options,
		bool parallel,
		ref long extracted,
		ref int filesExtracted,
		long bytesDelta,
		int filesDelta,
		string? currentFile) {
		long bytes;
		int files;
		if (parallel) {
			bytes = Interlocked.Add(ref extracted, bytesDelta);
			files = Interlocked.Add(ref filesExtracted, filesDelta);
		} else {
			extracted += bytesDelta;
			filesExtracted += filesDelta;
			bytes = extracted;
			files = filesExtracted;
		}

		options.RaiseProgressChanged((ulong)bytes, files, currentFile);
	}

	static private bool ShouldSkipTemporary(InnoArchiveFile file, ExtractionOptions options) =>
		!options.ExtractTemporaryFiles && (file.Options & InnoFileOptions.DeleteAfterInstall) != 0;

	static private void EnsureParentDirectory(string target, ConcurrentDictionary<string, byte> dirCache) {
		var dir = Path.GetDirectoryName(target)
			?? throw new InnoFormatException($"无法解析目标路径：{target}");
		// 目录已确认存在（本批次创建过）则跳过 CreateDirectory（省去重复 stat/mkdir syscall）
		if (dirCache.ContainsKey(dir)) {
			return;
		}

		Directory.CreateDirectory(dir);
		dirCache.TryAdd(dir, 0);
	}

	static private void SetTimestamp(string target, DateTime timestamp, ExtractionOptions options) {
		if (!options.PreserveTimestamps || timestamp == default) {
			return;
		}

		try {
			if (timestamp.Kind == DateTimeKind.Utc) {
				File.SetLastWriteTimeUtc(target, timestamp);
			} else {
				// 本地墙钟时间（TimeStampInUTC 未设置）：按本地时间写入，不做时区换算
				File.SetLastWriteTime(target, timestamp);
			}
		} catch (IOException) {
			// 忽略时间戳设置失败（如权限或文件系统限制）
		} catch (UnauthorizedAccessException) { }
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
	///     应用目录条目的权限与属性（提取完成后调用，避免只读目录阻碍文件写入）。
	/// </summary>
	private void ApplyDirectoryAttributes(string outputRoot) {
		ApplyDirectoryAttributes(outputRoot, Info.Directories, _converter);
	}

	/// <summary>
	///     应用目录条目的权限与属性：
	///     POSIX 权限（<see cref="InnoDirectoryEntry.Permission" /> ≥ 0）在非 Windows 平台应用；
	///     Windows 文件属性（<see cref="InnoDirectoryEntry.Attributes" /> 非 0）在 Windows 平台应用。
	///     应用失败（权限不足/平台不支持）静默忽略，不影响提取结果。
	/// </summary>
	internal static void ApplyDirectoryAttributes(
		string outputRoot,
		IEnumerable<InnoDirectoryEntry> directories,
		InnoFilenameConverter converter) {
		foreach (var directory in directories) {
			if ((directory.Options & InnoDirectoryOptions.DeleteAfterInstall) != 0) {
				continue; // 临时目录不应用
			}

			var path = converter.Convert(directory.Name);
			var target = ResolveOutputPath(outputRoot, path);
			if (target is null || !Directory.Exists(target)) {
				continue;
			}

			if (directory.Permission >= 0 && !OperatingSystem.IsWindows()) {
				try {
					File.SetUnixFileMode(target, (UnixFileMode)directory.Permission);
				} catch (Exception ex) when (
					  ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
					// 权限应用失败：静默忽略
				}
			}

			if (directory.Attributes != 0 && OperatingSystem.IsWindows()) {
				try {
					File.SetAttributes(target, (FileAttributes)directory.Attributes & ~FileAttributes.Directory);
				} catch (Exception ex) when (
					  ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
					// 属性应用失败：静默忽略
				}
			}
		}
	}

	/// <summary>
	///     应用单个文件条目的权限与属性（提取过程中逐文件调用，使用实际输出路径）：
	///     POSIX 权限在非 Windows 平台应用，Windows 文件属性在 Windows 平台应用。
	///     应用失败静默忽略，不影响提取结果。
	/// </summary>
	internal static void ApplyFileAttributes(string target, InnoArchiveFile file) {
		if (file.Permission >= 0 && !OperatingSystem.IsWindows()) {
			try {
				File.SetUnixFileMode(target, (UnixFileMode)file.Permission);
			} catch (Exception ex) when (
				  ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
				// 权限应用失败：静默忽略
			}
		}

		if (file.Attributes != 0 && OperatingSystem.IsWindows()) {
			try {
				File.SetAttributes(target, (FileAttributes)file.Attributes);
			} catch (Exception ex) when (
				  ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
				// 属性应用失败：静默忽略
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
		var buffer = ArrayPool<byte>.Shared.Rent(81920);
		try {
			while (count > 0) {
				var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
				if (n <= 0) {
					throw new InnoFormatException("chunk 数据不完整，无法跳过");
				}

				count -= n;
			}
		} finally {
			ArrayPool<byte>.Shared.Return(buffer);
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
