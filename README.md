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
- **并行提取**：多个 archive 实例可并发；单包内 `ExtractionOptions.MaxParallelism` 并行独立 chunk 组
  （仅对非固体安装包有收益，经文件路径打开时生效；固体包为单 LZMA2 流，chunk 间概率模型延续，无法并行，
  但含字典复位点的 LZMA2 流可按复位点分段并行，见[性能](#性能)节）
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
  多个安装包可同时提取；同一实例不支持并发调用（`MaxParallelism` 为单次调用内部的组间并行）
- 已知限制：
    - 5.x+ 安装包的卸载程序（UninstExe）数据由安装器运行时生成，不包含在包内（不提取）
    - 加密安装包的 6.5.0+ 解密支持经过算法级验证，但尚未经真实加密样本端到端验证

## 性能

### 与 innoextract 对比（同等条件）

同一文件集（排除 innoextract 默认跳过的卸载程序）、关闭校验和与时间戳、相同输出目录、 **best-of-7**（innoextract 为冷进程整进程墙钟）。测试机：i5-9300H / Linux。
innoextract 1.9 声明支持至 Inno Setup 6.0.5；6.7.3/7.0.2 样本使用
[dscho 的 inno-6.4-to-6.7-support 分支](https://github.com/dscho/innoextract/tree/inno-6.4-to-6.7-support) 自构建版本对比
（`bash tools/bench/compare.sh`，`INNOEXTRACT_BIN` 环境变量指定路径）：

| fixture                              | 大小     | innoextract | JIT（冷进程） | JIT（热路径） | AOT-Speed（冷进程） |
|--------------------------------------|----------|-------------|---------------|---------------|---------------------|
| isetup-4.2.7.exe（LZMA1）            | 2.9 MiB  | 45 ms       | 103 ms        | 81 ms         | 112 ms              |
| innosetup-5.5.9-unicode.exe（LZMA2） | 5.4 MiB  | 89 ms       | 129 ms        | 106 ms        | 159 ms              |
| innosetup-5.6.1-unicode.exe（LZMA2） | 5.3 MiB  | 95 ms       | 132 ms        | 102 ms        | 146 ms              |
| innosetup-6.7.3.exe（LZMA2，8 MiB 字典） | 27.6 MiB | 528 ms   | 555 ms        | 500 ms        | 788 ms              |
| innosetup-7.0.2-x64.exe（LZMA2，8 MiB 字典） | 49.0 MiB | 955 ms | 978 ms        | 886 ms        | 1246 ms             |

- **LZMA2（现代安装包）**：热路径与原生 liblzma 持平或略快（6.7.3 约 1.06x、7.0.2 约 1.08x）。
  纯托管 LZMA 位解码器已对齐 liblzma 结构（串行 range coder 每比特一次 32 位乘法，双方均无 SIMD 空间）；
  实测字面树展开、强制内联等 JIT 结构调整均无正收益，匹配复制已用 memmove/填充快路径（优于 C 的逐字节循环）
- **小样本（4.2.7/5.5.9）**：4.2.7 差距来自单文件提取开销（55 个文件的建目录/打开/写盘系统调用），
  解码器本身符号吞吐与 liblzma 一致（实测 ~17–20M 符号/秒）；5.5.9 差距同时含样本内 40% 未压缩 chunk 的逐字节
  字典写入（已改为分段 memcpy，见下）
- **高熵负载（如 Visual Studio 安装器，95% 字面量、427 符号/KB）**：本库 ~17M 符号/秒 vs liblzma ~28M，
  落后约 1.6–1.7x——字面量 8 位树为纯串行位解码，属托管标量代码生成上限（官方安装器样本 51–65% 字面量
  下与 liblzma 持平，即上表）
- **冷进程（CLI 场景）**：JIT 编译开销约 20–90 ms（随夹具增大）；AOT 消除后冷启动与 JIT 热路径持平

### LZMA2 解码器实现说明

- **内存模式**：压缩区域 ≤ 96 MiB 时构造期整体预取到池化缓冲，chunk 头与码流直接寻址，
  消除逐 chunk 的流读取与输入缓冲搬移（区域按块头给出的长度可能略短，末尾以 0xFF 填充容忍，与流模式一致）
- **未压缩 chunk 快路径**：字典写入按环回边界分段 `memcpy`（VS 安装器 40% 数据为未压缩 chunk）
- **并行解码（对应 7-Zip Lzma2DecMt 架构）**：LZMA2 仅在带字典复位（ctrl ≥ 0xE0）的 chunk 处允许独立分段，
  复位后匹配只能引用本段内已解出的数据，各段可完全并行。流含 ≥ 2 个复位点且输出 ≤ 256 MiB 时按复位点分段并发解码
  （`ExtractionOptions.MaxParallelism` 控制 worker 数）；合成多复位流实测 4/8 worker 约 1.6x/1.9x。
  **Inno Setup 生成的流仅流首有一个复位点，不会触发并行路径**——固体包为单一 LZMA2 流，
  chunk 间概率模型延续（串行依赖），并行解码不可行（与 7-Zip/liblzma 多线程解码对无复位流的行为一致）

### AOT（NativeAOT）三种 OptimizationPreference

`dotnet publish tools/bench -c Release -r linux-x64 -p:PublishAot=true -p:OptimizationPreference=<模式>`，
冷进程单次提取（同等条件，best-of-7）：

| fixture                     | Default | Speed      | Size   |
|-----------------------------|---------|------------|--------|
| isetup-4.2.7.exe            | 111 ms  | **81 ms**  | 111 ms |
| innosetup-5.5.9-unicode.exe | 153 ms  | **108 ms** | 169 ms |
| innosetup-5.6.1-unicode.exe | 142 ms  | **104 ms** | 142 ms |
| innosetup-6.7.3.exe         | 695 ms  | **563 ms** | 720 ms |
| innosetup-7.0.2-x64.exe     | 1257 ms | **937 ms** | 1318 ms |

`OptimizationPreference=Speed` 耗时为 Default/Size 的 **0.71–0.81 倍**（快约 19–29%）；
热路径下 AOT-Speed 与 JIT 持平，Default/Size 约慢 30–40%（JIT 分层编译对热点循环生成更好的代码）。
原生二进制约 3 MB，无运行时依赖。

### 内存

- 解码器字典/概率表/输入输出缓冲全部经 `ArrayPool` 租用并在流销毁时归还： **大缓冲零分配、
  提取全程 GC 零收集**（gc0/gc1/gc2 均为 0 次）；池预热后单次提取仅 0.23–0.55 MiB 小对象分配
- bzip2 块缓冲（≤900 KB）同样经 `ArrayPool` 租用并随流销毁归还（块输出直接手交解码器缓冲，
  无 LOH 复制）；提取泵（256KB）、`SkipBytes` 跳跃缓冲全部池化；MD5/SHA1/SHA256 校验哈希跨文件复用（`GetHashAndReset`）
- 文件写出使用 `RandomAccess` 直接 OS 写入，无 FileStream 内部缓冲分配

### 异步语义

`ExtractToDirectoryAsync` 将整个提取流程卸载到线程池执行（等价于 `Task.Run(ExtractToDirectory)`），
调用线程不被阻塞；解压为 CPU 密集工作，进度回调在线程池线程触发（UI 调用方需自行 marshal）。

### 复现

```bash
# 工具与脚本位于 tools/bench
# INNOEXTRACT_BIN 可指定自定义 innoextract 构建路径（如支持 Inno 6.7/7.0 的版本）
INNOEXTRACT_BIN=/path/to/innoextract bash tools/bench/compare.sh <fixtures-dir>  # 自动发布 AOT×3 并输出全部对比表
dotnet run --project tools/bench -c Release -- <fixtures-dir> gc      # 冷进程分配/GC
dotnet run --project tools/bench -c Release -- <fixtures-dir> gcwarm  # 池预热后分配/GC
dotnet run --project tools/bench -c Release -- crypto <size-mb>       # XChaCha20 解密吞吐（SIMD 门禁）
dotnet run --project tools/bench -c Release -- parallel <fixtures-dir> # 独立 chunk 并行解码门禁（串行 vs 并发）
dotnet run --project tools/bench -c Release -- decode <fixtures-dir> <fixture>  # 纯解码门禁（不写盘）
```

Windows 对应版本（PowerShell 5.1+/7 均可，同样支持 `INNOEXTRACT_BIN`）：

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
