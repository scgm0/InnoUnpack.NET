using System.Diagnostics;
using InnoUnpack.NET;

namespace InnoUnpack.Tests;

/// <summary>
///     与 innoextract 的黄金差分测试：提取结果逐字节一致。
///     innoextract 不可用时（未安装 / 未设置 INNOEXTRACT_BIN）静默跳过。
/// </summary>
public class GoldenDiffTests {
	[Fact]
	public void ExtractionMatchesInnoextract() {
		var innoextract = FindInnoextract();
		if (innoextract is null) {
			return; // innoextract 不可用：跳过（CI 中安装后生效）
		}

		const string fixture = "innosetup-5.6.1-unicode.exe";
		Fixtures.SkipIfMissing(fixture);

		var root = Path.Combine(Path.GetTempPath(), "innounpack-golden");
		var libOut = Path.Combine(root, "lib");
		var innoOut = Path.Combine(root, "innoextract");
		if (Directory.Exists(root)) {
			Directory.Delete(root, true);
		}

		try {
			// 本库：默认选项（关闭时间戳以对齐）
			using var archive = InnoSetupArchive.Open(Fixtures.Get(fixture));
			archive.ExtractToDirectory(libOut, new ExtractionOptions { PreserveTimestamps = false });

			// innoextract：-e 提取、-s 静默、-T none 关闭时间戳
			var psi = new ProcessStartInfo(innoextract) {
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			psi.ArgumentList.Add("-e");
			psi.ArgumentList.Add("-s");
			psi.ArgumentList.Add("-T");
			psi.ArgumentList.Add("none");
			psi.ArgumentList.Add("-d");
			psi.ArgumentList.Add(innoOut);
			psi.ArgumentList.Add(Fixtures.Get(fixture));

			using var process = Process.Start(psi)!;
			process.WaitForExit();
			Assert.True(process.ExitCode == 0, "innoextract 退出码非 0");

			AssertDirectoriesEqual(libOut, innoOut);
		} finally {
			if (Directory.Exists(root)) {
				Directory.Delete(root, true);
			}
		}
	}

	static private string? FindInnoextract() {
		var env = Environment.GetEnvironmentVariable("INNOEXTRACT_BIN");
		if (!string.IsNullOrEmpty(env) && File.Exists(env)) {
			return env;
		}

		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
			var candidate = Path.Combine(dir, "innoextract");
			if (File.Exists(candidate)) {
				return candidate;
			}
		}

		return null;
	}

	/// <summary>递归比对两个目录：相对路径集合与文件内容逐字节一致。</summary>
	static private void AssertDirectoriesEqual(string expectedDir, string actualDir) {
		var expected = Directory.EnumerateFiles(expectedDir, "*", SearchOption.AllDirectories)
			.Select(f => Path.GetRelativePath(expectedDir, f))
			.OrderBy(p => p)
			.ToList();
		var actual = Directory.EnumerateFiles(actualDir, "*", SearchOption.AllDirectories)
			.Select(f => Path.GetRelativePath(actualDir, f))
			.OrderBy(p => p)
			.ToList();
		Assert.Equal(expected, actual);

		foreach (var relative in expected) {
			var a = File.ReadAllBytes(Path.Combine(expectedDir, relative));
			var b = File.ReadAllBytes(Path.Combine(actualDir, relative));
			Assert.True(a.AsSpan().SequenceEqual(b), $"文件内容不一致：{relative}");
		}
	}
}
