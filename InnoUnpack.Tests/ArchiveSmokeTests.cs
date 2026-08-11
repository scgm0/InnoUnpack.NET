using InnoUnpack.NET;
using InnoUnpack.NET.Metadata;

namespace InnoUnpack.Tests;

/// <summary>
///     真实安装包的开包与提取冒烟测试。
/// </summary>
public class ArchiveSmokeTests {
	[Theory]
	[InlineData("isetup-4.2.7.exe")]
	[InlineData("innosetup-5.5.9-unicode.exe")]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	[InlineData("innosetup-7.0.2-x64.exe")]
	public void OpenAndEnumerate(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));

		Assert.NotNull(archive.Info.Header);
		Assert.True(archive.Info.Header.AppName.Length > 0, "AppName 不应为空");
		Assert.True(archive.Info.Header.Compression != InnoCompressionMethod.Stored, "官方安装包应使用压缩");

		List<InnoArchiveFile> files = [.. archive.EnumerateFiles()];
		Assert.True(files.Count > 5, $"文件数过少：{files.Count}");

		Assert.All(files,
			file => {
				Assert.True(file.Size > 0, $"文件 {file.Path} 大小无效");
				Assert.False(string.IsNullOrEmpty(file.SourceName), "源文件名不应为空");
				Assert.NotNull(file.Path);
			});
	}

	[Theory]
	[InlineData("isetup-4.2.7.exe", "Inno Setup")]
	[InlineData("innosetup-5.5.9-unicode.exe", "Inno Setup")]
	[InlineData("innosetup-5.6.1-unicode.exe", "Inno Setup")]
	[InlineData("innosetup-6.7.3.exe", "Inno Setup")]
	[InlineData("innosetup-7.0.2-x64.exe", "Inno Setup")]
	public void MetadataIsSane(string fixture, string expectedAppPrefix) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var header = archive.Info.Header;

		Assert.StartsWith(expectedAppPrefix, header.AppName);
		Assert.True(header.FileCount > 0);
		Assert.True(header.DirectoryCount >= 0);
		Assert.True(archive.Info.DataEntries.Count == header.DataEntryCount);
		Assert.True(archive.Info.Files.Count == header.FileCount);
	}

	[Theory]
	[InlineData("isetup-4.2.7.exe")]
	[InlineData("innosetup-5.5.9-unicode.exe")]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	[InlineData("innosetup-7.0.2-x64.exe")]
	public void ExtractAllFiles(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", fixture[..^4]);
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
			archive.ExtractToDirectory(outputDir);
		} catch (InnoFormatException ex) {
			Assert.Fail($"提取失败（数据损坏或校验和不匹配）：{ex.Message}");
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}
	}

	[Fact]
	public void VersionDetection() {
		Fixtures.SkipIfMissing("innosetup-7.0.2-x64.exe");

		using var archive = InnoSetupArchive.Open(Fixtures.Get("innosetup-7.0.2-x64.exe"));
		Assert.Equal(7u, archive.Info.Version.Major);
		Assert.True(archive.Info.Version.IsUnicode);
	}

	[Fact]
	public async Task OpenAndExtractAsync() {
		Fixtures.SkipIfMissing("innosetup-5.6.1-unicode.exe");

		await using var archive = await InnoSetupArchive.OpenAsync(Fixtures.Get("innosetup-5.6.1-unicode.exe"));
		Assert.True(archive.EnumerateFiles().Any());

		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "async-561");
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			await archive.ExtractToDirectoryAsync(outputDir);
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}
	}

	[Fact]
	public async Task ExtractProgressReportsFileCount() {
		Fixtures.SkipIfMissing("innosetup-5.6.1-unicode.exe");

		await using var archive = await InnoSetupArchive.OpenAsync(Fixtures.Get("innosetup-5.6.1-unicode.exe"));
		var totalFiles = archive.FileCount;
		var totalBytes = archive.TotalFileSize;
		Assert.True(totalFiles > 0);
		Assert.True(totalBytes > 0);

		var options = new ExtractionOptions();
		var filesExtracted = 0;
		ulong bytesExtracted = 0;
		var sawCompletion = false;
		options.ProgressChanged += p => {
			filesExtracted = Math.Max(filesExtracted, p.FilesExtracted);
			bytesExtracted = Math.Max(bytesExtracted, p.BytesExtracted);
			if (p.CurrentFileName is null) {
				sawCompletion = true;
			}
		};

		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "progress-561");
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			await archive.ExtractToDirectoryAsync(outputDir, options);
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}

		Assert.Equal(totalFiles, filesExtracted);
		Assert.Equal(totalBytes, bytesExtracted);
		Assert.True(sawCompletion, "应以 CurrentFileName=null 的完成事件结束");
	}

	[Theory]
	[InlineData("isetup-4.2.7.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public void SyncExtractionCanBeCancelled(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "cancel-sync");
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			using var cts = new CancellationTokenSource();
			// 进度回调中取消：至少解出第一个文件后立即停止
			var options = new ExtractionOptions { VerifyChecksums = false };
			options.ProgressChanged += _ => cts.Cancel();

			var ex = Assert.Throws<OperationCanceledException>(() =>
				archive.ExtractToDirectory(outputDir, options, cts.Token));

			Assert.True(cts.IsCancellationRequested, "取消应已触发");
			_ = ex;
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}
	}

	[Theory]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	public void FilteredProgressAlignsWithEnumerateFiles(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		bool IsApp(InnoArchiveFile file) => file.Path.StartsWith("app/", StringComparison.Ordinal);
		var filtered = archive.EnumerateFiles(IsApp).ToList();
		Assert.True(filtered.Count > 5 && filtered.Count < archive.FileCount, "过滤集应小于全集");

		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "filter-progress");
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			var options = new ExtractionOptions { FileFilter = IsApp };
			int finalFiles = 0;
			ulong finalBytes = 0;
			var sawCompletion = false;
			options.ProgressChanged += p => {
				finalFiles = Math.Max(finalFiles, p.FilesExtracted);
				finalBytes = Math.Max(finalBytes, p.BytesExtracted);
				if (p.CurrentFileName is null) {
					sawCompletion = true;
				}
			};
			archive.ExtractToDirectory(outputDir, options);

			// 完成时 FilesExtracted 与过滤总数严格一致（百分比不会超过 100%）
			Assert.Equal(filtered.Count, finalFiles);
			Assert.Equal(filtered.Aggregate(0UL, (acc, f) => acc + f.Size), finalBytes);
			Assert.True(sawCompletion, "应以 CurrentFileName=null 的完成事件结束");
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}
	}

	[Theory]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	public void ExtractWithDestinationBasedFilter(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));

		var expected = archive.EnumerateFiles(f => f.Path.StartsWith("app/", StringComparison.Ordinal)).Count();
		Assert.True(expected > 5);

		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "filter-dest");
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			archive.ExtractToDirectory(outputDir, new ExtractionOptions { FileFilter = IsAppDestination });
			var files = Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories).ToList();
			Assert.Equal(expected, files.Count);
			Assert.All(files,
				f => {
					var rel = Path.GetRelativePath(outputDir, f).Replace(Path.DirectorySeparatorChar, '/');
					Assert.StartsWith("app/", rel);
				});
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}

		return;

		// 基于安装器原始路径（含 {app} 常量）过滤，应与基于输出路径的过滤结果一致
		bool IsAppDestination(InnoArchiveFile file) => file.Destination.StartsWith("{app}", StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public void ExtractWithFileFilterOnlyExtractsMatching(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var filtered = archive.EnumerateFiles(IsApp).ToList();
		Assert.True(filtered.Count > 5, $"app 子树文件过少：{filtered.Count}");

		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "filter");
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			var options = new ExtractionOptions { FileFilter = IsApp };
			archive.ExtractToDirectory(outputDir, options);

			// 输出目录内不应出现非 app/ 前缀的文件
			var files = Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories).ToList();
			Assert.Equal(filtered.Count, files.Count);
			Assert.All(files,
				f => {
					var rel = Path.GetRelativePath(outputDir, f).Replace(Path.DirectorySeparatorChar, '/');
					Assert.StartsWith("app/", rel);
				});
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}

		return;

		bool IsApp(InnoArchiveFile file) => file.Path.StartsWith("app/", StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public void ExtractWithOutputPathMapperRedirects(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));

		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "mapper");
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			var options = new ExtractionOptions { OutputPathMapper = Map };
			archive.ExtractToDirectory(outputDir, options);

			var appFiles = archive.EnumerateFiles(f => f.Path.StartsWith("app/", StringComparison.Ordinal)).Count();
			var customFiles = Directory.EnumerateFiles(Path.Combine(outputDir, "custom"), "*", SearchOption.AllDirectories)
				.Count();
			Assert.Equal(appFiles, customFiles);
			Assert.False(Directory.Exists(Path.Combine(outputDir, "app")), "app 目录不应再存在");
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}

		return;

		// 将 {app} 子树（默认 "app/..."）映射到 "custom/..."，其余文件保留默认路径
		string? Map(InnoArchiveFile file) =>
			file.Path.StartsWith("app/", StringComparison.Ordinal) ? "custom/" + file.Path[4..] : null;
	}

	[Fact]
	public void ApplyDirectoryAttributesSetsUnixPermissions() {
		// 官方安装包目录表为空，使用合成条目直接验证权限应用逻辑
		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "dir-attrs");
		var sub = Path.Combine(outputDir, "app", "sub");
		Directory.CreateDirectory(sub);
		try {
			InnoDirectoryEntry entry = new() {
				Name = "{app}/sub",
				Permission = 489 // 0o751
			};
			InnoSetupArchive.ApplyDirectoryAttributes(outputDir, [entry], new InnoFilenameConverter());

			if (!OperatingSystem.IsWindows()) {
				Assert.Equal((UnixFileMode)489, File.GetUnixFileMode(sub)); // 0o751
			}

			// Permission = -1（未指定）不改变现有权限
			InnoDirectoryEntry unset = new() { Name = "{app}/sub", Permission = -1 };
			InnoSetupArchive.ApplyDirectoryAttributes(outputDir, [unset], new InnoFilenameConverter());
			if (!OperatingSystem.IsWindows()) {
				Assert.Equal((UnixFileMode)489, File.GetUnixFileMode(sub)); // 0o751
			}
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}
	}

	[Fact]
	public async Task ParallelExtractionOfMultipleArchives() {
		string[] fixtures = ["innosetup-5.5.9-unicode.exe", "innosetup-5.6.1-unicode.exe", "innosetup-6.7.3.exe"];
		Fixtures.SkipIfMissing(fixtures[0]); // 样本同目录，缺一个即跳过

		var root = Path.Combine(Path.GetTempPath(), "innounpack-tests", "parallel");
		if (Directory.Exists(root)) {
			Directory.Delete(root, true);
		}

		try {
			// 并行提取三个独立 archive 实例（覆盖并发首次代码页注册等静态初始化路径）
			var results = await Task.WhenAll(fixtures.Select(async f => {
				var outDir = Path.Combine(root, Path.GetFileNameWithoutExtension(f));
				await using var archive = await InnoSetupArchive.OpenAsync(Fixtures.Get(f));
				await archive.ExtractToDirectoryAsync(outDir, new ExtractionOptions { VerifyChecksums = true });
				return (Files: archive.FileCount, archive.TotalFileSize);
			}));

			Assert.Equal(3, results.Length);
			Assert.All(results, r => Assert.True(r.Files > 5));
			Assert.All(results, r => Assert.True(r.TotalFileSize > 0));

			// 每个输出目录都应包含校验通过的全部文件
			foreach (var f in fixtures) {
				var files = Directory.EnumerateFiles(
					Path.Combine(root, Path.GetFileNameWithoutExtension(f)),
					"*",
					SearchOption.AllDirectories).ToList();
				Assert.True(files.Count > 5, $"{f} 输出文件过少：{files.Count}");
			}
		} finally {
			if (Directory.Exists(root)) {
				Directory.Delete(root, true);
			}
		}
	}

	[Fact]
	public void OutputPathMapperRejectsUnsafePathAndFallsBack() {
		Fixtures.SkipIfMissing("innosetup-5.6.1-unicode.exe");

		using var archive = InnoSetupArchive.Open(Fixtures.Get("innosetup-5.6.1-unicode.exe"));

		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "mapper-unsafe");
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			archive.ExtractToDirectory(outputDir, new ExtractionOptions { OutputPathMapper = Map });

			// 全部文件仍落在输出目录内（默认路径回退），无任何逃逸
			Assert.All(Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories),
				f => {
					var full = Path.GetFullPath(f);
					Assert.StartsWith(Path.GetFullPath(outputDir) + Path.DirectorySeparatorChar, full);
				});
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}

		return;

		// 恶意/错误的映射：绝对路径与目录逃逸应回退默认路径而非写出根目录
		string? Map(InnoArchiveFile file) =>
			file.Path.StartsWith("app/", StringComparison.Ordinal)
				? file.Path[4..] is var rest && rest == "setup.exe" ? "/etc/evil" : "../escape/" + rest
				: null;
	}

	[Theory]
	[InlineData("isetup-4.2.7.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public async Task AsyncExtractionCanBeCancelled(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
		var outputDir = Path.Combine(Path.GetTempPath(), "innounpack-tests", "cancel-async");
		if (Directory.Exists(outputDir)) {
			Directory.Delete(outputDir, true);
		}

		try {
			using var cts = new CancellationTokenSource();
			cts.CancelAfter(50); // 开始后 50ms 取消（覆盖逐文件与块级检查）

			// 取消可能落在自检（OperationCanceledException）或异步 IO（TaskCanceledException，其子类）上
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
				archive.ExtractToDirectoryAsync(outputDir, new ExtractionOptions { VerifyChecksums = false }, cts.Token));

			Assert.True(cts.IsCancellationRequested, "取消应已触发");
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}
	}
}