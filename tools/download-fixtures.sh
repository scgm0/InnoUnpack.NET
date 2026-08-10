#!/usr/bin/env bash
# 下载 Inno Setup 官方安装包作为测试样本（覆盖 4.x 至 7.x 各版本）。
# 文件保存到 InnoUnpack.Tests/Fixtures/（该目录不入库，见 .gitignore）。
set -euo pipefail

FIXTURES="$(cd "$(dirname "$0")/.." && pwd)/InnoUnpack.Tests/Fixtures"
mkdir -p "$FIXTURES"

# 4.x（ANSI 老格式，zlib/lzma1 块）
IS4="https://files.jrsoftware.org/is/4/isetup-4.2.7.exe"

# 5.x（ANSI / Unicode）
IS5="https://files.jrsoftware.org/is/5/innosetup-5.5.9-unicode.exe"
IS5_2="https://files.jrsoftware.org/is/5/innosetup-5.6.1-unicode.exe"

# 6.x / 7.x（GitHub Release 资产）
IS6="https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe"
IS7="https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-7.0.2-x64.exe"

download() {
    local url="$1"
    local name="$2"
    if [ ! -f "$FIXTURES/$name" ]; then
        echo "下载 $name ..."
        curl -sL --retry 3 -o "$FIXTURES/$name" "$url"
    else
        echo "已存在 $name"
    fi
}

download "$IS4" "isetup-4.2.7.exe"
download "$IS5" "innosetup-5.5.9-unicode.exe"
download "$IS5_2" "innosetup-5.6.1-unicode.exe"
download "$IS6" "innosetup-6.7.3.exe"
download "$IS7" "innosetup-7.0.2-x64.exe"

ls -la "$FIXTURES"
echo "完成。"
