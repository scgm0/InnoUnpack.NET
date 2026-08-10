using InnoUnpack.NET.Compression;
using InnoUnpack.NET.Metadata;

namespace InnoUnpack.NET.Reading;

/// <summary>
///     读取 setup 头部数据块（主头部流与次头部流）。
///     块结构（4.0.9+）：
///     前 4 字节为头部 CRC32，随后依次为 stored_size（4 字节，6.7.0+ 为 8 字节）与
///     压缩标志（1 字节），这些字节全部参与 CRC 计算；之后是压缩数据。
///     压缩数据内部按 4096 字节子块组织，每个子块前有 4 字节 CRC32。
///     4.0.9 之前使用旧的块格式（compressed_size + uncompressed_size）。
/// </summary>
sealed class BlockReader : IDisposable {

	private BlockReader(Stream inner, long physicalEnd) {
		Stream = inner;
		PhysicalEnd = physicalEnd;
	}

	/// <summary>块数据（压缩后）在底层流中的物理结束位置。</summary>
	public long PhysicalEnd { get; }

	/// <summary>当前块内容的读取流。</summary>
	public Stream Stream { get; }

	public void Dispose() { Stream.Dispose(); }

	/// <summary>读取并解压一个块，返回可读取块内容的流。</summary>
	public static BlockReader Create(InnoBinaryReader reader, Stream baseStream, InnoVersion version) {
		InnoHeader.VersionGates v = new(version);

		var expectedCrc = reader.ReadUInt32();
		Crc32 checksum = new();

		ulong storedSize;
		InnoCompressionMethod compression;

		if (v.Ge409) {
			var sizeBytes = v.Ge670 ? reader.ReadBytes(8) : reader.ReadBytes(4);
			checksum.Update(sizeBytes);
			storedSize = v.Ge670 ? BitConverter.ToUInt64(sizeBytes, 0) : BitConverter.ToUInt32(sizeBytes, 0);

			var compressed = reader.ReadByte();
			checksum.Update(compressed);
			compression = compressed != 0
				? v.Ge416 ? InnoCompressionMethod.Lzma1 : InnoCompressionMethod.Zlib
				: InnoCompressionMethod.Stored;
		} else {
			// 4.0.9 之前：compressed_size + uncompressed_size（均参与 CRC）
			var compressedSizeBytes = reader.ReadBytes(4);
			checksum.Update(compressedSizeBytes);
			var uncompressedSizeBytes = reader.ReadBytes(4);
			checksum.Update(uncompressedSizeBytes);
			var compressedSize = BitConverter.ToUInt32(compressedSizeBytes, 0);
			var uncompressedSize = BitConverter.ToUInt32(uncompressedSizeBytes, 0);

			if (compressedSize == uint.MaxValue) {
				storedSize = uncompressedSize;
				compression = InnoCompressionMethod.Stored;
			} else {
				storedSize = compressedSize;
				compression = InnoCompressionMethod.Zlib;
			}

			// 每个 4KiB 子块前附带 4 字节 CRC32
			storedSize += (storedSize + 4095) / 4096 * 4;
		}

		if (checksum.GetValue() != expectedCrc) {
			throw new InnoFormatException("块头 CRC32 校验失败");
		}

		// 压缩数据按 4096 字节子块组织（每子块前有 CRC32），需先剥离子块头再解压
		var blockStart = baseStream.Position;
		RestrictedStream restricted = new(baseStream, storedSize);
		BlockFilterStream blockFilter = new(restricted);
		var decompressed = InnoCompressionStreamFactory.Create(blockFilter, compression, true);
		return new(decompressed, blockStart + (long)storedSize);
	}
}

/// <summary>
///     剥离块内部 4096 字节子块的 CRC32 头并校验。
/// </summary>
sealed class BlockFilterStream(Stream inner) : Stream {
	private readonly byte[] _buffer = new byte[4096];
	private int _length;
	private int _position;

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count) {
		var total = 0;
		while (count > 0) {
			if (_position == _length && !ReadChunk()) {
				break;
			}

			var n = Math.Min(count, _length - _position);
			Array.Copy(_buffer, _position, buffer, offset, n);
			_position += n;
			offset += n;
			count -= n;
			total += n;
		}

		return total;
	}

	public override int Read(Span<byte> buffer) {
		var total = 0;
		while (!buffer.IsEmpty) {
			if (_position == _length && !ReadChunk()) {
				break;
			}

			var n = Math.Min(buffer.Length, _length - _position);
			_buffer.AsSpan(_position, n).CopyTo(buffer);
			_position += n;
			buffer = buffer[n..];
			total += n;
		}

		return total;
	}

	/// <summary>读取一个子块（4 字节 CRC + 最多 4096 字节数据）并校验。</summary>
	private bool ReadChunk() {
		Span<byte> crcBytes = stackalloc byte[4];
		var crcRead = ReadFully(inner, crcBytes);
		if (crcRead != 4) {
			return false;
		}

		var expected = BitConverter.ToUInt32(crcBytes);

		_length = ReadFully(inner, _buffer);
		if (_length <= 0) {
			return false;
		}

		var actual = Crc32.Compute(_buffer.AsSpan(0, _length));
		if (actual != expected) {
			throw new InnoFormatException("块子块 CRC32 校验失败");
		}

		_position = 0;
		return true;
	}

	static private int ReadFully(Stream stream, Span<byte> buffer) {
		var total = 0;
		while (!buffer.IsEmpty) {
			var n = stream.Read(buffer);
			if (n <= 0) {
				break;
			}

			buffer = buffer[n..];
			total += n;
		}

		return total;
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

	override protected void Dispose(bool disposing) {
		if (disposing) {
			inner.Dispose();
		}

		base.Dispose(disposing);
	}
}

/// <summary>
///     限制底层流可读取范围的流。
/// </summary>
sealed class RestrictedStream(Stream inner, ulong length) : Stream {
	private long _read;

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => (long)length;
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count) {
		if (_read >= (long)length) {
			return 0;
		}

		count = (int)Math.Min(count, (long)length - _read);
		var n = inner.Read(buffer, offset, count);
		_read += n;
		return n;
	}

	public override int Read(Span<byte> buffer) {
		if (_read >= (long)length) {
			return 0;
		}

		if (buffer.Length > (long)length - _read) {
			buffer = buffer[..(int)((long)length - _read)];
		}

		var n = inner.Read(buffer);
		_read += n;
		return n;
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

	override protected void Dispose(bool disposing) {
		/* 不关闭底层流 */
	}
}