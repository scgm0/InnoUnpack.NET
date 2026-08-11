using System.Diagnostics;
using InnoUnpack.NET;
using InnoUnpack.NET.Metadata;

/*
 * InnoUnpack.NET 性能基准。
 *
 * 用法：
 *   bench fair   <fixtures-dir> <fixture>     冷进程单次提取（与 innoextract 对齐：
 *                                             排除卸载程序、关闭校验/时间戳）
 *   bench fairloop <fixtures-dir> <fixture>   进程内预热后 best-of-7（库热路径）
 *   bench full   <fixtures-dir>               公共 API 全量提取 best-of-3（默认选项）
 *   bench verify <fixtures-dir>               公共 API + SHA256 校验
 *   bench gc     <fixtures-dir>               分配与 GC 统计
 */

var fixtures = new[] {
	"isetup-4.2.7.exe",
	"innosetup-5.5.9-unicode.exe",
	"innosetup-5.6.1-unicode.exe",
	"innosetup-6.7.3.exe",
	"innosetup-7.0.2-x64.exe",
};

var mode = args.Length > 0 ? args[0] : "full";
var dir = args.Length > 1 ? args[1] : ".";
var only = args.Length > 2 ? args[2] : null;

if (mode is "fair" or "fairloop") {
	RunFixture(only!, mode);
	return;
}

foreach (var f in fixtures) {
	var path = Path.Combine(dir, f);
	if (!File.Exists(path)) {
		Console.WriteLine($"[skip] {f}");
		continue;
	}

	using var archive = InnoSetupArchive.Open(path);
	List<InnoArchiveFile> files = [.. archive.EnumerateFiles()];
	Console.WriteLine($"== {f}: {files.Count} files, {archive.TotalFileSize:N0} bytes, compression={archive.Info.Header.Compression}");

	switch (mode) {
		case "full": {
			double best = double.MaxValue;
			long bestMs = 0;
			for (var r = 0; r < 3; r++) {
				var outDir = Path.Combine(Path.GetTempPath(), "innobench", f[..^4]);
				if (Directory.Exists(outDir)) {
					Directory.Delete(outDir, true);
				}

				var sw = Stopwatch.StartNew();
				archive.ExtractToDirectory(outDir);
				sw.Stop();
				Directory.Delete(outDir, true);
				if (sw.Elapsed.TotalSeconds < best) {
					best = sw.Elapsed.TotalSeconds;
					bestMs = sw.ElapsedMilliseconds;
				}
			}

			Console.WriteLine($"  full extract: {bestMs} ms  ({archive.TotalFileSize / 1048576.0 / best:F1} MiB/s)");
			break;
		}
		case "verify": {
			var outDir = Path.Combine(Path.GetTempPath(), "innobench-verify-" + f[..^4]);
			if (Directory.Exists(outDir)) {
				Directory.Delete(outDir, true);
			}

			var opts = new ExtractionOptions { VerifyChecksums = true };
			var sw = Stopwatch.StartNew();
			archive.ExtractToDirectory(outDir, opts);
			sw.Stop();
			Directory.Delete(outDir, true);
			Console.WriteLine($"  verify+extract: {sw.ElapsedMilliseconds} ms  ({archive.TotalFileSize / 1048576.0 / sw.Elapsed.TotalSeconds:F1} MiB/s)");
			break;
		}
		case "gcwarm": {
			// 池预热后（进程内第二次提取）的分配与 GC 统计
			var opts = new ExtractionOptions { VerifyChecksums = false };
			var warmDir = Path.Combine(Path.GetTempPath(), "innobench-gcwarm");
			if (Directory.Exists(warmDir)) {
				Directory.Delete(warmDir, true);
			}

			archive.ExtractToDirectory(warmDir, opts);
			Directory.Delete(warmDir, true);

			var beforeAlloc = GC.GetTotalAllocatedBytes();
			var g0 = GC.CollectionCount(0);
			var g1 = GC.CollectionCount(1);
			var g2 = GC.CollectionCount(2);
			var outDir = Path.Combine(Path.GetTempPath(), "innobench-gcwarm-run");
			if (Directory.Exists(outDir)) {
				Directory.Delete(outDir, true);
			}

			var sw = Stopwatch.StartNew();
			archive.ExtractToDirectory(outDir, opts);
			sw.Stop();
			Directory.Delete(outDir, true);
			var allocKB = (GC.GetTotalAllocatedBytes() - beforeAlloc) / 1024.0;
			Console.WriteLine($"  {sw.ElapsedMilliseconds} ms, warm-run allocated {allocKB:F0} KiB, gc0={GC.CollectionCount(0) - g0} gc1={GC.CollectionCount(1) - g1} gc2={GC.CollectionCount(2) - g2}");
			break;
		}
		case "gc": {
			var beforeAlloc = GC.GetTotalAllocatedBytes();
			var g0 = GC.CollectionCount(0);
			var g1 = GC.CollectionCount(1);
			var g2 = GC.CollectionCount(2);
			var outDir = Path.Combine(Path.GetTempPath(), "innobench-gc-" + f[..^4]);
			if (Directory.Exists(outDir)) {
				Directory.Delete(outDir, true);
			}

			var sw = Stopwatch.StartNew();
			archive.ExtractToDirectory(outDir);
			sw.Stop();
			Directory.Delete(outDir, true);
			var allocMB = (GC.GetTotalAllocatedBytes() - beforeAlloc) / 1048576.0;
			Console.WriteLine($"  {sw.ElapsedMilliseconds} ms, allocated {allocMB:F0} MiB, gc0={GC.CollectionCount(0) - g0} gc1={GC.CollectionCount(1) - g1} gc2={GC.CollectionCount(2) - g2}");
			break;
		}
	}
}

void RunFixture(string fixture, string mode) {
	var path = Path.Combine(dir, fixture);
	using var archive = InnoSetupArchive.Open(path);
	List<InnoArchiveFile> files = [.. archive.EnumerateFiles()];
	// 与 innoextract 对齐：不提取卸载程序（innoextract 默认跳过），关闭校验与时间戳
	var opts = new ExtractionOptions { VerifyChecksums = false, PreserveTimestamps = false };
	var filtered = files.Where(x => x.Type != InnoFileType.UninstallerExe).ToList();
	ulong bytes = 0;
	foreach (var x in filtered) {
		bytes += x.Size;
	}

	if (mode == "fair") {
		var outDir = Path.Combine(Path.GetTempPath(), "innobench-fair-" + fixture[..^4]);
		if (Directory.Exists(outDir)) {
			Directory.Delete(outDir, true);
		}

		var sw = Stopwatch.StartNew();
		archive.ExtractByChunk(filtered, outDir, opts);
		sw.Stop();
		Directory.Delete(outDir, true);
		Console.WriteLine($"FAIR {fixture}: files={filtered.Count} bytes={bytes:N0} {sw.ElapsedMilliseconds} ms ({bytes / 1048576.0 / sw.Elapsed.TotalSeconds:F1} MiB/s)");
		return;
	}

	// fairloop：进程内预热后 best-of-7
	var warm = Path.Combine(Path.GetTempPath(), "innobench-warm");
	if (Directory.Exists(warm)) {
		Directory.Delete(warm, true);
	}

	archive.ExtractByChunk(filtered, warm, opts);
	Directory.Delete(warm, true);
	var bestMs = long.MaxValue;
	for (var r = 0; r < 7; r++) {
		var outDir = Path.Combine(Path.GetTempPath(), "innobench-loop-" + r);
		if (Directory.Exists(outDir)) {
			Directory.Delete(outDir, true);
		}

		var sw = Stopwatch.StartNew();
		archive.ExtractByChunk(filtered, outDir, opts);
		sw.Stop();
		Directory.Delete(outDir, true);
		if (sw.ElapsedMilliseconds < bestMs) {
			bestMs = sw.ElapsedMilliseconds;
		}
	}

	Console.WriteLine($"FAIRLOOP {fixture}: files={filtered.Count} bytes={bytes:N0} best {bestMs} ms ({bytes / 1048576.0 / (bestMs / 1000.0):F1} MiB/s)");
}
