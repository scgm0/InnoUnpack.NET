#requires -Version 5.1
# InnoUnpack.NET 性能对比脚本（Windows 版）：innoextract vs JIT vs AOT（Default/Speed/Size）。
# Linux/macOS 使用 compare.sh；本脚本在 Windows PowerShell 下原生运行，
# 在 pwsh 下亦可跨平台运行（自动选择 win-x64 / linux-x64 发布）。
#
# 用法：powershell -ExecutionPolicy Bypass -File compare.ps1 [-FixturesDir <dir>]
#       （默认使用仓库内 InnoUnpack.Tests\Fixtures）
# 前置：innoextract（加入 PATH；可用环境变量 INNOEXTRACT_BIN 指定自定义构建路径）、
#       dotnet SDK 10、clang/LLVM（AOT 发布需要，Windows 下为 VS Build Tools 或 LLVM）。
param([string]$FixturesDir = "")

$ErrorActionPreference = "Stop"

$INNOEXTRACT = if ($env:INNOEXTRACT_BIN) { $env:INNOEXTRACT_BIN } else { "innoextract" }
$BENCH_SRC = $PSScriptRoot
if (-not $FixturesDir) {
	# 默认：tools/bench → tools → 仓库根/InnoUnpack.Tests/Fixtures（两级上溯）
	$FixturesDir = Join-Path (Split-Path -Parent (Split-Path -Parent $BENCH_SRC)) "InnoUnpack.Tests" "Fixtures"
}

$RUNS = 7
$COMPARABLE = @("isetup-4.2.7.exe", "innosetup-5.5.9-unicode.exe", "innosetup-5.6.1-unicode.exe")
$ALL = @("isetup-4.2.7.exe", "innosetup-5.5.9-unicode.exe", "innosetup-5.6.1-unicode.exe", "innosetup-6.7.3.exe", "innosetup-7.0.2-x64.exe")

# OS 自适应：Windows 使用 win-x64 + .exe，其余平台 linux-x64
if ($env:OS -eq "Windows_NT") {
	$RID = "win-x64"
	$BENCH_EXE = "InnoUnpack.Bench.exe"
} else {
	$RID = "linux-x64"
	$BENCH_EXE = "InnoUnpack.Bench"
}

if (-not $env:INNOEXTRACT_BIN -and -not (Get-Command innoextract -ErrorAction SilentlyContinue)) {
	Write-Host "缺少 innoextract（请安装并加入 PATH，或用 INNOEXTRACT_BIN 指定路径）" -ForegroundColor Red
	exit 1
}

Write-Host "== 发布 AOT（三种 OptimizationPreference，RID=$RID） =="
foreach ($pref in @("Default", "Speed", "Size")) {
	Write-Host "  发布 AOT-$pref ..."
	dotnet publish $BENCH_SRC -c Release -r $RID -p:PublishAot=true -p:OptimizationPreference=$pref -o (Join-Path $BENCH_SRC "bin\aot-$pref") -v q | Out-Null
	if ($LASTEXITCODE -ne 0) {
		Write-Host "AOT 发布失败: $pref" -ForegroundColor Red
		exit 1
	}
}
dotnet build $BENCH_SRC -c Release -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "bench 构建失败" }
$BENCH_DLL = Join-Path $BENCH_SRC "bin\Release\net10.0\InnoUnpack.Bench.dll"

# 运行 bench 并提取毫秒数（fair 输出 "… <ms> ms"，fairloop 输出 "best <ms> ms"）
function Invoke-BenchMs([string]$Mode, [string]$Fixture, [string]$LinePattern) {
	$best = [long]::MaxValue
	for ($i = 0; $i -lt $RUNS; $i++) {
		if ($Mode -eq "jit") {
			$out = & dotnet $BENCH_DLL fair $FixturesDir $Fixture 2>$null | Out-String
		} else {
			$out = & (Join-Path $Mode $BENCH_EXE) fair $FixturesDir $Fixture 2>$null | Out-String
		}
		$m = [regex]::Match($out, $LinePattern)
		if ($m.Success) {
			$t = [long]$m.Groups[1].Value
			if ($t -lt $best) { $best = $t }
		}
	}
	return $best
}

# 进程内预热 + best-of-7（库热路径）
function Invoke-BenchWarmMs([string]$Mode, [string]$Fixture) {
	$best = [long]::MaxValue
	for ($i = 0; $i -lt $RUNS; $i++) {
		if ($Mode -eq "jit") {
			$out = & dotnet $BENCH_DLL fairloop $FixturesDir $Fixture 2>$null | Out-String
		} else {
			$out = & (Join-Path $Mode $BENCH_EXE) fairloop $FixturesDir $Fixture 2>$null | Out-String
		}
		$m = [regex]::Match($out, 'best (\d+) ms')
		if ($m.Success) {
			$t = [long]$m.Groups[1].Value
			if ($t -lt $best) { $best = $t }
		}
	}
	return $best
}

# innoextract 冷进程（整进程墙钟）
function Invoke-InnoextractMs([string]$Fixture) {
	$best = [long]::MaxValue
	for ($i = 0; $i -lt $RUNS; $i++) {
		$out = Join-Path ([System.IO.Path]::GetTempPath()) "innoextract-bench-$($Fixture -replace '\.exe$','')-$i"
		if (Test-Path $out) { Remove-Item $out -Recurse -Force }
		$sw = [System.Diagnostics.Stopwatch]::StartNew()
		& $INNOEXTRACT -e -s -T none -d $out (Join-Path $FixturesDir $Fixture) 2>$null | Out-Null
		$sw.Stop()
		Remove-Item $out -Recurse -Force
		if ($sw.ElapsedMilliseconds -lt $best) { $best = $sw.ElapsedMilliseconds }
	}
	return $best
}

# 可比文件集字节数
function Get-ComparableBytes([string]$Fixture) {
	$out = & dotnet $BENCH_DLL fair $FixturesDir $Fixture 2>$null | Out-String
	$m = [regex]::Match($out, 'bytes=([0-9,]+)')
	if (-not $m.Success) { throw "无法解析 bytes: $Fixture" }
	return [long]($m.Groups[1].Value -replace ',', '')
}

Write-Host ""
Write-Host "== 同等条件对比（同一文件集、无校验/时间戳、best-of-$RUNS） =="
"{0,-32} {1,10} {2,10} {3,10} {4,10} {5,10} {6,10}" -f "fixture", "bytes", "innoextract", "JIT-cold", "JIT-warm", "AOT-cold", "AOT-warm"
$AOT_DEFAULT = Join-Path $BENCH_SRC "bin\aot-Default"
foreach ($f in $COMPARABLE) {
	$bytes = Get-ComparableBytes $f
	$mib = [math]::Round($bytes / 1048576, 1)
	$ino = Invoke-InnoextractMs $f
	$jc = Invoke-BenchMs "jit" $f '(\d+) ms'
	$jw = Invoke-BenchWarmMs "jit" $f
	$ac = Invoke-BenchMs $AOT_DEFAULT $f '(\d+) ms'
	$aw = Invoke-BenchWarmMs $AOT_DEFAULT $f
	"{0,-32} {1,8} {2,10} {3,10} {4,10} {5,10} {6,10}" -f $f, "$mib MiB", "$ino ms", "$jc ms", "$jw ms", "$ac ms", "$aw ms"
}

Write-Host ""
Write-Host "== AOT 三种 OptimizationPreference（冷进程 fair，best-of-$RUNS） =="
"{0,-32} {1,10} {2,10} {3,10} {4,10}" -f "fixture", "Default", "Speed", "Size", "Speed-vs-Default"
$AOT_SPEED = Join-Path $BENCH_SRC "bin\aot-Speed"
$AOT_SIZE = Join-Path $BENCH_SRC "bin\aot-Size"
foreach ($f in $COMPARABLE) {
	$d = Invoke-BenchMs $AOT_DEFAULT $f '(\d+) ms'
	$s = Invoke-BenchMs $AOT_SPEED $f '(\d+) ms'
	$z = Invoke-BenchMs $AOT_SIZE $f '(\d+) ms'
	$ratio = [math]::Round($s / $d, 2)
	"{0,-32} {1,10} {2,10} {3,10} {4,10}" -f $f, "$d ms", "$s ms", "$z ms", "$ratio x"
}

Write-Host ""
Write-Host "== 库自身（公共 API 默认选项，含 SHA256 校验，best-of-3） =="
"{0,-32} {1,10} {2,10} {3,10}" -f "fixture", "bytes", "JIT", "AOT-Speed"
foreach ($f in $ALL) {
	$path = Join-Path $FixturesDir $f
	if (-not (Test-Path $path)) {
		"{0,-32} {1,10}" -f $f, "skip"
		continue
	}

	$fullOut = & dotnet $BENCH_DLL full $FixturesDir 2>$null | Out-String
	$mTotal = [regex]::Match($fullOut, "(?s)== $f.*?([0-9,]+) bytes")
	$mJit = [regex]::Match($fullOut, "(?s)== $f.*?full extract: (\d+) ms")
	$total = if ($mTotal.Success) { [long]($mTotal.Groups[1].Value -replace ',', '') } else { 0 }
	$mib = [math]::Round($total / 1048576, 1)
	$j = if ($mJit.Success) { $mJit.Groups[1].Value } else { "-" }

	$aotSpeedExe = Join-Path $AOT_SPEED $BENCH_EXE
	if (Test-Path $aotSpeedExe) {
		$aotOut = & $aotSpeedExe full $FixturesDir 2>$null | Out-String
		$mAot = [regex]::Match($aotOut, "(?s)== $f.*?full extract: (\d+) ms")
		$a = if ($mAot.Success) { $mAot.Groups[1].Value } else { "-" }
	} else {
		$a = "-"
	}

	"{0,-32} {1,8} {2,10} {3,10}" -f $f, "$mib MiB", "$j ms", "$a ms"
}

Write-Host ""
Write-Host "完成。结果可写入 README 的性能章节。"
