using System.Diagnostics;
using InnoUnpack.NET;
using InnoUnpack.NET.Compression;
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
 *   bench gc     <fixtures-dir>               分配与 GC 统计（冷进程）
 *   bench gcwarm <fixtures-dir>               池预热后的分配与 GC 统计
 *   bench crypto <size-mb>                    内存中 XChaCha20 流解密吞吐（SIMD 门禁）
 *   bench parallel <fixtures-dir>             独立 chunk 并行解码门禁（串行 vs 并发批量）
 *   bench decode <fixtures-dir> <fixture>     纯解码门禁（解码到最后一个文件末尾，不写盘）
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

if (mode == "crypto") {
	var sizeMb = args.Length > 1 && int.TryParse(args[1], out var m) ? m : 64;
	RunCryptoBench(sizeMb);
	return;
}

if (mode == "parallel") {
	RunParallelBench(dir);
	return;
}

if (mode == "decode") {
	RunDecodeBench(dir, only!);
	return;
}

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

void RunDecodeBench(string fixturesDir, string fixture) {
	// 纯解码门禁：解码到文件数据末尾（固体包 = 整条流），输出丢弃不写盘。
	// 与提取解耦，衡量 LZMA 解码器本身（含 ExeFilter 过滤）。
	var path = Path.Combine(fixturesDir, fixture);
	using var archive = InnoSetupArchive.Open(path);
	// 数据偏移最大的文件：其解码覆盖整条固体流前缀
	var target = archive.EnumerateFiles()
		.OrderByDescending(x => x.DataEntry.FileOffset + x.Size)
		.First();

	// 预热（JIT + 解码器池）
	Discard(archive, target);
	var best = double.MaxValue;
	long bestMs = 0;
	var bytes = 0L;
	var sink = new byte[256 * 1024];
	for (var r = 0; r < 7; r++) {
		var sw = Stopwatch.StartNew();
		bytes = Discard(archive, target, sink);
		sw.Stop();
		if (sw.Elapsed.TotalSeconds < best) {
			best = sw.Elapsed.TotalSeconds;
			bestMs = sw.ElapsedMilliseconds;
		}
	}

	Console.WriteLine($"DECODE {fixture}: {bestMs} ms ({bytes / 1048576.0 / best:F1} MiB/s)");
}

static long Discard(InnoSetupArchive archive, InnoArchiveFile target, byte[]? sink = null) {
	using var stream = archive.OpenFile(target);
	sink ??= new byte[256 * 1024];
	var total = 0L;
	while (true) {
		var n = stream.Read(sink);
		if (n <= 0) {
			break;
		}

		total += n;
	}

	return total;
}

void RunParallelBench(string fixturesDir) {
	// 独立 chunk 并行解码门禁：以各 fixture 的固体 chunk 作为独立工作项
	// （非固体安装包的每个 chunk 组与此等价），串行批量 vs 并发批量，同字节总数对比墙钟时间
	var paths = new[] {
		"isetup-4.2.7.exe",
		"innosetup-5.5.9-unicode.exe",
		"innosetup-5.6.1-unicode.exe",
		"innosetup-6.7.3.exe",
		"innosetup-7.0.2-x64.exe"
	}.Where(f => File.Exists(Path.Combine(fixturesDir, f)))
		.Select(f => Path.Combine(fixturesDir, f))
		.ToArray();
	if (paths.Length == 0) {
		Console.WriteLine("no fixtures");
		return;
	}

	var opts = new ExtractionOptions { VerifyChecksums = false, PreserveTimestamps = false };
	var dirs = paths.ToDictionary(p => p, p => Path.Combine(Path.GetTempPath(), "innobench-par", Path.GetFileNameWithoutExtension(p)));

	// 预热（解码器池、JIT）
	foreach (var p in paths) {
		using var a = InnoSetupArchive.Open(p);
		a.ExtractToDirectory(dirs[p], opts);
	}

	var serialBest = double.MaxValue;
	for (var r = 0; r < 3; r++) {
		var sw = Stopwatch.StartNew();
		foreach (var p in paths) {
			using var a = InnoSetupArchive.Open(p);
			a.ExtractToDirectory(dirs[p], opts);
		}

		sw.Stop();
		if (sw.Elapsed.TotalSeconds < serialBest) {
			serialBest = sw.Elapsed.TotalSeconds;
		}
	}

	var parallelBest = double.MaxValue;
	for (var r = 0; r < 3; r++) {
		var sw = Stopwatch.StartNew();
		var tasks = paths.Select(p => Task.Run(() => {
			using var a = InnoSetupArchive.Open(p);
			a.ExtractToDirectory(dirs[p], opts);
		})).ToArray();
		Task.WaitAll(tasks);
		sw.Stop();
		if (sw.Elapsed.TotalSeconds < parallelBest) {
			parallelBest = sw.Elapsed.TotalSeconds;
		}
	}

	Console.WriteLine(
		$"PARALLEL {paths.Length} chunks: serial={serialBest * 1000:F0}ms parallel={parallelBest * 1000:F0}ms speedup={serialBest / parallelBest:F2}x");
}

void RunCryptoBench(int sizeMb) {
	var key = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
	var nonce = Convert.FromHexString("202122232425262728292A2B2C2D2E2F3031323334353637");
	var size = sizeMb * 1024 * 1024;
	var data = new byte[size];
	new Random(1).NextBytes(data);
	var encrypted = new byte[size];
	using (XChaCha20Stream enc = new(new MemoryStream(data), key, nonce)) {
		enc.ReadExactly(encrypted);
	}

	// 预热
	using (XChaCha20Stream warm = new(new MemoryStream(encrypted), key, nonce)) {
		var probe = new byte[64 * 1024];
		var total = 0;
		while (total < size) {
			var n = warm.Read(probe);
			if (n <= 0) {
				break;
			}

			total += n;
		}
	}

	var best = double.MaxValue;
	for (var r = 0; r < 5; r++) {
		var sw = Stopwatch.StartNew();
		using XChaCha20Stream dec = new(new MemoryStream(encrypted), key, nonce);
		var probe = new byte[256 * 1024];
		var total = 0;
		while (total < size) {
			var n = dec.Read(probe);
			if (n <= 0) {
				break;
			}

			total += n;
		}

		sw.Stop();
		var secs = sw.Elapsed.TotalSeconds;
		if (secs > 0 && secs < best) {
			best = secs;
		}
	}

	Console.WriteLine($"CRYPTO {sizeMb} MiB: best {best * 1000:F0} ms ({size / 1048576.0 / best:F1} MiB/s)");
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
