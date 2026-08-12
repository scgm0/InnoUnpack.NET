using System.IO.Compression;
using InnoUnpack.NET.Metadata;

namespace InnoUnpack.NET.Compression;

/// <summary>
///     创建 Inno Setup 各压缩算法对应的解压流。
///     压缩数据的布局（块与 chunk 相同）：
///     - LZMA1：5 字节属性头（lc/lp/pb + 字典大小），后接 LZMA 码流（无结束标记）；
///     - LZMA2：1 字节属性（字典大小），后接 LZMA2 码流；
///     - zlib：标准 zlib 流（2 字节头 + deflate + Adler-32）；
///     - bzip2：标准 bzip2 流；
///     - 其余：原样返回。
/// </summary>
static class InnoCompressionStreamFactory {
	/// <summary>
	///     包装输入流，解压指定压缩方法的数据。
	///     返回的流读取时输出解压后的数据；调用方负责释放。
	/// </summary>
	/// <param name="input">压缩数据输入流。</param>
	/// <param name="method">压缩方法。</param>
	/// <param name="allowTruncated">
	///     LZMA1 输入耗尽时的行为：true 用 0xFF 填充继续（适用于带 EOS 标记的头部块），
	///     false 严格停止（适用于无结束标记的数据块）。
	/// </param>
	/// <param name="maxParallelism">LZMA2 允许的并行解码 worker 数（1 = 串行）。</param>
	public static Stream Create(Stream input, InnoCompressionMethod method, bool allowTruncated = false, int maxParallelism = 1) {
		switch (method) {
			case InnoCompressionMethod.Stored:
				return new NonClosingWrapper(input);

			case InnoCompressionMethod.Zlib:
				return new ZLibStream(input, CompressionMode.Decompress, false);

			case InnoCompressionMethod.BZip2:
				return new BZip2Stream(input);

			case InnoCompressionMethod.Lzma1: {
					// 属性头在 Lzma1Stream 内部解析（5 字节）
					return new Lzma1Stream(input, allowTruncated);
				}

			case InnoCompressionMethod.Lzma2: {
					// 属性头：1 字节（字典大小编码）
					var properties = ReadProperties(input, method);
					return new Lzma2Stream(input, properties[0], maxParallelism);
				}

			default:
				throw new InnoFormatException($"未知的压缩方法：{method}");
		}
	}

	static private byte[] ReadProperties(Stream input, InnoCompressionMethod method) {
		var length = method == InnoCompressionMethod.Lzma1 ? 5 : 1;
		var properties = new byte[length];
		var offset = 0;
		while (offset < length) {
			var n = input.Read(properties, offset, length - offset);
			if (n <= 0) {
				throw new InnoFormatException("压缩数据意外结束（缺少属性头）");
			}

			offset += n;
		}

		return properties;
	}

	/// <summary>不关闭底层流的包装流（存储模式的"解压流"）。</summary>
	sealed private class NonClosingWrapper(Stream inner) : Stream {

		public override bool CanRead => inner.CanRead;
		public override bool CanSeek => inner.CanSeek;
		public override bool CanWrite => false;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }
		public override void Flush() { inner.Flush(); }

		public override int Read(byte[] buffer, int offset, int count) { return inner.Read(buffer, offset, count); }

		public override int Read(Span<byte> buffer) { return inner.Read(buffer); }

		public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }

		public override void SetLength(long value) { throw new NotSupportedException(); }

		public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

		override protected void Dispose(bool disposing) {
			/* 不关闭底层流 */
		}
	}
}
