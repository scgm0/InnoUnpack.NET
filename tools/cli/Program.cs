using InnoUnpack.NET;

/*
 * InnoUnpack.NET 命令行工具（dotnet tool）。
 *
 * 用法：
 *   inno-unpack list    <installer>            列出全部文件
 *   inno-unpack info    <installer>            显示安装包元数据
 *   inno-unpack extract <installer> [outDir]   解压到目录（默认当前目录）
 */

if (args.Length < 2) {
	PrintUsage();
	return 1;
}

var command = args[0];
var installer = args[1];

try {
	switch (command) {
		case "list": {
			using var archive = InnoSetupArchive.Open(installer);
			foreach (var file in archive.EnumerateFiles()) {
				Console.WriteLine($"{file.Path}\t{file.Size}");
			}

			return 0;
		}
		case "info": {
			using var archive = InnoSetupArchive.Open(installer);
			var info = archive.Info;
			var h = info.Header;
			Console.WriteLine($"应用名：{h.AppName}");
			Console.WriteLine($"版本：{h.AppVersion}");
			Console.WriteLine($"数据版本：{info.Version}");
			Console.WriteLine($"压缩：{h.Compression}");
			Console.WriteLine($"加密：{(info.IsEncrypted ? "是" : "否")}");
			Console.WriteLine($"文件数：{archive.FileCount}，总大小：{archive.TotalFileSize:N0} 字节");
			Console.WriteLine($"目录数：{info.Directories.Count}");
			Console.WriteLine($"类型数：{info.Types.Count}，组件数：{info.Components.Count}，任务数：{info.Tasks.Count}");
			Console.WriteLine($"注册表条目：{info.RegistryEntries.Count}，运行条目：{info.RunEntries.Count}");
			return 0;
		}
		case "extract": {
			var outDir = args.Length > 2 ? args[2] : Environment.CurrentDirectory;
			using var archive = InnoSetupArchive.Open(installer);
			Console.WriteLine($"解压 {archive.FileCount} 个文件到 {outDir} ...");
			archive.ExtractToDirectory(outDir);
			Console.WriteLine("完成。");
			return 0;
		}
		default:
			PrintUsage();
			return 1;
	}
} catch (InnoFormatException ex) {
	Console.Error.WriteLine($"格式错误：{ex.Message}");
	return 2;
} catch (InnoUnsupportedException ex) {
	Console.Error.WriteLine($"不支持：{ex.Message}");
	return 3;
}

static void PrintUsage() {
	Console.Error.WriteLine("用法：inno-unpack <list|info|extract> <installer> [outDir]");
}
