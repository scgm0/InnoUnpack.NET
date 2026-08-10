using System.Security.Cryptography;
using InnoUnpack.NET.Reading;

namespace InnoUnpack.NET.Metadata;

/// <summary>
///     文件位置条目（对应 TSetupFileLocationEntry），描述文件数据在切片中的位置、
///     压缩/加密方式与校验信息。
/// </summary>
public sealed class InnoDataEntry {
	/// <summary>chunk 起始切片号。</summary>
	public uint FirstSlice { get; internal set; }

	/// <summary>chunk 结束切片号。</summary>
	public uint LastSlice { get; internal set; }

	/// <summary>chunk 数据在起始切片中的偏移（6.5.2+ 为 64 位）。</summary>
	public ulong Offset { get; internal set; }

	/// <summary>文件数据在解压后 chunk 数据中的偏移。</summary>
	public ulong FileOffset { get; internal set; }

	/// <summary>文件数据大小（解压后）。</summary>
	public ulong FileSize { get; internal set; }

	/// <summary>chunk 压缩后总大小。</summary>
	public ulong Size { get; internal set; }

	/// <summary>解压后的 chunk 大小。</summary>
	public ulong UncompressedSize { get; internal set; }

	/// <summary>压缩方法。</summary>
	public InnoCompressionMethod Compression { get; internal set; } = InnoCompressionMethod.Stored;

	/// <summary>加密方法。</summary>
	public InnoEncryptionMethod Encryption { get; internal set; } = InnoEncryptionMethod.Plaintext;

	/// <summary>选项标志。</summary>
	public InnoDataOptions Options { get; internal set; }

	/// <summary>签名模式（6.3.x）。</summary>
	public InnoSignMode Sign { get; internal set; } = InnoSignMode.NoSetting;

	/// <summary>校验和类型与数据（MD5 / SHA1 / SHA256 / CRC32）。</summary>
	public InnoChecksum Checksum { get; internal set; } = InnoChecksum.None;

	/// <summary>文件时间戳（UTC）。</summary>
	public DateTime Timestamp { get; internal set; }

	/// <summary>时间戳纳秒部分。</summary>
	public uint TimestampNsec { get; internal set; }

	/// <summary>文件版本（MS 与 LS 组成的 64 位值）。</summary>
	public ulong FileVersion { get; internal set; }

	/// <summary>内部：解析数据条目。</summary>
	internal static InnoDataEntry Parse(InnoBinaryReader reader, InnoVersion version, InnoCompressionMethod headerCompression) {
		InnoHeader.VersionGates v = new(version);
		InnoDataEntry entry = new() {
			FirstSlice = reader.ReadUInt32(),
			LastSlice = reader.ReadUInt32()
		};

		if (v.Lt400) {
			entry.FirstSlice--;
			entry.LastSlice--;
		}

		entry.Offset = v.Ge652 ? reader.ReadUInt64() : reader.ReadUInt32();

		if (v.Ge401) {
			entry.FileOffset = reader.ReadUInt64();
		}

		if (v.Ge400) {
			entry.FileSize = reader.ReadUInt64();
			entry.Size = reader.ReadUInt64();
		} else {
			entry.FileSize = reader.ReadUInt32();
			entry.Size = reader.ReadUInt32();
		}

		entry.UncompressedSize = entry.FileSize;

		if (v.Ge640) {
			entry.Checksum = new(InnoChecksumType.Sha256, reader.ReadBytes(32));
		} else if (v.Ge539) {
			entry.Checksum = new(InnoChecksumType.Sha1, reader.ReadBytes(20));
		} else if (v.Ge420) {
			entry.Checksum = new(InnoChecksumType.Md5, reader.ReadBytes(16));
		} else if (v.Ge401) {
			entry.Checksum = new(InnoChecksumType.Crc32, BitConverter.GetBytes(reader.ReadUInt32()));
		} else {
			entry.Checksum = new(InnoChecksumType.Adler32, BitConverter.GetBytes(reader.ReadUInt32()));
		}

		// 时间戳：32 位安装包使用 Win32 FILETIME
		var filetime = reader.ReadInt64();
		const long filetimeOffset = 0x19DB1DED53E8000; // 1601-01-01 与 1970-01-01 的差值（100ns 单位）
		if (filetime >= filetimeOffset) {
			filetime -= filetimeOffset;
			entry.Timestamp = DateTime.UnixEpoch.AddTicks(filetime / 10);
			entry.TimestampNsec = (uint)(filetime % 10_000_000) * 100;
		}

		var versionMs = reader.ReadUInt32();
		var versionLs = reader.ReadUInt32();
		entry.FileVersion = (ulong)versionMs << 32 | versionLs;

		ulong options = 0;
		FlagReader flags = new(reader);
		flags.Add(0); // VersionInfoValid
		if (v.Lt643) flags.Add(1); // VersionInfoNotValid
		if (v is { Ge2017: true, Lt401: true }) flags.Add(2); // BZipped
		if (v.Ge4010) flags.Add(3); // TimeStampInUTC
		if (v is { Ge410: true, Lt643: true }) flags.Add(4); // IsUninstallerExe
		if (v.Ge418) flags.Add(5); // CallInstructionOptimized
		if (v is { Ge420: true, Lt643: true }) flags.Add(6); // Touch
		if (v.Ge422) flags.Add(7); // ChunkEncrypted
		if (v.Ge425) {
			flags.Add(8); // ChunkCompressed
		} else {
			options |= 1UL << 8;
		}

		if (v is { Ge5113: true, Lt643: true }) flags.Add(9); // SolidBreak
		if (v is { Ge557: true, Lt630: true }) {
			flags.Add(10); // Sign
			flags.Add(11); // SignOnce
		}

		entry.Options = (InnoDataOptions)flags.GetResult();

		if (v.Ge643) {
			entry.Sign = InnoSignMode.NoSetting;
		} else if (v.Ge630) {
			entry.Sign = (InnoSignMode)reader.ReadByte();
		} else if ((entry.Options & InnoDataOptions.SignOnce) != 0) {
			entry.Sign = InnoSignMode.Once;
		} else if ((entry.Options & InnoDataOptions.Sign) != 0) {
			entry.Sign = InnoSignMode.Yes;
		}

		if ((entry.Options & InnoDataOptions.ChunkCompressed) != 0) {
			entry.Compression = headerCompression;
		} else {
			entry.Compression = InnoCompressionMethod.Stored;
		}

		if ((entry.Options & InnoDataOptions.BZipped) != 0) {
			entry.Compression = InnoCompressionMethod.BZip2;
		}

		if ((entry.Options & InnoDataOptions.ChunkEncrypted) != 0) {
			if (v.Ge640) {
				entry.Encryption = InnoEncryptionMethod.XChaCha20;
			} else if (v.Ge539) {
				entry.Encryption = InnoEncryptionMethod.Arc4Sha1;
			} else {
				entry.Encryption = InnoEncryptionMethod.Arc4Md5;
			}
		}

		return entry;
	}

	/// <summary>内部：按位读取的标志集。</summary>
	sealed private class FlagReader(InnoBinaryReader reader) {
		private int _bits;
		private byte _current;
		private ulong _flags;

		public void Add(int flagIndex) {
			if ((_bits & 7) == 0) {
				_current = reader.ReadByte();
			}

			if ((_current & 1 << (_bits & 7)) != 0) {
				_flags |= 1UL << flagIndex;
			}

			_bits++;
		}

		/// <summary>返回标志值（3 字节标志集按 Delphi set 布局补齐为 4 字节，与 innoextract 一致）。</summary>
		public ulong GetResult() {
			if (_bits is > 16 and <= 24) {
				_ = reader.ReadByte();
			}

			return _flags;
		}
	}
}

/// <summary>校验和类型。</summary>
public enum InnoChecksumType {
	None,
	Adler32,
	Crc32,
	Md5,
	Sha1,
	Sha256
}

/// <summary>校验和值。</summary>
public readonly record struct InnoChecksum(InnoChecksumType Type, byte[] Data) {
	public static readonly InnoChecksum None = new(InnoChecksumType.None, []);

	/// <summary>校验和字节长度。</summary>
	public int Size =>
		Type switch {
			InnoChecksumType.Adler32 or InnoChecksumType.Crc32 => 4,
			InnoChecksumType.Md5 => 16,
			InnoChecksumType.Sha1 => 20,
			InnoChecksumType.Sha256 => 32,
			_ => 0
		};

	/// <summary>校验和是否与给定数据匹配（data 需为解压后的完整文件内容）。</summary>
	public bool Matches(ReadOnlySpan<byte> data) {
		switch (Type) {
			case InnoChecksumType.Adler32:
				return ComputeAdler32(data) == BitConverter.ToUInt32(Data);
			case InnoChecksumType.Crc32:
				return Crc32.Compute(data) == BitConverter.ToUInt32(Data);
			case InnoChecksumType.Md5: {
				var hash = MD5.HashData(data);
				return hash.AsSpan().SequenceEqual(Data);
			}
			case InnoChecksumType.Sha1: {
				var hash = SHA1.HashData(data);
				return hash.AsSpan().SequenceEqual(Data);
			}
			case InnoChecksumType.Sha256: {
				var hash = SHA256.HashData(data);
				return hash.AsSpan().SequenceEqual(Data);
			}
			default:
				return true;
		}
	}

	static private uint ComputeAdler32(ReadOnlySpan<byte> data) {
		const uint mod = 65521;
		uint a = 1, b = 0;
		foreach (var value in data) {
			a = (a + value) % mod;
			b = (b + a) % mod;
		}

		return b << 16 | a;
	}
}

/// <summary>
///     流式文件校验器：边读边计算校验和，提取完成后与条目中的期望值比对。
/// </summary>
abstract class FileHasher : IDisposable {

	public abstract void Dispose();
	public abstract void Update(ReadOnlySpan<byte> data);

	/// <summary>比对最终校验和与期望值。</summary>
	public abstract bool Verify(InnoChecksum expected);

	/// <summary>按校验和类型创建校验器；类型未知或为 None 时返回 null。</summary>
	public static FileHasher? Create(InnoChecksumType type) {
		return type switch {
			InnoChecksumType.Md5 => new IncrementalHasher(InnoChecksumType.Md5, HashAlgorithmName.MD5),
			InnoChecksumType.Sha1 => new IncrementalHasher(InnoChecksumType.Sha1, HashAlgorithmName.SHA1),
			InnoChecksumType.Sha256 => new IncrementalHasher(InnoChecksumType.Sha256, HashAlgorithmName.SHA256),
			InnoChecksumType.Crc32 => new Crc32Hasher(),
			InnoChecksumType.Adler32 => new Adler32Hasher(),
			_ => null
		};
	}

	sealed private class IncrementalHasher(InnoChecksumType expectedType, HashAlgorithmName algorithm) : FileHasher {
		private readonly IncrementalHash _hash =
			IncrementalHash.CreateHash(algorithm);

		public override void Update(ReadOnlySpan<byte> data) { _hash.AppendData(data); }

		public override bool Verify(InnoChecksum expected) {
			var actual = _hash.GetHashAndReset();
			return expected.Type == expectedType && actual.AsSpan().SequenceEqual(expected.Data);
		}

		public override void Dispose() { _hash.Dispose(); }
	}

	sealed private class Crc32Hasher : FileHasher {
		private readonly Crc32 _crc = new();

		public override void Update(ReadOnlySpan<byte> data) { _crc.Update(data); }

		public override bool Verify(InnoChecksum expected) {
			return expected.Type == InnoChecksumType.Crc32
				&& _crc.GetValue() == BitConverter.ToUInt32(expected.Data);
		}

		public override void Dispose() { }
	}

	sealed private class Adler32Hasher : FileHasher {
		private const uint Mod = 65521;
		private uint _a = 1;
		private uint _b;

		public override void Update(ReadOnlySpan<byte> data) {
			foreach (var value in data) {
				_a = (_a + value) % Mod;
				_b = (_b + _a) % Mod;
			}
		}

		public override bool Verify(InnoChecksum expected) {
			return expected.Type == InnoChecksumType.Adler32
				&& (_b << 16 | _a) == BitConverter.ToUInt32(expected.Data);
		}

		public override void Dispose() { }
	}
}