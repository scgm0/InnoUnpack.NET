using InnoUnpack.NET;

namespace InnoUnpack.Tests;

/// <summary>
///     健壮性测试：随机数据与损坏样本只应抛出格式/不支持/IO 异常，不应崩溃
///     （NullReference / IndexOutOfRange / OutOfMemory 等均视为解析器缺陷）。
/// </summary>
public class FuzzTests {
	[Fact]
	public void RandomDataThrowsExpectedExceptions() {
		var rng = new Random(42);
		foreach (var size in new[] { 0, 1, 4, 8, 12, 64, 1024, 65536 }) {
			for (var i = 0; i < 20; i++) {
				var data = new byte[size];
				rng.NextBytes(data);
				using var ms = new MemoryStream(data);
				AssertOpenThrowsExpected(ms);
			}
		}
	}

	[Theory]
	[InlineData("isetup-4.2.7.exe")]
	[InlineData("innosetup-5.6.1-unicode.exe")]
	[InlineData("innosetup-6.7.3.exe")]
	public void CorruptedFixtureThrowsExpectedExceptions(string fixture) {
		Fixtures.SkipIfMissing(fixture);

		var original = File.ReadAllBytes(Fixtures.Get(fixture));
		var rng = new Random(12345);
		for (var i = 0; i < 40; i++) {
			var data = (byte[])original.Clone();
			var flips = rng.Next(1, 17);
			for (var j = 0; j < flips; j++) {
				data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
			}

			using var ms = new MemoryStream(data);
			AssertOpenThrowsExpected(ms);
		}
	}

	static private void AssertOpenThrowsExpected(Stream stream) {
		try {
			using var archive = InnoSetupArchive.Open(stream);
			// 极少数情况下损坏数据仍能成功打开：仅枚举，不应崩溃
			_ = archive.EnumerateFiles().Count();
		} catch (InnoFormatException) {
		} catch (InnoUnsupportedException) {
		} catch (IOException) {
		} catch (InvalidDataException) {
		}
	}
}
