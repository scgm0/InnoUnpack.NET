# InnoUnpack.NET

跨平台、纯C#的 [Inno Setup](https://jrsoftware.org/isinfo.php) 安装包解压库（.NET 10， **零 NuGet 依赖**）。

[![NuGet](https://img.shields.io/nuget/v/InnoUnpack.NET)](https://www.nuget.org/packages/InnoUnpack.NET)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## 功能

- 支持 **Inno Setup 4.0 – 7.x**（ANSI 与 Unicode，含 6.5.0+ 新版偏移表布局）
- 压缩算法全覆盖：`stored` / `zlib` / `bzip2` / `LZMA1` / `LZMA2`（`bzip2` 自定义实现，`LZMA1`/`LZMA2` 移植自 LZMA
  SDK（Public Domain），`zlib` 使用 .NET 内置，零第三方依赖）
- 加密安装包解密：
    - ARC4 + MD5（4.2.2 – 5.3.8）
    - ARC4 + SHA1（5.3.9 – 6.3.x）
    - XChaCha20（6.4.0+，含 6.5.0+ 独立加密头）
- 可执行文件调用指令优化还原（Call Instruction Optimizer，4.1.8+ 默认启用）
- 目录权限与属性应用（POSIX 权限 / Windows 文件属性，`ApplyDirectoryAttributes`，默认关闭）
- 逐文件提取过滤与输出路径映射（`FileFilter` / `OutputPathMapper`）
- 提取取消（同步/异步均支持 `CancellationToken`）
- **并行提取**：多个 archive 实例可并发；单包内 `ExtractionOptions.MaxParallelism` 并行独立 chunk 组（仅对非固体安装包有收益，经文件路径打开时生效）
- 文件校验和验证（MD5 / SHA1 / SHA256 / CRC32 / Adler32）
- 异步 API（`OpenAsync` / `ExtractToDirectoryAsync` / `IAsyncDisposable`）
- 基于 **文件数**与 **字节数**的进度报告（绝对进度，百分比由调用方计算）
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

**手动停止提取**：同步与异步 API 均支持 `CancellationToken`（逐文件与文件内读写块边界检查，
取消时抛出 `OperationCanceledException`，已完成的文件保留）：

```csharp
using var cts = new CancellationTokenSource();
cts.CancelAfter(TimeSpan.FromSeconds(5)); // 或由用户操作触发 cts.Cancel()

archive.ExtractToDirectory("output", options, cts.Token);        // 同步
await archive.ExtractToDirectoryAsync("output", options, cts.Token); // 异步
```

## 过滤与路径映射

`ExtractionOptions` 支持逐文件决策与输出路径自定义：

```csharp
var options = new ExtractionOptions {
    // 只提取 {app} 子树（提取过程中逐文件调用）
    FileFilter = f => f.Path.StartsWith("app/", StringComparison.Ordinal),

    // 把 {app} 下的文件映射到 "custom/..."（返回 null 使用默认路径；
    // 不安全路径（绝对/逃逸）自动回退默认，绝不写出输出目录）
    OutputPathMapper = f => f.Path.StartsWith("app/", StringComparison.Ordinal)
        ? "custom/" + f.Path[4..]
        : null,
};

archive.ExtractToDirectory("output", options);
```

- 被过滤的文件**不参与进度统计**（不计入已处理文件数、不触发进度事件）
- `FileFilter` 可基于任意文件属性判断：输出路径（`f.Path`）、安装器原始路径
  （`f.Destination`，含 `{app}` 常量与 Windows 分隔符）、源文件名（`f.SourceName`）、大小等
- 过滤后的文件总数可用 `archive.EnumerateFiles(filter).Count()` 计算（进度百分比对齐）
- 路径映射分两层，各司其职：
  - `InnoOpenOptions.PathMappings`（打开时）：全局常量替换，作用于**所有**路径（含目录条目），
    适合"整个包换前缀"（如 `{app}` → `myapp`）
  - `OutputPathMapper`（提取时）：逐文件条件化映射，作用于已展开路径，
    适合"个别文件重命名/分流"（如把某个 exe 单独输出到别的目录）；与 `FileFilter` 组合
    可实现"只提取 app 到指定文件夹"

## 加密安装包

```csharp
var options = new InnoOpenOptions { Password = "your-password" };
using var archive = InnoSetupArchive.Open("encrypted-setup.exe", options);
archive.ExtractToDirectory("output");
```

- 密码错误时 `Open` 抛出 `InnoFormatException`（"密码不正确"）
- 未提供密码时仍可打开与枚举文件，提取加密文件时抛出 `InnoUnsupportedException`

## 多磁盘安装包

数据位于 `setup-N.bin` 切片中的安装包 **无需特殊处理**，直接打开：

```csharp
// setup.exe 的元数据内嵌，数据在同目录的 setup-1.bin / setup-2.bin ... 中
using var archive = InnoSetupArchive.Open("setup.exe");
archive.ExtractToDirectory("output");
```

> 注意：从 `Stream` 打开（`Open(Stream, ...)`）不支持多磁盘安装包，必须使用文件路径重载。

## 其他 API

| API                                        | 说明                                           |
|--------------------------------------------|------------------------------------------------|
| `IsInnoSetup(Stream)` / `IsInnoSetupAsync` | 无副作用检测（不改变流位置）                   |
| `OpenFile(InnoArchiveFile)`                | 打开单个文件的数据流                           |
| `InnoArchiveFile`                          | 源文件名 / 目标路径 / 大小 / 时间戳 / 文件版本 |
| `InnoOpenOptions`                          | 强制代码页 / 密码 / 路径变量映射               |
| `ExtractionOptions`                        | 时间戳保留 / 校验和验证 / 覆盖策略 / 进度报告  |

## 支持范围

- 平台：.NET 10（跨平台，Windows / Linux）
- **依赖：零**（无 NuGet 依赖；`bzip2` 与加密算法自定义实现，`LZMA1`/`LZMA2` 移植自 LZMA SDK，`zlib` 使用 .NET 内置）
- **NativeAOT 兼容**（无反射，可 `PublishAot=true` 发布为原生二进制）
- **多线程并行**：不同 archive 实例间无共享可变状态（静态初始化已线程安全），
  多个安装包可同时提取；同一实例不支持并发访问
- 已知限制：
    - 5.x+ 安装包的卸载程序（UninstExe）数据由安装器运行时生成，不包含在包内（不提取）
    - 加密安装包的 6.5.0+ 解密支持经过算法级验证，但尚未经真实加密样本端到端验证

## 性能

### 与 innoextract 对比（同等条件）

同一文件集（排除 innoextract 默认跳过的卸载程序）、关闭校验和与时间戳、相同输出目录、 **best-of-7**。测试机：i5-9300H，Linux，测试夹具为
Inno Setup 官方安装器（innoextract 1.9
仅支持到 Inno Setup 6.0.5，故可对比 3 个）：

| fixture                              | 大小    | innoextract | 本库 JIT（冷进程） | 本库 JIT（热路径） | 本库 AOT-Speed（冷进程） |
|--------------------------------------|---------|-------------|--------------------|--------------------|--------------------------|
| isetup-4.2.7.exe（LZMA1）            | 2.9 MiB | 42 ms       | 127 ms             | 86 ms              | **86 ms**                |
| innosetup-5.5.9-unicode.exe（LZMA2） | 5.4 MiB | 88 ms       | 165 ms             | 117 ms             | **118 ms**               |
| innosetup-5.6.1-unicode.exe（LZMA2） | 5.3 MiB | 97 ms       | 152 ms             | 113 ms             | **114 ms**               |

- **热路径（库场景，进程内预热）**：与 innoextract 差距约 **1.2–2.1x**，剩余差距来自
  纯托管 LZMA 位解码器 vs liblzma 的原生标量代码（LZMA range coder 为串行位依赖，双方均无 SIMD）
- **冷进程（CLI 场景）**：AOT 消除了 JIT 编译开销（约 40–50 ms），AOT-Speed 冷启动与
  JIT 热路径基本持平

### AOT（NativeAOT）三种 OptimizationPreference

`dotnet publish tools/bench -c Release -r linux-x64 -p:PublishAot=true -p:OptimizationPreference=<模式>`，
冷进程单次提取（同等条件，best-of-7）：

| fixture                     | Default | Speed      | Size   |
|-----------------------------|---------|------------|--------|
| isetup-4.2.7.exe            | 117 ms  | **86 ms**  | 118 ms |
| innosetup-5.5.9-unicode.exe | 156 ms  | **118 ms** | 161 ms |
| innosetup-5.6.1-unicode.exe | 148 ms  | **114 ms** | 154 ms |

`OptimizationPreference=Speed` 耗时为 Default/Size 的 **0.74–0.77 倍**（快约 30–36%）；
热路径下 AOT-Speed 与 JIT 持平，Default/Size 约慢 30% 左右（JIT 分层编译对热点循环生成更好的代码）。
原生二进制约 3 MB，无运行时依赖。

### 内存

- 解码器字典/概率表/输入输出缓冲全部经 `ArrayPool` 租用并在流销毁时归还： **大缓冲零分配、
  提取全程 GC 零收集**（gc0/gc1/gc2 均为 0 次）；池预热后单次提取仅 0.23–0.55 MiB 小对象分配
- bzip2 块输出直接手交解码器缓冲（消除每块最大 900KB 的 LOH 复制）；提取泵（256KB）、
  `SkipBytes` 跳跃缓冲全部池化；MD5/SHA1/SHA256 校验哈希跨文件复用（`GetHashAndReset`）
- 文件写出使用 `RandomAccess` 直接 OS 写入，无 FileStream 内部缓冲分配

### 异步语义

`ExtractToDirectoryAsync` 将整个提取流程卸载到线程池执行（等价于 `Task.Run(ExtractToDirectory)`），
调用线程不被阻塞；解压为 CPU 密集工作，进度回调在线程池线程触发（UI 调用方需自行 marshal）。

### 复现

```bash
# 工具与脚本位于 tools/bench
bash tools/bench/compare.sh <fixtures-dir>   # 自动发布 AOT×3 并输出全部对比表
dotnet run --project tools/bench -c Release -- <fixtures-dir> gc      # 冷进程分配/GC
dotnet run --project tools/bench -c Release -- <fixtures-dir> gcwarm  # 池预热后分配/GC
dotnet run --project tools/bench -c Release -- crypto <size-mb>       # XChaCha20 解密吞吐（SIMD 门禁）
dotnet run --project tools/bench -c Release -- parallel <fixtures-dir> # 独立 chunk 并行解码门禁（串行 vs 并发）
```

Windows 对应版本（PowerShell 5.1+/7 均可）：

```powershell
tools\bench\compare.cmd [fixtures-dir]      # 快捷入口（自动选择 pwsh / powershell）
powershell -ExecutionPolicy Bypass -File tools\bench\compare.ps1 [-FixturesDir <dir>]
```

测试样本下载（Windows 下运行 `dotnet test` 前需要）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\download-fixtures.ps1
```

## 许可证

[MIT](LICENSE)

## 致谢

- [InnoUnpacker-Windows-GUI](https://github.com/jrathlev/InnoUnpacker-Windows-GUI)（MIT）—— 加密、指令优化与 bzip2 的格式/算法参考
- [LZMA SDK](https://www.7-zip.org/sdk.html)（Public Domain）—— LZMA1 解码器移植来源
- [innoextract](https://github.com/dscharrer/innoextract) —— 二进制格式文档参考
