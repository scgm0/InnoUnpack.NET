using System.Security.Cryptography;
using System.Text;
using InnoUnpack.NET.Metadata;
using InnoUnpack.NET.Reading;

namespace InnoUnpack.NET.Compression;

/// <summary>
///     安装包加密上下文：密码校验、密钥派生与 chunk 解密流组装。
///     支持 ARC4+MD5（4.2.2–5.3.8）、ARC4+SHA1（5.3.9–6.3.x）、XChaCha20（6.4.0+）。
///     密钥派生与 nonce 生成依据 Inno Setup 格式规范实现
///     （XChaCha20 nonce 的 chunk 偏移/切片异或参考 InnoUnpacker（MIT License）的 Extract6400.pas）。
/// </summary>
sealed class InnoCrypto {
	private readonly byte[] _encodedPassword;
	private readonly byte[]? _xchachaKey; // 32 字节（仅 XChaCha20）
	private readonly byte[]? _xchachaNonce; // 24 字节基础 nonce（仅 XChaCha20）

	private InnoCrypto(byte[] encodedPassword, byte[]? key, byte[]? nonce) {
		_encodedPassword = encodedPassword;
		_xchachaKey = key;
		_xchachaNonce = nonce;
	}

	/// <summary>
	///     根据安装包元数据与密码创建加密上下文；未加密或未提供密码时返回 null。
	///     密码错误抛出 <see cref="InnoFormatException" />。
	/// </summary>
	public static InnoCrypto? Create(InnoSetupInfo info, string? password) {
		if (!info.IsEncrypted) {
			return null;
		}

		if (string.IsNullOrEmpty(password)) {
			return null;
		}

		var encoded = EncodePassword(password, info.Codepage);

		// 6.5.0+：加密元数据位于独立加密头
		if (info.EncryptionHeader is { Encrypted: true }) {
			var (key, nonce) = DeriveXChaCha20(encoded, info.EncryptionHeader);
			if (!CheckXChaCha20Password(info.EncryptionHeader.PasswordTest, key, nonce, false)) {
				throw new InnoFormatException("密码不正确");
			}

			return new(encoded, key, nonce);
		}

		switch (info.Header.PasswordType) {
			case InnoPasswordType.Md5:
			case InnoPasswordType.Sha1: {
				// 校验：hash("PasswordCheckHash" + salt(8) + password) == PasswordCheck
				var hash = HashSaltedPassword(info.Header.PasswordType, info.Header.PasswordSalt, encoded);
				if (!hash.AsSpan().SequenceEqual(info.Header.PasswordCheck)) {
					throw new InnoFormatException("密码不正确");
				}

				break;
			}
			case InnoPasswordType.Pbkdf2Sha256XChaCha20: {
				// 6.4.0：PBKDF2 salt(16) + 迭代次数(4) + 基础 nonce(24) 都在主头的 44 字节 salt 中
				var key = Rfc2898DeriveBytes.Pbkdf2(
					encoded,
					info.Header.PasswordSalt.AsSpan(0, 16),
					(int)BitConverter.ToUInt32(info.Header.PasswordSalt, 16),
					HashAlgorithmName.SHA256,
					32);
				var nonce = info.Header.PasswordSalt.AsSpan(20, 24).ToArray();
				if (!CheckXChaCha20Password(info.Header.PasswordCheck, key, nonce, true)) {
					throw new InnoFormatException("密码不正确");
				}

				return new(encoded, key, nonce);
			}
			default:
				throw new InnoUnsupportedException($"不支持的密码校验方式：{info.Header.PasswordType}");
		}

		return new(encoded, null, null);
	}

	/// <summary>计算 MD5/SHA1 密码校验值：hash(salt(含前缀) + password)，避免中间数组分配。</summary>
	static private byte[] HashSaltedPassword(InnoPasswordType type, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> password) {
		var total = salt.Length + password.Length;
		var buffer = total <= 256 ? stackalloc byte[total] : new byte[total];
		salt.CopyTo(buffer);
		password.CopyTo(buffer[salt.Length..]);
		return type == InnoPasswordType.Md5
			? MD5.HashData(buffer)
			: SHA1.HashData(buffer);
	}

	/// <summary>从 6.5.0+ 加密头派生 XChaCha20 密钥（32 字节）与基础 nonce（24 字节）。</summary>
	static private (byte[] Key, byte[] Nonce) DeriveXChaCha20(byte[] encodedPassword, InnoEncryptionHeader header) {
		var key = Rfc2898DeriveBytes.Pbkdf2(
			encodedPassword,
			header.KdfSalt,
			(int)header.KdfIterations,
			HashAlgorithmName.SHA256,
			32);
		var nonce = new byte[24];
		BitConverter.GetBytes(header.RandomXorStartOffset).CopyTo(nonce, 0);
		BitConverter.GetBytes(header.RandomXorFirstSlice).CopyTo(nonce, 8);
		header.RemainingRandom.CopyTo(nonce, 12);
		return (key, nonce);
	}

	/// <summary>校验 XChaCha20 密码：解密 4 字节零并与期望值比较。</summary>
	static private bool CheckXChaCha20Password(ReadOnlySpan<byte> expected, byte[] key, byte[] nonce, bool xorNonceWord8) {
		var testNonce = (byte[])nonce.Clone();
		if (xorNonceWord8) {
			// 6.4.0：nonce 中间 4 字节取反后校验（与编译器 GeneratePasswordTest 一致）
			for (var i = 8; i < 12; i++) {
				testNonce[i] ^= 0xFF;
			}
		}

		var decrypted = new byte[4];
		XChaCha20Stream stream = new(new MemoryStream(decrypted, false), key, testNonce);
		stream.ReadExactly(decrypted);
		return decrypted.AsSpan().SequenceEqual(expected);
	}

	/// <summary>
	///     为数据条目创建解密流（输入流已定位到 chunk 数据区，含魔数之后的全部数据）。
	/// </summary>
	public Stream WrapDecryptor(Stream input, InnoDataEntry data) {
		switch (data.Encryption) {
			case InnoEncryptionMethod.Arc4Md5:
			case InnoEncryptionMethod.Arc4Sha1: {
				// 8 字节 chunk salt + 加密数据；key = hash(chunk_salt + password)
				var salt = new byte[8];
				if (ReadFully(input, salt) != salt.Length) {
					throw new InnoFormatException("加密 chunk 缺少 salt");
				}

				var key = HashSaltedPassword(
					data.Encryption == InnoEncryptionMethod.Arc4Md5 ? InnoPasswordType.Md5 : InnoPasswordType.Sha1,
					salt,
					_encodedPassword);
				return new Arc4Stream(input, key);
			}
			case InnoEncryptionMethod.XChaCha20: {
				if (_xchachaKey is null || _xchachaNonce is null) {
					throw new InnoFormatException("缺少 XChaCha20 密钥");
				}

				// nonce = 基础 nonce 异或 chunk 偏移与切片
				var nonce = (byte[])_xchachaNonce.Clone();
				var offset = data.Offset;
				nonce[0] ^= (byte)offset;
				nonce[1] ^= (byte)(offset >> 8);
				nonce[2] ^= (byte)(offset >> 16);
				nonce[3] ^= (byte)(offset >> 24);
				nonce[4] ^= (byte)(offset >> 32);
				nonce[5] ^= (byte)(offset >> 40);
				nonce[6] ^= (byte)(offset >> 48);
				nonce[7] ^= (byte)(offset >> 56);
				var slice = data.FirstSlice;
				nonce[8] ^= (byte)slice;
				nonce[9] ^= (byte)(slice >> 8);
				nonce[10] ^= (byte)(slice >> 16);
				nonce[11] ^= (byte)(slice >> 24);
				return new XChaCha20Stream(input, _xchachaKey, nonce);
			}
			default:
				return input;
		}
	}

	static private byte[] EncodePassword(string password, int codepage) {
		if (codepage == InnoStringDecoder.CpUtf16Le) {
			return Encoding.Unicode.GetBytes(password); // UTF-16LE
		}

		// InnoStringDecoder 的静态构造注册了 CodePagesEncodingProvider（ANSI 代码页）
		return InnoStringDecoder.GetEncoding(codepage).GetBytes(password);
	}

	static private int ReadFully(Stream stream, Span<byte> buffer) {
		var total = 0;
		while (total < buffer.Length) {
			var n = stream.Read(buffer[total..]);
			if (n <= 0) {
				break;
			}

			total += n;
		}

		return total;
	}
}