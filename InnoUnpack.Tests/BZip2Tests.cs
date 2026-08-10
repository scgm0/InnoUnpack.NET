using InnoUnpack.NET;
using InnoUnpack.NET.Compression;

namespace InnoUnpack.Tests;

/// <summary>
///     自研 bzip2 解码器测试（压缩向量由系统 bzip2 1.0.8 生成）。
/// </summary>
public class BZip2Tests {
	[Fact]
	public void DecodesTextBlock() {
		// "Hello, InnoUnpack bzip2 decoder!\n" × 3，bzip2 -9 压缩
		var compressed = Convert.FromBase64String(
			"QlpoOTFBWSZTWWEPliAAAAvfgAAQYAQQAABgAgA+LdAQIABBn/qqek0BoaYhQNNDIyYkJPE11l2zdJJCiqGiqjNh81XVYWSQmwhqw/F3JFOFCQYQ+WIA");
		var expected = string.Concat(Enumerable.Repeat("Hello, InnoUnpack bzip2 decoder!\n", 3));

		using var bz2 = new BZip2Stream(new MemoryStream(compressed));
		var actual = new StreamReader(bz2).ReadToEnd();

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void DecodesLongRuns() {
		// "AAAAABBBBBCCCCC" × 200：触发长 RUNA/RUNB 运行
		var compressed = Convert.FromBase64String(
			"QlpoOTFBWSZTWSrkVIYAArvEACAAOAAgAFBmmgUpPUwi8IsIuFIwi8KRwiwpHCL4u5IpwoSBVyKkMA==");
		var expected = string.Concat(Enumerable.Repeat("AAAAABBBBBCCCCC", 200));

		using var bz2 = new BZip2Stream(new MemoryStream(compressed));
		var actual = new StreamReader(bz2).ReadToEnd();

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void HandlesEmptyStream() {
		// bzip2 空流：流头 + 结束魔数
		var compressed = Convert.FromBase64String(
			"QlpoORdyRThQkAAAAAA=");

		using var bz2 = new BZip2Stream(new MemoryStream(compressed));
		var buffer = new byte[16];
		var n = bz2.Read(buffer);

		Assert.Equal(0, n);
	}

	[Fact]
	public void RejectsInvalidHeader() {
		var invalid = "not bzip"u8.ToArray();
		using var bz2 = new BZip2Stream(new MemoryStream(invalid));
		var ex = Assert.Throws<InnoFormatException>(() => bz2.Read(new byte[16]));
		Assert.Contains("流头", ex.Message);
	}
}