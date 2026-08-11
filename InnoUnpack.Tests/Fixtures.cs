namespace InnoUnpack.Tests;

sealed class SkipException(string message) : Exception(message);

/// <summary>
///     测试用共享数据（真实 Inno Setup 安装包样本）。
///     通过 tools/download-fixtures.sh（Windows: tools/download-fixtures.ps1）下载，不入库。
/// </summary>
static class Fixtures {

	public static readonly (string File, string ExpectedVersion, bool Unicode)[] Installers = [
		("isetup-4.2.7.exe", "4.2.6", false),
		("innosetup-5.5.9-unicode.exe", "5.5.7", true),
		("innosetup-5.6.1-unicode.exe", "5.5.8", true),
		("innosetup-6.7.3.exe", "6.7.0", true),
		("innosetup-7.0.2-x64.exe", "7.0.0.3", true)
	];

	public static string Directory {
		get {
			var root = AppContext.BaseDirectory;
			// 从输出目录向上找 Fixtures
			DirectoryInfo? dir = new(root);
			while (dir is not null) {
				var candidate = Path.Combine(dir.FullName, "Fixtures");
				if (System.IO.Directory.Exists(candidate)) {
					return candidate;
				}

				dir = dir.Parent;
			}

			throw new FileNotFoundException("未找到 Fixtures 目录，请先运行 tools/download-fixtures.sh（Windows: tools/download-fixtures.ps1）");
		}
	}

	public static string Get(string name) { return Path.Combine(Directory, name); }

	public static bool Exists(string name) { return File.Exists(Get(name)); }

	/// <summary>缺少样本时跳过测试。</summary>
	public static IDisposable SkipIfMissing(string fixture) {
		if (!Exists(fixture)) {
			throw new SkipException($"缺少样本 {fixture}，请运行 tools/download-fixtures.sh（Windows: tools/download-fixtures.ps1）");
		}

		return NullScope.Instance;
	}

	sealed private class NullScope : IDisposable {
		public static readonly NullScope Instance = new();
		public void Dispose() { }
	}
}