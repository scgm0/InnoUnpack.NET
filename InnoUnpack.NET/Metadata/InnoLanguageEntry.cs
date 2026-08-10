using InnoUnpack.NET.Reading;

namespace InnoUnpack.NET.Metadata;

/// <summary>
///     语言条目（对应 TSetupLanguageEntry）。
/// </summary>
public sealed class InnoLanguageEntry {
	/// <summary>语言名称（内部名，空表示默认）。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>解析期保存的名称原始字节（代码页确定后解码）。</summary>
	internal byte[] NameRaw { get; set; } = [];

	/// <summary>语言显示名。</summary>
	public string LanguageName { get; internal set; } = string.Empty;

	/// <summary>对话框字体。</summary>
	public string DialogFont { get; internal set; } = string.Empty;

	/// <summary>标题字体（6.6.0 起不再存储）。</summary>
	public string TitleFont { get; internal set; } = string.Empty;

	/// <summary>欢迎页字体。</summary>
	public string WelcomeFont { get; internal set; } = string.Empty;

	/// <summary>版权字体（6.6.0 起不再存储）。</summary>
	public string CopyrightFont { get; internal set; } = string.Empty;

	/// <summary>语言数据（翻译字符串）。</summary>
	public byte[] Data { get; internal set; } = [];

	/// <summary>许可协议文本。</summary>
	public byte[] LicenseText { get; internal set; } = [];

	/// <summary>安装前信息。</summary>
	public byte[] InfoBefore { get; internal set; } = [];

	/// <summary>安装后信息。</summary>
	public byte[] InfoAfter { get; internal set; } = [];

	/// <summary>Windows 语言 ID。</summary>
	public uint LanguageId { get; internal set; }

	/// <summary>该语言默认使用的 Windows 代码页。</summary>
	public uint Codepage { get; internal set; }

	/// <summary>对话框字体大小。</summary>
	public uint DialogFontSize { get; internal set; }

	/// <summary>对话框字体标准高度（4.1.0 之前）。</summary>
	public uint DialogFontStandardHeight { get; internal set; }

	/// <summary>对话框字体基准缩放高度（6.6.0+）。</summary>
	public uint DialogFontBaseScaleHeight { get; internal set; }

	/// <summary>对话框字体基准缩放宽度（6.6.0+）。</summary>
	public uint DialogFontBaseScaleWidth { get; internal set; }

	/// <summary>标题字体大小。</summary>
	public uint TitleFontSize { get; internal set; }

	/// <summary>欢迎页字体大小。</summary>
	public uint WelcomeFontSize { get; internal set; }

	/// <summary>版权字体大小。</summary>
	public uint CopyrightFontSize { get; internal set; }

	/// <summary>是否从右向左。</summary>
	public bool RightToLeft { get; internal set; }

	/// <summary>内部：解析语言条目（原始字节，未解码）。</summary>
	internal static InnoLanguageEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage) {
		InnoHeader.VersionGates v = new(version);
		InnoLanguageEntry entry = new();

		if (v.Ge400) {
			entry.NameRaw = reader.ReadStringBytes();
		}

		entry.LanguageName = Decode(reader.ReadStringBytes(), v.Ge422 ? InnoStringDecoder.CpUtf16Le : codepage);
		entry.DialogFont = Decode(reader.ReadStringBytes(), codepage);
		if (v.Lt660) {
			entry.TitleFont = Decode(reader.ReadStringBytes(), codepage);
		}

		entry.WelcomeFont = Decode(reader.ReadStringBytes(), codepage);
		if (v.Lt660) {
			entry.CopyrightFont = Decode(reader.ReadStringBytes(), codepage);
		}

		if (v.Ge400) {
			entry.Data = reader.ReadStringBytes();
		}

		if (v.Ge401) {
			entry.LicenseText = reader.ReadStringBytes();
			entry.InfoBefore = reader.ReadStringBytes();
			entry.InfoAfter = reader.ReadStringBytes();
		}

		entry.LanguageId = v.Ge660 ? reader.ReadUInt16() : reader.ReadUInt32();

		if (v.Lt422) {
			entry.Codepage = DefaultCodepageForLanguage(entry.LanguageId);
		} else if (!version.IsUnicode) {
			entry.Codepage = reader.ReadUInt32();
			if (entry.Codepage == 0) {
				entry.Codepage = InnoStringDecoder.CpWindows1252;
			}
		} else {
			if (v.Lt530) {
				_ = reader.ReadUInt32();
			}

			entry.Codepage = InnoStringDecoder.CpUtf16Le;
		}

		entry.DialogFontSize = reader.ReadUInt32();
		if (v.Lt410) {
			entry.DialogFontStandardHeight = reader.ReadUInt32();
		}

		if (v.Ge660) {
			entry.DialogFontBaseScaleHeight = reader.ReadUInt32();
			entry.DialogFontBaseScaleWidth = reader.ReadUInt32();
		} else {
			entry.TitleFontSize = reader.ReadUInt32();
		}

		entry.WelcomeFontSize = reader.ReadUInt32();
		if (v.Lt660) {
			entry.CopyrightFontSize = reader.ReadUInt32();
		}

		if (v.Ge523) {
			entry.RightToLeft = reader.ReadByte() != 0;
		}

		return entry;
	}

	/// <summary>
	///     根据 Windows 语言 ID 返回默认代码页（未列出的语言返回 1252）。
	/// </summary>
	internal static uint DefaultCodepageForLanguage(uint languageId) {
		// 语言 ID → 默认 ANSI 代码页表（仅列非 1252 的语言）
		Span<(ushort Lang, ushort Cp)> table = [
			(0x0401, 1256), (0x0402, 1251), (0x0404, 950), (0x0405, 1250), (0x0408, 1253),
			(0x040D, 1255), (0x040E, 1250), (0x0411, 932), (0x0412, 949), (0x0415, 1250),
			(0x0418, 1250), (0x0419, 1251), (0x041A, 1250), (0x041B, 1250), (0x041C, 1250),
			(0x041E, 874), (0x041F, 1254), (0x0420, 1256), (0x0422, 1251), (0x0423, 1251),
			(0x0424, 1250), (0x0425, 1257), (0x0426, 1257), (0x0427, 1257), (0x0429, 1256),
			(0x042A, 1258), (0x042C, 1254), (0x042F, 1251), (0x043F, 1251), (0x0440, 1251),
			(0x0443, 1254), (0x0444, 1251), (0x0450, 1251), (0x0492, 28594),
			(0x0801, 1256), (0x0804, 936), (0x081A, 1250), (0x082C, 1251), (0x0843, 1251),
			(0x0C01, 1256), (0x0C04, 950), (0x0C1A, 1251),
			(0x1001, 1256), (0x1004, 936),
			(0x1401, 1256), (0x1404, 950),
			(0x1801, 1256), (0x1C01, 1256), (0x2001, 1256), (0x2401, 1256), (0x2801, 1256),
			(0x2C01, 1256), (0x3001, 1256), (0x3401, 1256), (0x3801, 1256), (0x3C01, 1256),
			(0x4001, 1256)
		];
		foreach (var (lang, cp) in table) {
			if (lang == languageId) {
				return cp;
			}
		}

		return InnoStringDecoder.CpWindows1252;
	}

	/// <summary>按确定的代码页解码语言名称。</summary>
	internal void DecodeName(int codepage) {
		Name = InnoStringDecoder.Decode(NameRaw, codepage);
		if (Name.Length == 0) {
			Name = "default";
		}
	}

	static private string Decode(byte[] data, int codepage) { return InnoStringDecoder.Decode(data, codepage); }
}