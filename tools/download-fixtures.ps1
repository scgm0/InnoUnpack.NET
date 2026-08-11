# 下载 Inno Setup 官方安装包作为测试样本（覆盖 4.x 至 7.x 各版本）。
# 文件保存到 InnoUnpack.Tests/Fixtures/（该目录不入库，见 .gitignore）。
# Windows 版（Linux/macOS 使用 download-fixtures.sh；本脚本亦可在 pwsh 下跨平台运行）。
$ErrorActionPreference = "Stop"

$FIXTURES = Join-Path (Split-Path -Parent $PSScriptRoot) "InnoUnpack.Tests" "Fixtures"
New-Item -ItemType Directory -Path $FIXTURES -Force | Out-Null

# 4.x（ANSI 老格式，zlib/lzma1 块）
$IS4 = "https://files.jrsoftware.org/is/4/isetup-4.2.7.exe"

# 5.x（ANSI / Unicode）
$IS5 = "https://files.jrsoftware.org/is/5/innosetup-5.5.9-unicode.exe"
$IS5_2 = "https://files.jrsoftware.org/is/5/innosetup-5.6.1-unicode.exe"

# 6.x / 7.x（GitHub Release 资产）
$IS6 = "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe"
$IS7 = "https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/innosetup-7.0.2-x64.exe"

function Download([string]$Url, [string]$Name) {
	$dest = Join-Path $FIXTURES $Name
	if (Test-Path $dest) {
		Write-Host "已存在 $Name"
		return
	}

	Write-Host "下载 $Name ..."
	# Windows 10+ / Linux 均自带 curl.exe；缺失时回退 Invoke-WebRequest
	if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
		& curl.exe -sL --retry 3 -o $dest $Url
		if ($LASTEXITCODE -ne 0) { throw "curl 下载失败: $Url" }
	} else {
		Invoke-WebRequest -Uri $Url -OutFile $dest -UseBasicParsing
	}
}

Download $IS4 "isetup-4.2.7.exe"
Download $IS5 "innosetup-5.5.9-unicode.exe"
Download $IS5_2 "innosetup-5.6.1-unicode.exe"
Download $IS6 "innosetup-6.7.3.exe"
Download $IS7 "innosetup-7.0.2-x64.exe"

Get-ChildItem $FIXTURES | Format-Table Name, Length -AutoSize
Write-Host "完成。"
