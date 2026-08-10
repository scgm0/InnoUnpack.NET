using System.Text;

namespace InnoUnpack.NET.Reading;

/// <summary>
///     负责将 Inno Setup 文件中的原始字符串字节转换为 .NET 字符串。
///     Unicode 安装包（5.2.5+）的字符串为 UTF-16LE 编码；ANSI 安装包使用 Windows 代码页
///     （由语言表推断，默认 1252）。
/// </summary>
static class InnoStringDecoder {
	/// <summary>UTF-16LE 的 Windows 代码页号。</summary>
	public const int CpUtf16Le = 1200;

	/// <summary>Windows-1252 的代码页号。</summary>
	public const int CpWindows1252 = 1252;

	static private readonly Encoding _utf16Le =
		new UnicodeEncoding(false, false, false);

	static private bool _codePagesRegistered;

	static private void EnsureCodePages() {
		if (!_codePagesRegistered) {
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			_codePagesRegistered = true;
		}
	}

	/// <summary>获取指定 Windows 代码页对应的编码。</summary>
	public static Encoding GetEncoding(int codepage) {
		if (codepage == CpUtf16Le) {
			return _utf16Le;
		}

		EnsureCodePages();
		try {
			return Encoding.GetEncoding(codepage);
		} catch (ArgumentException) {
			return Encoding.GetEncoding(CpWindows1252);
		}
	}

	/// <summary>
	///     将原始字符串字节解码为字符串。
	/// </summary>
	/// <param name="data">原始字节（不包含长度前缀与终止符）。</param>
	/// <param name="codepage">Windows 代码页号，<see cref="CpUtf16Le" /> 表示 UTF-16LE。</param>
	/// <param name="leadBytes">
	///     ANSI 安装包的 lead-byte 表（256 位）。用于在双字节字符中保留 0x5C 路径分隔符，
	///     为 null 时不进行该处理。
	/// </param>
	public static string Decode(ReadOnlySpan<byte> data, int codepage, bool[]? leadBytes = null) {
		if (data.IsEmpty) {
			return string.Empty;
		}

		if (leadBytes is not null) {
			StringBuilder builder = new(data.Length);
			var start = 0;
			while (start < data.Length) {
				var end = start;
				while (end < data.Length) {
					if (leadBytes[data[end]]) {
						end = Math.Min(data.Length, end + 2);
					} else if (data[end] != 0x5C) {
						end++;
					} else {
						break;
					}
				}

				builder.Append(ConvertSegment(data[start..end], codepage));
				if (end < data.Length) {
					builder.Append('\\');
				}

				start = end + 1;
			}

			return builder.ToString();
		}

		return ConvertSegment(data, codepage);
	}

	static private string ConvertSegment(ReadOnlySpan<byte> data, int codepage) {
		return codepage == CpUtf16Le
			? _utf16Le.GetString(data)
			: GetEncoding(codepage).GetString(data);
	}
}