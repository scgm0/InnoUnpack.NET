# InnoUnpack.NET

跨平台、纯托管的 [Inno Setup](https://jrsoftware.org/isinfo.php) 安装包解压库（.NET 10，**零 NuGet 依赖**）。

[![NuGet](https://img.shields.io/nuget/v/InnoUnpack.NET)](https://www.nuget.org/packages/InnoUnpack.NET)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## 功能

- 支持 **Inno Setup 4.0 – 7.x**（ANSI 与 Unicode，含 6.5.0+ 新版偏移表布局）
- 全部压缩算法自研实现：`stored` / `zlib` / `bzip2` / `LZMA1` / `LZMA2`（无第三方依赖）
- 加密安装包解密：
  - ARC4 + MD5（4.2.2 – 5.3.8）
  - ARC4 + SHA1（5.3.9 – 6.3.x）
  - XChaCha20（6.4.0+，含 6.5.0+ 独立加密头）
- 可执行文件调用指令优化还原（Call Instruction Optimizer，4.1.8+ 默认启用）
- 文件校验和验证（MD5 / SHA1 / SHA256 / CRC32 / Adler32）
- 异步 API（`OpenAsync` / `ExtractToDirectoryAsync` / `IAsyncDisposable`）
- 基于**文件数**与**字节数**的进度报告（绝对进度，百分比由调用方计算）
- 路径安全防护（拒绝路径穿越与绝对路径）
- **多磁盘安装包**（数据在 `setup-N.bin` 切片中）
- **卸载程序提取**（4.x 包内嵌的卸载程序数据）

## 安装

```
dotnet add package InnoUnpack.NET
```

## 快速开始

```csharp
using InnoUnpack.NET;

using var archive = InnoSetupArchive.Open("setup.exe");

Console.WriteLine($"文件数：{archive.FileCount}，总大小：{archive.TotalFileSize} 字节");

foreach (var file in archive.EnumerateFiles())
{
    Console.WriteLine($"{file.Path} ({file.Size} bytes)");
}

archive.ExtractToDirectory("output");
```

## 异步提取与进度

```csharp
using var archive = await InnoSetupArchive.OpenAsync("setup.exe");

var totalFiles = archive.FileCount;
var totalBytes = archive.TotalFileSize;

var options = new ExtractionOptions();
options.ProgressChanged += p =>
{
    double filePercent = totalFiles == 0 ? 100 : (double)p.FilesExtracted / totalFiles * 100;
    double bytePercent  = totalBytes == 0 ? 100 : (double)p.BytesExtracted / totalBytes * 100;
    Console.WriteLine($"{p.FilesExtracted}/{totalFiles} 文件（{filePercent:F1}%），{bytePercent:F1}%");
};

await archive.ExtractToDirectoryAsync("output", options);
```

## 加密安装包

```csharp
var options = new InnoOpenOptions { Password = "your-password" };
using var archive = InnoSetupArchive.Open("encrypted-setup.exe", options);
archive.ExtractToDirectory("output");
```

- 密码错误时 `Open` 抛出 `InnoFormatException`（"密码不正确"）
- 未提供密码时仍可打开与枚举文件，提取加密文件时抛出 `InnoUnsupportedException`

## 多磁盘安装包

数据位于 `setup-N.bin` 切片中的安装包**无需特殊处理**，直接打开：

```csharp
// setup.exe 的元数据内嵌，数据在同目录的 setup-1.bin / setup-2.bin ... 中
using var archive = InnoSetupArchive.Open("setup.exe");
archive.ExtractToDirectory("output");
```

> 注意：从 `Stream` 打开（`Open(Stream, ...)`）不支持多磁盘安装包，必须使用文件路径重载。

## 其他 API

| API | 说明 |
| --- | --- |
| `IsInnoSetup(Stream)` / `IsInnoSetupAsync` | 无副作用检测（不改变流位置） |
| `OpenFile(InnoArchiveFile)` | 打开单个文件的数据流 |
| `InnoArchiveFile` | 源文件名 / 目标路径 / 大小 / 时间戳 / 文件版本 |
| `InnoOpenOptions` | 强制代码页 / 密码 / 路径变量映射 |
| `ExtractionOptions` | 时间戳保留 / 校验和验证 / 覆盖策略 / 进度报告 |

## 支持范围

- 平台：.NET 10（跨平台，Windows / Linux / macOS）
- **依赖：零**（全部压缩与加密算法自研实现）
- 已知限制：
  - 5.x+ 安装包的卸载程序（UninstExe）数据由安装器运行时生成，不包含在包内（不提取）
  - 加密安装包的 6.5.0+ 解密支持经过算法级验证，但尚未经真实加密样本端到端验证

## 许可证

[MIT](LICENSE)

## 致谢

- [InnoUnpacker-Windows-GUI](https://github.com/jrathlev/InnoUnpacker-Windows-GUI)（MIT）—— 加密、指令优化与 bzip2 的格式/算法参考
- [LZMA SDK](https://www.7-zip.org/sdk.html)（Public Domain）—— LZMA1 解码器移植来源
- [innoextract](https://github.com/dscharrer/innoextract) —— 二进制格式文档参考
