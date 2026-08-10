using System.Text;

namespace InnoUnpack.NET.Metadata;

/// <summary>
///     Inno Setup 数据版本号（安装包签名串中的版本，通常落后于发布版本）。
///     支持最多四段版本号（如 7.0.0.3），以及 Unicode / ISX 变体标记。
/// </summary>
public readonly struct InnoVersion : IComparable<InnoVersion>, IEquatable<InnoVersion> {
	/// <summary>第一位版本号。</summary>
	public uint Major { get; }

	/// <summary>第二位版本号。</summary>
	public uint Minor { get; }

	/// <summary>第三位版本号。</summary>
	public uint Patch { get; }

	/// <summary>第四位版本号（无则为 0）。</summary>
	public uint Revision { get; }

	/// <summary>是否为 Unicode 变体（5.2.5+ 或签名带 "(u)"）。</summary>
	public bool IsUnicode { get; }

	/// <summary>是否为 My Inno Setup Extensions (ISX) 变体。</summary>
	public bool IsIsx { get; }

	/// <summary>是否为 16 位安装包。</summary>
	public bool Is16Bit { get; }

	/// <summary>签名是否匹配已知版本表。</summary>
	public bool IsKnown { get; }

	internal InnoVersion(
		uint major,
		uint minor,
		uint patch,
		uint revision,
		bool isUnicode,
		bool isIsx,
		bool is16Bit,
		bool isKnown) {
		Major = major;
		Minor = minor;
		Patch = patch;
		Revision = revision;
		IsUnicode = isUnicode;
		IsIsx = isIsx;
		Is16Bit = is16Bit;
		IsKnown = isKnown;
		NumericValue = (ulong)major << 48 | (ulong)minor << 32 | (ulong)patch << 16 | revision;
	}

	/// <summary>基于现有版本的变体标记构造指定版本号（用于版本比较）。</summary>
	internal static InnoVersion From(uint major, uint minor, uint patch, InnoVersion like)
		=> new(major, minor, patch, 0, like.IsUnicode, like.IsIsx, like.Is16Bit, isKnown: true);

	/// <summary>转换为纯版本号（忽略变体标记）。</summary>
	private ulong NumericValue { get; }

	public int CompareTo(InnoVersion other) { return NumericValue.CompareTo(other.NumericValue); }

	public bool Equals(InnoVersion other) {
		return NumericValue == other.NumericValue && IsUnicode == other.IsUnicode && IsIsx == other.IsIsx;
	}

	public override bool Equals(object? obj) { return obj is InnoVersion other && Equals(other); }

	public override int GetHashCode() { return NumericValue.GetHashCode(); }

	public static bool operator <(InnoVersion left, InnoVersion right) { return left.CompareTo(right) < 0; }

	public static bool operator >(InnoVersion left, InnoVersion right) { return left.CompareTo(right) > 0; }

	public static bool operator <=(InnoVersion left, InnoVersion right) { return left.CompareTo(right) <= 0; }

	public static bool operator >=(InnoVersion left, InnoVersion right) { return left.CompareTo(right) >= 0; }

	public static bool operator ==(InnoVersion left, InnoVersion right) { return left.Equals(right); }

	public static bool operator !=(InnoVersion left, InnoVersion right) { return !left.Equals(right); }

	public override string ToString() {
		StringBuilder builder = new();
		builder.Append(Major).Append('.').Append(Minor).Append('.').Append(Patch);
		if (Revision != 0) {
			builder.Append('.').Append(Revision);
		}

		if (IsUnicode) {
			builder.Append(" (unicode)");
		}

		if (IsIsx) {
			builder.Append(" (isx)");
		}

		return builder.ToString();
	}
}