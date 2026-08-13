# InnoUnpack.NET.Cli

`inno-unpack`：跨平台 Inno Setup 安装包解压命令行工具，基于 [InnoUnpack.NET](../README.md) 库。

## 安装

```bash
# 本地打包并安装（无需发布到 NuGet）
dotnet pack tools/cli -c Release -o ./artifacts/tools
dotnet tool install --global --add-source ./artifacts/tools InnoUnpack.NET.Cli
```

## 用法

```bash
inno-unpack list    <installer>             # 列出全部文件
inno-unpack info    <installer>             # 显示元数据
inno-unpack extract <installer> [outDir]    # 解压到目录（默认当前目录）
```

## 示例

```bash
inno-unpack info setup.exe
inno-unpack extract setup.exe output
```

## 许可证

[MIT](../LICENSE)
