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

		Progress<ExtractionProgress> progress = new();
		var filesExtracted = 0;
		ulong bytesExtracted = 0;
		var sawCompletion = false;
		progress.ProgressChanged += (_, p) => {
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
			await archive.ExtractToDirectoryAsync(outputDir, new() { Progress = progress });
		} finally {
			if (Directory.Exists(outputDir)) {
				Directory.Delete(outputDir, true);
			}
		}

		Assert.Equal(totalFiles, filesExtracted);
		Assert.Equal(totalBytes, bytesExtracted);
		Assert.True(sawCompletion, "应以 CurrentFileName=null 的完成事件结束");
	}
}