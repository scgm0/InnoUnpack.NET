namespace InnoUnpack.NET;

/// <summary>
///     表示解析安装包时遇到的格式错误（数据损坏或非 Inno Setup 文件）。
/// </summary>
public class InnoFormatException : Exception {
	public InnoFormatException(string message) : base(message) { }

	public InnoFormatException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
///     表示文件本身是 Inno Setup 安装包，但其使用的特性或版本不受支持。
/// </summary>
public class InnoUnsupportedException(string message) : Exception(message);