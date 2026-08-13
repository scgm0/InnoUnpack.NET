using InnoUnpack.NET;

// 最小示例：打开安装包，列出文件，提取单个文件。

if (args.Length == 0) {
	Console.Error.WriteLine("用法：dotnet run --project samples -- <setup.exe>");
	return 1;
}

using var archive = InnoSetupArchive.Open(args[0]);

Console.WriteLine($"文件数：{archive.FileCount}，总大小：{archive.TotalFileSize:N0} 字节");
Console.WriteLine();

foreach (var file in archive.EnumerateFiles()) {
	Console.WriteLine($"  {file.Path} ({file.Size} bytes)");
}

// 提取最大的文件到当前目录
var largest = archive.EnumerateFiles().OrderByDescending(f => f.Size).First();
Console.WriteLine();
Console.WriteLine($"提取最大文件：{largest.Path}");
archive.ExtractFile(largest.Path, "output");
Console.WriteLine("完成。");
return 0;
