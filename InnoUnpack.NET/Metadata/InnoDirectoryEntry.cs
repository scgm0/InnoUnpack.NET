using InnoUnpack.NET.Reading;

namespace InnoUnpack.NET.Metadata;

/// <summary>
///     目录条目（对应 TSetupDirEntry）。
/// </summary>
public sealed class InnoDirectoryEntry {
	/// <summary>目录名（可能包含 {app} 等常量）。</summary>
	public string Name { get; internal set; } = string.Empty;

	/// <summary>组件条件。</summary>
	public string Components { get; internal set; } = string.Empty;

	/// <summary>任务条件。</summary>
	public string Tasks { get; internal set; } = string.Empty;

	/// <summary>语言条件。</summary>
	public string Languages { get; internal set; } = string.Empty;

	/// <summary>Check 函数条件。</summary>
	public string Check { get; internal set; } = string.Empty;

	/// <summary>AfterInstall 函数。</summary>
	public string AfterInstall { get; internal set; } = string.Empty;

	/// <summary>BeforeInstall 函数。</summary>
	public string BeforeInstall { get; internal set; } = string.Empty;

	/// <summary>所需最小 Windows 版本。</summary>
	public InnoWindowsVersionRange MinVersion { get; internal set; }

	/// <summary>目录属性。</summary>
	public uint Attributes { get; internal set; }

	/// <summary>权限（-1 表示未指定）。</summary>
	public short Permission { get; internal set; } = -1;

	/// <summary>选项标志。</summary>
	public InnoDirectoryOptions Options { get; internal set; }

	/// <summary>内部：解析目录条目。</summary>
	internal static InnoDirectoryEntry Parse(InnoBinaryReader reader, InnoVersion version, int codepage, bool[]? leadBytes) {
		InnoHeader.VersionGates v = new(version);
		InnoDirectoryEntry entry = new() {
			Name = InnoStringDecoder.Decode(reader.ReadStringBytes(), codepage, leadBytes)
		};

		if (v.Ge200) {
			entry.Components = InnoStringDecoder.Decode(reader.ReadStringBytes(), codepage);
			entry.Tasks = InnoStringDecoder.Decode(reader.ReadStringBytes(), codepage);
		}

		if (v.Ge401) {
			entry.Languages = InnoStringDecoder.Decode(reader.ReadStringBytes(), codepage);
		}

		if (v.Ge400) {
			entry.Check = InnoStringDecoder.Decode(reader.ReadStringBytes(), codepage);
		}

		if (v.Ge410) {
			entry.AfterInstall = InnoStringDecoder.Decode(reader.ReadStringBytes(), codepage);
			entry.BeforeInstall = InnoStringDecoder.Decode(reader.ReadStringBytes(), codepage);
		}

		// 版本数据
		_ = ReadWindowsVersionRange(reader);

		if (v.Ge2011) {
			entry.Attributes = reader.ReadUInt32();
		}

		if (v.Ge410) {
			entry.Permission = unchecked((short)reader.ReadUInt16());
		}

		if (v.Ge520) {
			var options = reader.ReadByte();
			entry.Options = (InnoDirectoryOptions)options;
		} else {
			var options = reader.ReadByte();
			entry.Options = (InnoDirectoryOptions)(options & 0x07);
		}

		return entry;
	}

	static private InnoWindowsVersionRange ReadWindowsVersionRange(InnoBinaryReader reader) {
		return new(ReadOne(), ReadOne());

		InnoWindowsVersion ReadOne() {
			var build = reader.ReadUInt16();
			var minor = reader.ReadByte();
			var major = reader.ReadByte();
			_ = reader.ReadUInt16(); // nt_version build
			_ = reader.ReadByte(); // nt_version minor
			_ = reader.ReadByte(); // nt_version major
			_ = reader.ReadUInt16(); // nt_service_pack（major+minor）
			return new(major, minor, build);
		}
	}
}