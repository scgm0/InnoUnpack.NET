using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using InnoUnpack.NET.Compression;
using InnoUnpack.NET.Metadata;

namespace InnoUnpack.Tests;

/// <summary>
///     加密算法测试（ARC4 与 XChaCha20 的解密正确性）。
/// </summary>
public class CryptoAlgorithmTests {
	[Fact]
	public void Arc4DecryptsStandardVector() {
		// 经典 RC4 测试向量：key="Key"，明文 "Plaintext" → 密文 BBF316E8D940AF0AD3
		byte[] key = [.. "Key"u8];
		var ciphertext = Convert.FromHexString("BBF316E8D940AF0AD3");
		byte[] expected = [.. "Plaintext"u8];

		using Arc4Stream stream = new(new MemoryStream(ciphertext), key);
		var actual = new byte[expected.Length];
		stream.ReadExactly(actual);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void Arc4RoundTrips() {
		byte[] key = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];
		byte[] data = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

		// 模拟加密：相同密钥的 ARC4 流异或
		var encrypted = new byte[data.Length];
		using (Arc4Stream enc = new(new MemoryStream(data), key)) {
			enc.ReadExactly(encrypted);
		}

		using Arc4Stream dec = new(new MemoryStream(encrypted), key);
		var decrypted = new byte[data.Length];
		dec.ReadExactly(decrypted);

		Assert.Equal(data, decrypted);
	}

	[Fact]
	public void XChaCha20MatchesStandardImplementation() {
		// 由 pycryptodome（标准 XChaCha20 实现）生成的权威向量；
		// Inno Setup 6.4+ 使用的即为此变体（HChaCha20 + 8 字节 nonce 的 ChaCha20 布局）
		var key = Convert.FromHexString(
			"000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
		var nonce = Convert.FromHexString(
			"202122232425262728292A2B2C2D2E2F3031323334353637");
		var ciphertext = Convert.FromHexString(
			"AA5C9A466C867C6391EE9C65EC1DDF6CDA5D3A8E27C114C59190A29AC93F768CEDF46D841A10D905");
		byte[] expected = [.. "XChaCha20 standard implementation vector"u8];

		using XChaCha20Stream stream = new(new MemoryStream(ciphertext), key, nonce);
		var actual = new byte[expected.Length];
		stream.ReadExactly(actual);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void XChaCha20RoundTrips() {
		var key = Convert.FromHexString(
			"000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
		var nonce = Convert.FromHexString(
			"202122232425262728292A2B2C2D2E2F3031323334353637");
		var data = "加密安装包测试数据：XChaCha20 流解密。"u8.ToArray();

		var encrypted = new byte[data.Length];
		using (XChaCha20Stream enc = new(new MemoryStream(data), key, nonce)) {
			enc.ReadExactly(encrypted);
		}

		using XChaCha20Stream dec = new(new MemoryStream(encrypted), key, nonce);
		var decrypted = new byte[data.Length];
		dec.ReadExactly(decrypted);

		Assert.Equal(data, decrypted);
	}

	[Fact]
	public void Arc4Md5ChunkDecryptorRoundTrips() {
		// 模拟 Inno Setup 加密 chunk：key = MD5(chunk_salt + password)，ARC4 流加密
		byte[] salt = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88];
		var password = "secret"u8.ToArray();
		var key = MD5.HashData(salt.Concat(password).ToArray());
		var data = "模拟加密 chunk 数据 ARC4 MD5"u8.ToArray();

		var encrypted = new byte[data.Length];
		using (Arc4Stream enc = new(new MemoryStream(data), key)) {
			enc.ReadExactly(encrypted);
		}

		InnoDataEntry entry = new() {
			Encryption = InnoEncryptionMethod.Arc4Md5,
			Offset = 0,
			FirstSlice = 0
		};
		InnoCryptoForTest crypto = new(password);

		// 加密数据前拼接 8 字节 salt
		byte[] salted = [.. salt, .. encrypted];
		using var dec = crypto.WrapDecryptor(new MemoryStream(salted), entry);
		var decrypted = new byte[data.Length];
		dec.ReadExactly(decrypted);

		Assert.Equal(data, decrypted);
	}

	[Fact]
	public void XChaCha20ChunkDecryptorRoundTrips() {
		// 模拟 Inno Setup 6.4+ 加密 chunk：nonce = 基础 nonce 异或 chunk 偏移/切片
		var key = Convert.FromHexString(
			"000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
		var baseNonce = Convert.FromHexString(
			"202122232425262728292A2B2C2D2E2F3031323334353637");
		const ulong offset = 0x123456789;
		const uint slice = 7;

		var nonce = (byte[])baseNonce.Clone();
		for (var i = 0; i < 8; i++) nonce[i] ^= (byte)(offset >> 8 * i);
		for (var i = 0; i < 4; i++) nonce[8 + i] ^= (byte)(slice >> 8 * i);

		var data = "模拟加密 chunk 数据 XChaCha20"u8.ToArray();
		var encrypted = new byte[data.Length];
		using (XChaCha20Stream enc = new(new MemoryStream(data), key, nonce)) {
			enc.ReadExactly(encrypted);
		}

		InnoDataEntry entry = new() {
			Encryption = InnoEncryptionMethod.XChaCha20,
			Offset = offset,
			FirstSlice = slice
		};
		InnoCryptoForTest crypto = new(key, baseNonce);

		using var dec = crypto.WrapDecryptor(new MemoryStream(encrypted), entry);
		var decrypted = new byte[data.Length];
		dec.ReadExactly(decrypted);

		Assert.Equal(data, decrypted);
	}

	[Fact]
	public void PasswordCheckUsesPasswordCheckHashPrefix() {
		// 验证 MD5 密码校验：MD5("PasswordCheckHash" + salt(8) + password)
		byte[] salt = [1, 2, 3, 4, 5, 6, 7, 8];
		const string password = "test-password";
		var passwordBytes = Encoding.UTF8.GetBytes(password);

		var prefixSalt = new byte[14 + salt.Length];
		"PasswordCheckHash"u8.CopyTo(prefixSalt);
		salt.CopyTo(prefixSalt, 14);
		var expected = MD5.HashData(prefixSalt.Concat(passwordBytes).ToArray());

		InnoHeader header = new() {
			PasswordType = InnoPasswordType.Md5,
			PasswordCheck = expected,
			PasswordSalt = prefixSalt,
			Options = InnoHeaderOptions.EncryptionUsed
		};

		InnoSetupInfo info = new() { Header = header };
		// 密码正确时不应抛异常
		var crypto = InnoCrypto.Create(info, password);
		Assert.NotNull(crypto);
	}

	/// <summary>测试用：InnoCrypto 构造函数非公开，通过反射构造实例。</summary>
	sealed private class InnoCryptoForTest {
		private readonly object _crypto;
		private readonly MethodInfo _wrap;

		public InnoCryptoForTest(byte[] password) {
			var type = typeof(InnoCrypto);
			var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
			_crypto = ctor.Invoke([password, null, null]);
			_wrap = type.GetMethod("WrapDecryptor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
		}

		public InnoCryptoForTest(byte[] key, byte[] nonce) {
			var type = typeof(InnoCrypto);
			var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)[0];
			_crypto = ctor.Invoke([Array.Empty<byte>(), key, nonce]);
			_wrap = type.GetMethod("WrapDecryptor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
		}

		public Stream WrapDecryptor(Stream input, InnoDataEntry entry) {
			return (Stream)_wrap.Invoke(_crypto, [input, entry])!;
		}
	}
}