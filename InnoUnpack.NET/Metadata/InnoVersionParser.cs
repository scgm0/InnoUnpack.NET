using System.Text;

namespace InnoUnpack.NET.Metadata;

/// <summary>
///     解析安装包签名串中的 Inno Setup 数据版本。
///     签名格式为 "Inno Setup Setup Data (X.Y.Z)" 或 "Inno Setup Setup Data (X.Y.Z) (u)"，
///     以及 My Inno Setup Extensions 变体 "My Inno Setup Extensions Setup Data (...)"。
/// </summary>
static class InnoVersionParser {
	/// <summary>签名串最大长度（64 字节）。</summary>
	public const int SignatureSize = 64;

	static private readonly string[] _knownSignatures = [
		// 4.x（ANSI）
		"Inno Setup Setup Data (4.0.0a)",
		"Inno Setup Setup Data (4.0.1)",
		"Inno Setup Setup Data (4.0.3)",
		"Inno Setup Setup Data (4.0.5)",
		"Inno Setup Setup Data (4.0.9)",
		"Inno Setup Setup Data (4.0.10)",
		"Inno Setup Setup Data (4.0.11)",
		"Inno Setup Setup Data (4.1.0)",
		"Inno Setup Setup Data (4.1.2)",
		"Inno Setup Setup Data (4.1.3)",
		"Inno Setup Setup Data (4.1.4)",
		"Inno Setup Setup Data (4.1.5)",
		"Inno Setup Setup Data (4.1.6)",
		"Inno Setup Setup Data (4.1.8)",
		"Inno Setup Setup Data (4.2.0)",
		"Inno Setup Setup Data (4.2.1)",
		"Inno Setup Setup Data (4.2.2)",
		"Inno Setup Setup Data (4.2.3)",
		"Inno Setup Setup Data (4.2.4)",
		"Inno Setup Setup Data (4.2.5)",
		"Inno Setup Setup Data (4.2.6)",
		// 5.x（4.2.x–5.2.4 ANSI，5.2.5+ Unicode）
		"Inno Setup Setup Data (5.0.0)",
		"Inno Setup Setup Data (5.0.1)",
		"Inno Setup Setup Data (5.0.3)",
		"Inno Setup Setup Data (5.0.4)",
		"Inno Setup Setup Data (5.1.0)",
		"Inno Setup Setup Data (5.1.2)",
		"Inno Setup Setup Data (5.1.7)",
		"Inno Setup Setup Data (5.1.10)",
		"Inno Setup Setup Data (5.1.13)",
		"Inno Setup Setup Data (5.2.0)",
		"Inno Setup Setup Data (5.2.1)",
		"Inno Setup Setup Data (5.2.3)",
		"Inno Setup Setup Data (5.2.5)",
		"Inno Setup Setup Data (5.2.5) (u)",
		"Inno Setup Setup Data (5.3.0)",
		"Inno Setup Setup Data (5.3.0) (u)",
		"Inno Setup Setup Data (5.3.3)",
		"Inno Setup Setup Data (5.3.3) (u)",
		"Inno Setup Setup Data (5.3.5)",
		"Inno Setup Setup Data (5.3.5) (u)",
		"Inno Setup Setup Data (5.3.6)",
		"Inno Setup Setup Data (5.3.6) (u)",
		"Inno Setup Setup Data (5.3.7)",
		"Inno Setup Setup Data (5.3.7) (u)",
		"Inno Setup Setup Data (5.3.8)",
		"Inno Setup Setup Data (5.3.8) (u)",
		"Inno Setup Setup Data (5.3.9)",
		"Inno Setup Setup Data (5.3.9) (u)",
		"Inno Setup Setup Data (5.3.10)",
		"Inno Setup Setup Data (5.3.10) (u)",
		"Inno Setup Setup Data (5.4.2)",
		"Inno Setup Setup Data (5.4.2) (u)",
		"Inno Setup Setup Data (5.5.0)",
		"Inno Setup Setup Data (5.5.0) (u)",
		"Inno Setup Setup Data (5.5.6)",
		"Inno Setup Setup Data (5.5.6) (u)",
		"Inno Setup Setup Data (5.5.7)",
		"Inno Setup Setup Data (5.5.7) (u)",
		"Inno Setup Setup Data (5.5.7) (U)",
		"Inno Setup Setup Data (5.5.8) (u)",
		"Inno Setup Setup Data (5.6.0)",
		"Inno Setup Setup Data (5.6.0) (u)",
		"Inno Setup Setup Data (5.6.2)",
		"Inno Setup Setup Data (5.6.2) (u)",
		// 6.x（全部 Unicode）
		"Inno Setup Setup Data (6.0.0) (u)",
		"Inno Setup Setup Data (6.1.0) (u)",
		"Inno Setup Setup Data (6.3.0)",
		"Inno Setup Setup Data (6.4.0)",
		"Inno Setup Setup Data (6.4.0.1)",
		"Inno Setup Setup Data (6.4.2)",
		"Inno Setup Setup Data (6.4.3)",
		"Inno Setup Setup Data (6.5.0)",
		"Inno Setup Setup Data (6.5.2)",
		"Inno Setup Setup Data (6.6.0)",
		"Inno Setup Setup Data (6.6.1)",
		"Inno Setup Setup Data (6.7.0)",
		// 7.x
		"Inno Setup Setup Data (7.0.0.1)",
		"Inno Setup Setup Data (7.0.0.3)"
	];

	/// <summary>各已知签名对应的版本元数据。</summary>
	static private readonly (uint A, uint B, uint C, uint D, bool Unicode, bool Isx, bool Ambiguous)[] _knownVersions = [
		(4, 0, 0, 0, false, false, false), (4, 0, 1, 0, false, false, false), (4, 0, 3, 0, false, false, false),
		(4, 0, 5, 0, false, false, false), (4, 0, 9, 0, false, false, false), (4, 0, 10, 0, false, false, false),
		(4, 0, 11, 0, false, false, false), (4, 1, 0, 0, false, false, false), (4, 1, 2, 0, false, false, false),
		(4, 1, 3, 0, false, false, false), (4, 1, 4, 0, false, false, false), (4, 1, 5, 0, false, false, false),
		(4, 1, 6, 0, false, false, false), (4, 1, 8, 0, false, false, false), (4, 2, 0, 0, false, false, false),
		(4, 2, 1, 0, false, false, false), (4, 2, 2, 0, false, false, false), (4, 2, 3, 0, false, false, true),
		(4, 2, 4, 0, false, false, false), (4, 2, 5, 0, false, false, false), (4, 2, 6, 0, false, false, false),
		(5, 0, 0, 0, false, false, false), (5, 0, 1, 0, false, false, false), (5, 0, 3, 0, false, false, false),
		(5, 0, 4, 0, false, false, false), (5, 1, 0, 0, false, false, false), (5, 1, 2, 0, false, false, false),
		(5, 1, 7, 0, false, false, false), (5, 1, 10, 0, false, false, false), (5, 1, 13, 0, false, false, false),
		(5, 2, 0, 0, false, false, false), (5, 2, 1, 0, false, false, false), (5, 2, 3, 0, false, false, false),
		(5, 2, 5, 0, false, false, false), (5, 2, 5, 0, true, false, false),
		(5, 3, 0, 0, false, false, false), (5, 3, 0, 0, true, false, false),
		(5, 3, 3, 0, false, false, false), (5, 3, 3, 0, true, false, false),
		(5, 3, 5, 0, false, false, false), (5, 3, 5, 0, true, false, false),
		(5, 3, 6, 0, false, false, false), (5, 3, 6, 0, true, false, false),
		(5, 3, 7, 0, false, false, false), (5, 3, 7, 0, true, false, false),
		(5, 3, 8, 0, false, false, false), (5, 3, 8, 0, true, false, false),
		(5, 3, 9, 0, false, false, false), (5, 3, 9, 0, true, false, false),
		(5, 3, 10, 0, false, false, true), (5, 3, 10, 0, true, false, true),
		(5, 4, 2, 0, false, false, true), (5, 4, 2, 0, true, false, true),
		(5, 5, 0, 0, false, false, true), (5, 5, 0, 0, true, false, true),
		(5, 5, 6, 0, false, false, false), (5, 5, 6, 0, true, false, false),
		(5, 5, 7, 0, false, false, true), (5, 5, 7, 0, true, false, true),
		(5, 5, 7, 0, true, false, true), (5, 5, 7, 0, true, false, true),
		(5, 6, 0, 0, false, false, false), (5, 6, 0, 0, true, false, false),
		(5, 6, 2, 0, false, false, false), (5, 6, 2, 0, true, false, false),
		(6, 0, 0, 0, true, false, false), (6, 1, 0, 0, true, false, false), (6, 3, 0, 0, true, false, false),
		(6, 4, 0, 0, true, false, false), (6, 4, 0, 1, true, false, false), (6, 4, 2, 0, true, false, false),
		(6, 4, 3, 0, true, false, false), (6, 5, 0, 0, true, false, false), (6, 5, 2, 0, true, false, false),
		(6, 6, 0, 0, true, false, false), (6, 6, 1, 0, true, false, false), (6, 7, 0, 0, true, false, false),
		(7, 0, 0, 1, true, false, false), (7, 0, 0, 3, true, false, false)
	];

	/// <summary>
	///     从 64 字节签名缓冲区中解析数据版本（签名至少 12 字节）。
	/// </summary>
	/// <exception cref="InnoFormatException">签名不属于 Inno Setup。</exception>
	public static InnoVersion Parse(ReadOnlySpan<byte> signature) {
		// 老式 1.2.10 签名："i1.2.10--16\x1a" 或 "i1.2.10--32\x1a"
		if (signature[0] == (byte)'i' && signature[11] == 0x1A) {
			var is16 = signature[9] == '1' && signature[10] == '6';
			var is32 = signature[9] == '3' && signature[10] == '2';
			if (signature[2] == '.' && signature[4] == '.' && signature[7] == '-' && signature[8] == '-' && (is16 || is32)) {
				var a = (uint)(signature[1] - '0');
				var b = (uint)(signature[3] - '0');
				var c = (uint)((signature[5] - '0') * 10 + (signature[6] - '0'));
				return new(a, b, c, 0, false, false, is16, true);
			}

			throw new InnoFormatException("无法识别的旧版 Inno Setup 签名");
		}

		// 标准签名：读取到首个 '\0'
		var length = signature.IndexOf((byte)0);
		if (length < 0) {
			length = signature.Length;
		}

		var text = Encoding.ASCII.GetString(signature[..length]);

		if (!text.Contains("Inno Setup", StringComparison.Ordinal)) {
			throw new InnoFormatException("不是 Inno Setup 安装包（缺少签名）");
		}

		for (var i = 0; i < _knownSignatures.Length; i++) {
			if (text == _knownSignatures[i]) {
				var (a, b, c, d, unicode, isx, _) = _knownVersions[i];
				return new(a, b, c, d, unicode, isx, false, true);
			}
		}

		// 未知签名：解析括号内所有版本号，取最大者
		uint major = 0, minor = 0, patch = 0, rev = 0;
		var found = false;
		var bracket = text.IndexOf('(');
		while (bracket >= 0 && bracket + 6 <= text.Length) {
			var aStart = bracket + 1;
			var aEnd = FindFirstNonDigit(text, aStart);
			if (aEnd > aStart && aEnd < text.Length && text[aEnd] == '.') {
				var bStart = aEnd + 1;
				var bEnd = FindFirstNonDigit(text, bStart);
				if (bEnd > bStart && bEnd < text.Length && text[bEnd] == '.') {
					var cStart = bEnd + 1;
					var cEnd = FindFirstNonDigit(text, cStart);
					if (cEnd > cStart && cEnd < text.Length) {
						var a = ParseSegment(text, aStart, aEnd);
						var b = ParseSegment(text, bStart, bEnd);
						var c = ParseSegment(text, cStart, cEnd);
						uint d = 0;
						var dStart = cEnd;
						if (text[dStart] == 'a') {
							dStart++;
						}

						if (dStart < text.Length && text[dStart] == '.') {
							dStart++;
							var dEnd = FindFirstNonDigit(text, dStart);
							if (dEnd > dStart) {
								d = ParseSegment(text, dStart, dEnd);
							}
						}

						if (CompareNumeric(a, b, c, d, major, minor, patch, rev) > 0) {
							major = a;
							minor = b;
							patch = c;
							rev = d;
						}

						found = true;
					}
				}
			}

			bracket = text.IndexOf('(', bracket + 1);
		}

		if (!found) {
			throw new InnoFormatException($"无法解析 Inno Setup 版本签名：\"{text}\"");
		}

		var unicodeVariant = major >= 6 || text.Contains("(u)", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("(U)", StringComparison.Ordinal);
		var isxVariant = text.Contains("My Inno Setup Extensions", StringComparison.Ordinal)
			|| text.Contains("with ISX", StringComparison.Ordinal);
		return new(major, minor, patch, rev, unicodeVariant, isxVariant, false, false);
	}

	/// <summary>
	///     版本是否具有歧义（可能对应下一个已知版本），需要渐进解析。
	/// </summary>
	public static bool IsAmbiguous(InnoVersion version) {
		return (version.Major, version.Minor, version.Patch) switch {
			(1, 3, 21) => true,
			(2, 0, 1) => true,
			(3, 0, 3) => true,
			(4, 2, 3) => true,
			(5, 3, 10) => true,
			(5, 4, 2) => true,
			(5, 5, 0) => true,
			(5, 5, 7) => true,
			_ => false
		};
	}

	/// <summary>
	///     返回版本表中下一个已知版本（用于渐进解析歧义版本）。
	/// </summary>
	public static InnoVersion? Next(InnoVersion version) {
		for (var i = 0; i < _knownVersions.Length; i++) {
			var (a, b, c, d, unicode, _, _) = _knownVersions[i];
			if (CompareNumeric(a, b, c, d, version.Major, version.Minor, version.Patch, version.Revision) > 0
				&& unicode == version.IsUnicode) {
				return new InnoVersion(a, b, c, d, unicode, false, false, true);
			}
		}

		return null;
	}

	static private int FindFirstNonDigit(string text, int start) {
		var i = start;
		while (i < text.Length && text[i] >= '0' && text[i] <= '9') {
			i++;
		}

		return i;
	}

	static private uint ParseSegment(string text, int start, int end) {
		uint value = 0;
		for (var i = start; i < end; i++) {
			value = value * 10 + (uint)(text[i] - '0');
		}

		return value;
	}

	static private int CompareNumeric(uint a1, uint b1, uint c1, uint d1, uint a2, uint b2, uint c2, uint d2) {
		var v1 = (ulong)a1 << 48 | (ulong)b1 << 32 | (ulong)c1 << 16 | d1;
		var v2 = (ulong)a2 << 48 | (ulong)b2 << 32 | (ulong)c2 << 16 | d2;
		return v1.CompareTo(v2);
	}
}