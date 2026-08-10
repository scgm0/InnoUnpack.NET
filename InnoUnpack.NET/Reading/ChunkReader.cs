using InnoUnpack.NET.Compression;
using InnoUnpack.NET.Metadata;

namespace InnoUnpack.NET.Reading;

/// <summary>
///     读取文件数据块（chunk）。
///     chunk 结构：4 字节魔数 "zlb\x1a"，随后是按 <see cref="InnoDataEntry.Compression" />
///     压缩的数据（LZMA1/LZMA2 属性头在压缩数据起始处）；加密数据在压缩数据之前先解密。
/// </summary>
sealed class ChunkReader : IDisposable {

	private ChunkReader(Stream stream) { Stream = stream; }
	static private ReadOnlySpan<byte> ChunkMagic => "zlb\u001A"u8;

	/// <summary>解压后的 chunk 数据流。</summary>
	public Stream Stream { get; }

	public void Dispose() { Stream.Dispose(); }

	/// <summary>
	///     打开指定数据条目对应的 chunk。
	/// </summary>
	/// <param name="slices">切片读取器。</param>
	/// <param name="data">数据条目（描述 chunk 位置与压缩/加密方式）。</param>
	/// <param name="crypto">加密上下文（加密安装包且已提供密码时非 null）。</param>
	/// <exception cref="InnoUnsupportedException">数据已加密且未提供密码。</exception>
	public static ChunkReader Open(SliceReader slices, InnoDataEntry data, InnoCrypto? crypto) {
		if (!slices.Seek(data.FirstSlice, data.Offset)) {
			throw new InnoFormatException($"无法定位 chunk（切片 {data.FirstSlice} 偏移 {data.Offset}）");
		}

		Span<byte> magic = stackalloc byte[4];
		if (slices.Read(magic) != 4 || !magic.SequenceEqual(ChunkMagic)) {
			throw new InnoFormatException($"chunk 魔数无效：{Convert.ToHexString(magic)} size={data.Size} offset={data.Offset}");
		}


		// 限制 chunk 数据读取范围（压缩后大小，含加密 salt）
		RestrictedStream restricted = new(
			new SliceStream(slices),
			data.Size - 4);

		Stream current = restricted;

		if (data.Encryption != InnoEncryptionMethod.Plaintext) {
			if (crypto is null) {
				throw new InnoUnsupportedException(
					"该安装包的文件数据已加密，需要提供密码（InnoOpenOptions.Password）");
			}

			current = crypto.WrapDecryptor(current, data);
		}

		current = InnoCompressionStreamFactory.Create(current, data.Compression);
		return new(current);
	}

	/// <summary>
	///     将切片读取器包装为流（用于限制读取范围）。
	/// </summary>
	sealed private class SliceStream(SliceReader slices) : Stream {

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }
		public override int Read(byte[] buffer, int offset, int count) { return slices.Read(buffer.AsSpan(offset, count)); }

		public override int Read(Span<byte> buffer) { return slices.Read(buffer); }

		public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

		public override void SetLength(long value) { throw new NotSupportedException(); }

		public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

		override protected void Dispose(bool disposing) {
			/* 不关闭切片读取器 */
		}
	}
}