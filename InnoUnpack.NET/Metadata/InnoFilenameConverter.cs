using System.Text;

namespace InnoUnpack.NET.Metadata;

/// <summary>
///     将存储的 Windows 风格路径（可能包含 {app} 等常量）转换为输出文件名。
///     转换规则与 innoextract 的 filename_map 一致：
///     - 未映射的变量 {name} 展开为其名称本身（如 {app} → "app"）；
///     - "{{" 转义为 "{"；
///     - 不安全字符（&lt; &gt; : " | ? * 与控制字符）替换为 '$'；
///     - 清理 "." 与 ".." 路径段；
///     - 分隔符转换为当前平台的分隔符。
/// </summary>
public sealed class InnoFilenameConverter {
	private const string UnsafeChars = "<>:\"|?*";

	private readonly IReadOnlyDictionary<string, string> _mappings;

	/// <summary>创建转换器。</summary>
	/// <param name="mappings">变量名（不含花括号）到值的映射，未映射的变量保持名称本身。</param>
	public InnoFilenameConverter(IReadOnlyDictionary<string, string>? mappings = null) {
		_mappings = mappings ?? new Dictionary<string, string>();
	}

	/// <summary>
	///     转换路径。
	/// </summary>
	/// <param name="path">存储的 Windows 路径。</param>
	/// <param name="expand">是否展开变量（false 时仅替换不安全字符）。</param>
	public string Convert(string path, bool expand = true) {
		if (path.Length == 0) {
			return path;
		}

		var result = expand ? ExpandVariables(path) : ReplaceUnsafeChars(path);
		return ShortenPath(result);
	}

	/// <summary>替换路径中的不安全字符。</summary>
	public static string ReplaceUnsafeChars(string path) {
		StringBuilder builder = new(path.Length);
		foreach (var c in path) {
			builder.Append(c < 32 || UnsafeChars.Contains(c) ? '$' : c);
		}

		return builder.ToString();
	}

	private string ExpandVariables(string path) {
		var index = 0;
		StringBuilder builder = new(path.Length);
		while (index < path.Length) {
			var c = path[index];
			if (c != '{' && c != '}') {
				builder.Append(c);
				index++;
				continue;
			}

			if (c == '}') {
				// 单独的 '}' 是字面量
				builder.Append('}');
				index++;
				continue;
			}

			// "{{" 转义
			if (index + 1 < path.Length && path[index + 1] == '{') {
				builder.Append('{');
				index += 2;
				continue;
			}

			// 找到匹配的 '}'
			var close = path.IndexOf('}', index + 1);
			if (close < 0) {
				builder.Append('{');
				index++;
				continue;
			}

			var variable = path[(index + 1)..close];
			if (variable.Contains('{') || variable.Contains('}')) {
				// 嵌套变量：先展开内部
				variable = ExpandVariables(variable);
			}

			builder.Append(_mappings.TryGetValue(variable, out var mapped) ? mapped : ReplaceUnsafeChars(variable));
			index = close + 1;
		}

		return builder.ToString();
	}

	/// <summary>
	///     清理路径：忽略空段与 "."，处理 ".." 回退，转换分隔符。
	/// </summary>
	static private string ShortenPath(string path) {
		var separator = Path.DirectorySeparatorChar;
		List<string> segments = [];
		var start = 0;
		for (var i = 0; i <= path.Length; i++) {
			if (i < path.Length && path[i] != '\\' && path[i] != '/') {
				continue;
			}

			var segment = path[start..i];
			start = i + 1;
			if (segment.Length == 0 || segment == ".") {
				continue;
			}

			if (segment == "..") {
				if (segments.Count > 0) {
					segments.RemoveAt(segments.Count - 1);
				}

				continue;
			}

			segments.Add(segment);
		}

		return string.Join(separator, segments);
	}
}