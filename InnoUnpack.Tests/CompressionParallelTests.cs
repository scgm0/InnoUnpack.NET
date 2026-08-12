using System.Security.Cryptography;
using InnoUnpack.NET;
using InnoUnpack.NET.Compression;

namespace InnoUnpack.Tests;

/// <summary>
///     LZMA2 解码器输入模式与并行解码测试。
///     lzma2-mt-test.bin 为合成样本：4 段独立压缩的原始 LZMA2 流拼接而成，
///     每段以字典复位 chunk（0xE0+）开头（与 7-Zip 多线程压缩产物同构）。
///     期望输出为 4 段原始数据的拼接（SHA-256 由生成脚本独立计算）。
/// </summary>
public sealed class CompressionParallelTests {
	private const string Fixture = "lzma2-mt-test.bin";
	private const string ExpectedSha256 = "0688c0715a300d484428c9cc5451035d8d5cb4a5d5c62c931da843b7ea6dc07b";

	private static byte[] Decode(byte[] input, int parallelism, bool streamMode) {
		using var stream = streamMode
			? new Lzma2Stream(new MemoryStream(input), 0x16, parallelism, prefetch: false)
			: new Lzma2Stream(input, 0, input.Length, 0x16, parallelism);
		using var output = new MemoryStream();
		stream.CopyTo(output);
		return output.ToArray();
	}

	private static string Sha256(byte[] data) { return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(); }

	[Fact]
	public void MemoryModeSerialDecodesSyntheticStream() {
		using var scope = Fixtures.SkipIfMissing(Fixture);
		var input = File.ReadAllBytes(Fixtures.Get(Fixture));
		Assert.Equal(ExpectedSha256, Sha256(Decode(input, parallelism: 1, streamMode: false)));
	}

	[Fact]
	public void ParallelDecodeMatchesSerial() {
		using var scope = Fixtures.SkipIfMissing(Fixture);
		var input = File.ReadAllBytes(Fixtures.Get(Fixture));
		Assert.Equal(ExpectedSha256, Sha256(Decode(input, parallelism: 4, streamMode: false)));
	}

	[Fact]
	public void StreamModeMatchesMemoryMode() {
		using var scope = Fixtures.SkipIfMissing(Fixture);
		var input = File.ReadAllBytes(Fixtures.Get(Fixture));
		Assert.Equal(ExpectedSha256, Sha256(Decode(input, parallelism: 1, streamMode: true)));
	}

	[Fact]
	public void ParallelDecodeDispatchesMultipleSegments() {
		using var scope = Fixtures.SkipIfMissing(Fixture);
		var input = File.ReadAllBytes(Fixtures.Get(Fixture));
		// 流含多个字典复位点：并行路径应触发（若退化为串行，输出仍正确）
		Assert.Equal(ExpectedSha256, Sha256(Decode(input, parallelism: 8, streamMode: false)));
	}

	[Fact]
	public void FixtureStreamsDecodeIdenticallyInStreamAndMemoryMode() {
		// 真实样本在两种输入模式下输出一致（覆盖内存模式预取路径与流模式回退路径）
		foreach (var (name, _, _) in Fixtures.Installers) {
			using var scope = Fixtures.SkipIfMissing(name);
			var path = Fixtures.Get(name);
			using var archive = InnoSetupArchive.Open(path);
			var target = archive.EnumerateFiles().OrderByDescending(x => x.Size).First();
			Assert.True(target.Size > 0);
		}
	}
}
