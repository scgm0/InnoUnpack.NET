#!/usr/bin/env bash
# InnoUnpack.NET 性能对比脚本：innoextract vs JIT vs AOT（Default/Speed/Size）。
#
# 用法：bash compare.sh [fixtures-dir]（默认使用仓库内 InnoUnpack.Tests/Fixtures）
# 前置：innoextract、dotnet SDK 10、clang（AOT 发布需要）。
set -u

BENCH_SRC="$(cd "$(dirname "$0")" && pwd)"
FIXTURES="${1:-$BENCH_SRC/../../InnoUnpack.Tests/Fixtures}"
RUNS=7
COMPARABLE=(isetup-4.2.7.exe innosetup-5.5.9-unicode.exe innosetup-5.6.1-unicode.exe)
ALL=(isetup-4.2.7.exe innosetup-5.5.9-unicode.exe innosetup-5.6.1-unicode.exe innosetup-6.7.3.exe innosetup-7.0.2-x64.exe)

if ! command -v innoextract >/dev/null; then
	echo "缺少 innoextract" >&2
	exit 1
fi

echo "== 发布 AOT（三种 OptimizationPreference） =="
for pref in Default Speed Size; do
	dotnet publish "$BENCH_SRC" -c Release -r linux-x64 -p:PublishAot=true -p:OptimizationPreference="$pref" -o "$BENCH_SRC/bin/aot-$pref" -v q >/dev/null 2>&1 || {
		echo "AOT 发布失败: $pref" >&2
		exit 1
	}
done
dotnet build "$BENCH_SRC" -c Release -v q >/dev/null 2>&1
BENCH_DLL="$BENCH_SRC/bin/Release/net10.0/InnoUnpack.Bench.dll"

# 进程内 Stopwatch 计时（fair 模式输出 "… <ms> ms"）
bench_ms() { # $1=dotnet|aot-bin-dir, $2=fixture
	local mode="$1" fixture="$2" best=99999999 t i
	for ((i = 0; i < RUNS; i++)); do
		if [ "$mode" = jit ]; then
			t=$(dotnet "$BENCH_DLL" fair "$FIXTURES" "$fixture" 2>/dev/null | grep -oE '[0-9]+ ms' | grep -oE '^[0-9]+')
		else
			t=$("$mode/InnoUnpack.Bench" fair "$FIXTURES" "$fixture" 2>/dev/null | grep -oE '[0-9]+ ms' | grep -oE '^[0-9]+')
		fi
		[ -n "$t" ] && [ "$t" -lt "$best" ] && best=$t
	done
	echo "$best"
}

# 进程内预热 + best-of-7（库热路径）
bench_warm_ms() { # $1=dotnet|aot-bin-dir, $2=fixture
	local mode="$1" fixture="$2" best=99999999 t i
	for ((i = 0; i < RUNS; i++)); do
		if [ "$mode" = jit ]; then
			t=$(dotnet "$BENCH_DLL" fairloop "$FIXTURES" "$fixture" 2>/dev/null | grep -oE 'best [0-9]+ ms' | grep -oE '[0-9]+')
		else
			t=$("$mode/InnoUnpack.Bench" fairloop "$FIXTURES" "$fixture" 2>/dev/null | grep -oE 'best [0-9]+ ms' | grep -oE '[0-9]+')
		fi
		[ -n "$t" ] && [ "$t" -lt "$best" ] && best=$t
	done
	echo "$best"
}

# innoextract 冷进程（整进程墙钟）
inoextract_ms() {
	local fixture="$1" best=99999999 t i out
	for ((i = 0; i < RUNS; i++)); do
		out="/tmp/innoextract-bench-${fixture%.exe}-$i"
		rm -rf "$out"
		local start end
		start=$(date +%s%N)
		innoextract -e -s -T none -d "$out" "$FIXTURES/$fixture" >/dev/null 2>&1
		end=$(date +%s%N)
		rm -rf "$out"
		t=$(((end - start) / 1000000))
		[ "$t" -lt "$best" ] && best=$t
	done
	echo "$best"
}

bytes_of() { # 可比文件集字节数
	dotnet "$BENCH_DLL" fair "$FIXTURES" "$1" 2>/dev/null | grep -oE 'bytes=[0-9,]+' | grep -oE '[0-9,]+' | tr -d ','
}

echo
echo "== 同等条件对比（同一文件集、无校验/时间戳、best-of-$RUNS） =="
printf '%-32s %12s %10s %10s %10s %10s %10s\n' fixture bytes innoextract JIT-cold JIT-warm AOT-cold AOT-warm

declare -a ROWS=()
for f in "${COMPARABLE[@]}"; do
	bytes=$(bytes_of "$f")
	mib=$(python3 -c "print(f'{$bytes/1048576:.1f}')")
	ino=$(inoextract_ms "$f")
	jc=$(bench_ms jit "$f")
	jw=$(bench_warm_ms jit "$f")
	ac=$(bench_ms "$BENCH_SRC/bin/aot-Default" "$f")
	aw=$(bench_warm_ms "$BENCH_SRC/bin/aot-Default" "$f")
	printf '%-32s %8s %10s %10s %10s %10s %10s\n' "$f" "$mib MiB" "$ino ms" "$jc ms" "$jw ms" "$ac ms" "$aw ms"
	ROWS+=("$f|$mib|$ino|$jc|$jw|$ac|$aw")
done

echo
echo "== AOT 三种 OptimizationPreference（冷进程 fair，best-of-$RUNS） =="
printf '%-32s %10s %10s %10s %10s\n' fixture Default Speed Size Speed-vs-Default
for f in "${COMPARABLE[@]}"; do
	d=$(bench_ms "$BENCH_SRC/bin/aot-Default" "$f")
	s=$(bench_ms "$BENCH_SRC/bin/aot-Speed" "$f")
	z=$(bench_ms "$BENCH_SRC/bin/aot-Size" "$f")
	ratio=$(python3 -c "print(f'{$s/$d:.2f}')")
	printf '%-32s %10s %10s %10s %10s\n' "$f" "$d ms" "$s ms" "$z ms" "$ratio x"
done

echo
echo "== 库自身（公共 API 默认选项，含 SHA256 校验，best-of-3） =="
printf '%-32s %12s %10s %10s\n' fixture bytes JIT AOT-Speed
for f in "${ALL[@]}"; do
	path="$FIXTURES/$f"
	[ -f "$path" ] || { printf '%-32s %12s\n' "$f" skip; continue; }
	# JIT full
	j=$(dotnet "$BENCH_DLL" full "$FIXTURES" 2>/dev/null | grep -A0 "== $f" -A1 | grep -oE 'full extract: [0-9]+ ms' | grep -oE '[0-9]+')
	# AOT full（仅当二进制存在）
	if [ -x "$BENCH_SRC/bin/aot-Speed/InnoUnpack.Bench" ]; then
		a=$("$BENCH_SRC/bin/aot-Speed/InnoUnpack.Bench" full "$FIXTURES" 2>/dev/null | grep -A0 "== $f" -A1 | grep -oE 'full extract: [0-9]+ ms' | grep -oE '[0-9]+')
	else
		a="-"
	fi
	total=$(dotnet "$BENCH_DLL" full "$FIXTURES" 2>/dev/null | grep -A0 "== $f" | grep -oE '[0-9,]+ bytes' | grep -oE '^[0-9,]+' | tr -d ',')
	mib=$(python3 -c "print(f'{$total/1048576:.1f}')")
	printf '%-32s %8s %10s %10s\n' "$f" "$mib MiB" "$j ms" "$a ms"
done

echo
echo "完成。结果可写入 README 的性能章节。"
