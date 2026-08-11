namespace InnoUnpack.NET.Compression;

/// <summary>
///     bzip2 块解码器（块式，MSB-first 位序）。
///     依据公开的 bzip2 格式规范独立实现（参考 Julian Seward 的 bzip2 文档与
///     libbz2 的格式描述，非代码移植）。
///     管道：Huffman → RLE2 逆（RUNA/RUNB）→ MTF 逆 → BWT 逆 → RLE1 逆 → 块输出。
/// </summary>
sealed class BZip2Decoder(Stream input) {
	private const ulong BlockMagic = 0x314159265359; // "1AY&SY"
	private const ulong EndMagic = 0x177245385090; // "\x17rE8P\x90"
	private const int GroupSize = 50;
	private const int MaxGroups = 6;
	private const int MaxSelectors = 2 + 900000 / GroupSize;
	private const int MaxHuffmanBits = 20;
	private readonly BitReader _bits = new(input);

	private int _blockSize100K;
	private byte[] _bwt = [];
	private bool _headerRead;

	// 块内复用缓冲（按需扩容）
	private int[] _mtfIndex = [];
	private int[] _next = [];
	private int[] _selector = [];
	private byte[] _output = [];
	private bool _streamEnded;

	/// <summary>
	///     解码下一个块，返回块解压数据与有效长度；流结束返回 null。
	///     缓冲所有权仍属解码器（下次调用前有效），调用方须在下次解码前消费完毕。
	/// </summary>
	public (byte[] Buffer, int Length)? DecodeNextBlock() {
		if (_streamEnded) {
			return null;
		}

		ReadStreamHeader();

		var magic = _bits.ReadMagic();
		if (magic == EndMagic) {
			_streamEnded = true;
			return null;
		}

		if (magic != BlockMagic) {
			throw new InnoFormatException("bzip2 块头魔数无效");
		}


		return DecodeBlock();
	}

	private void ReadStreamHeader() {
		if (_headerRead) {
			return;
		}

		_headerRead = true;

		// 流头："BZh" + 块大小级别（'1'-'9'）
		Span<byte> header = stackalloc byte[4];
		var read = 0;
		while (read < 4) {
			var n = input.Read(header[read..]);
			if (n <= 0) {
				throw new InnoFormatException("bzip2 数据意外结束（缺少流头）");
			}

			read += n;
		}

		if (header[0] != (byte)'B' || header[1] != (byte)'Z' || header[2] != (byte)'h'
			|| header[3] is < (byte)'1' or > (byte)'9') {
			throw new InnoFormatException("无效的 bzip2 流头");
		}

		_blockSize100K = header[3] - '0';
	}

	private (byte[] Buffer, int Length) DecodeBlock() {
		var expectedCrc = _bits.ReadBits(32);
		_ = _bits.ReadBit(); // randomised（Inno Setup 不使用随机化块，忽略）
		var origPtr = (int)_bits.ReadBits(24);

		var maxBlock = _blockSize100K * 100000;

		// 使用集：16 位索引 + 每组的 16 位
		var nInUse = 0;
		var seqToUnseq = new byte[256];
		var inUse16 = _bits.ReadBits(16);
		for (var i = 0; i < 16; i++) {
			if ((inUse16 & 1u << 15 - i) == 0) {
				continue;
			}

			var inUse = _bits.ReadBits(16);
			for (var j = 0; j < 16; j++) {
				if ((inUse & 1u << 15 - j) != 0) {
					seqToUnseq[nInUse++] = (byte)(i * 16 + j);
				}
			}
		}

		if (nInUse == 0) {
			throw new InnoFormatException("bzip2 块使用集为空");
		}

		// 分组信息
		var nGroups = (int)_bits.ReadBits(3);
		if (nGroups is < 2 or > MaxGroups) {
			throw new InnoFormatException($"bzip2 组数无效：{nGroups}");
		}

		var nSelectors = (int)_bits.ReadBits(15);
		if (nSelectors < 1) {
			throw new InnoFormatException($"bzip2 选择器数无效：{nSelectors}");
		}

		// 超出上限的多余选择器永远不会被使用，忽略（与参考实现一致）
		if (nSelectors > MaxSelectors) {
			nSelectors = MaxSelectors;
		}

		// 选择器：一元编码（连续 1 后跟 0），再做 MTF 逆
		EnsureCapacity(ref _selector, nSelectors);
		var selector = _selector;
		var selectorList = new byte[MaxGroups];
		for (var i = 0; i < nGroups; i++) {
			selectorList[i] = (byte)i;
		}

		for (var i = 0; i < nSelectors; i++) {
			var j = 0;
			while (_bits.ReadBit()) {
				j++;
				if (j >= nGroups) {
					throw new InnoFormatException("bzip2 选择器值无效");
				}
			}

			var value = selectorList[j];
			// MTF：取第 j 个移到头部
			for (var k = j; k > 0; k--) {
				selectorList[k] = selectorList[k - 1];
			}

			selectorList[0] = value;
			selector[i] = value;
		}

		// 每组的 Huffman 表
		var minLen = new int[MaxGroups];
		var maxLen = new int[MaxGroups];
		var prefix = new int[MaxGroups][];
		var perm = new int[MaxGroups][];
		var startCode = new int[MaxGroups][];
		var endCode = new int[MaxGroups][];
		for (var g = 0; g < nGroups; g++) {
			BuildHuffmanTable(g, nInUse, minLen, maxLen, prefix, perm, startCode, endCode);
		}

		// 主数据：解码符号流（RUNA/RUNB 展开）到 MTF 索引数组
		EnsureCapacity(ref _mtfIndex, maxBlock);
		var nblock = DecodeSymbolStream(nInUse,
			nSelectors,
			selector,
			minLen,
			maxLen,
			prefix,
			perm,
			startCode,
			endCode,
			maxBlock);
		if (nblock == 0) {
			throw new InnoFormatException("bzip2 块为空");
		}

		// MTF 逆 → BWT 字符
		EnsureCapacity(ref _bwt, nblock);
		InverseMtf(_mtfIndex, nblock, nInUse, seqToUnseq, _bwt);

		// BWT 逆 + RLE1 展开
		EnsureCapacity(ref _next, nblock);
		EnsureCapacity(ref _output, maxBlock);
		var outputLen = InverseBwtAndRle1(_bwt, _next, nblock, origPtr, _output, maxBlock);

		// CRC 校验（bzip2 风格：非反射多项式 0x04C11DB7）
		var crc = BZip2Crc32.Compute(_output.AsSpan(0, outputLen));
		if (crc != expectedCrc) {
			throw new InnoFormatException("bzip2 块 CRC 校验失败");
		}

		// 直接返回底层输出缓冲与有效长度（所有权仍属解码器，杜绝每块 900KB 的复制与 LOH 分配）
		return (_output, outputLen);
	}

	static private void FlushRun(int[] target, ref int nblock, ref int run, out int runBits) {
		for (var i = 0; i < run; i++) {
			target[nblock++] = 0; // MTF 索引 0：重复前一字符
		}

		run = 0;
		runBits = 0;
	}

	/// <summary>主数据：解码符号流（RUNA/RUNB 展开）到 MTF 索引数组，返回 BWT 符号数。</summary>
	private int DecodeSymbolStream(
		int nInUse,
		int nSelectors,
		int[] selector,
		int[] minLen,
		int[] maxLen,
		int[][] prefix,
		int[][] perm,
		int[][] startCode,
		int[][] endCode,
		int maxBlock) {
		var nblock = 0;
		var groupNo = -1;
		var groupPos = 0;
		var currentGroup = 0;
		var run = 0;
		var runBits = 0;
		while (true) {
			if (groupPos == 0) {
				groupNo++;
				if (groupNo >= nSelectors) {
					throw new InnoFormatException("bzip2 块数据截断（选择器耗尽）");
				}

				currentGroup = selector[groupNo];
				groupPos = GroupSize;
			}

			groupPos--;

			// Huffman 解码一个符号
			var n = minLen[currentGroup];
			uint code = 0;
			while (true) {
				code = code << 1 | (_bits.ReadBit() ? 1u : 0u);
				if (code <= (uint)endCode[currentGroup][n]) {
					break;
				}

				n++;
				if (n > maxLen[currentGroup]) {
					throw new InnoFormatException("bzip2 Huffman 码无效");
				}
			}

			var symbol = perm[currentGroup][prefix[currentGroup][n] + (int)(code - startCode[currentGroup][n])];

			if (symbol < 2) {
				// RUNA（0）/ RUNB（1）累积
				run += symbol == 0 ? 1 << runBits : 2 << runBits;
				runBits++;
				// 与参考一致：run 值上限 2MB（约 21 位）
				if (runBits > 20) {
					throw new InnoFormatException("bzip2 运行长度溢出");
				}
			} else if (symbol == nInUse + 1) {
				// EOB（符号 = nInUse + 1）：块结束
				if (run > 0) {
					if (nblock + run > maxBlock) {
						throw new InnoFormatException("bzip2 块符号数溢出");
					}

					FlushRun(_mtfIndex, ref nblock, ref run, out runBits);
				}

				break;
			} else {
				// 数据符号 = MTF 位置 + 1
				symbol -= 1;
				if (run > 0) {
					if (nblock + run > maxBlock) {
						throw new InnoFormatException("bzip2 块符号数溢出");
					}

					FlushRun(_mtfIndex, ref nblock, ref run, out runBits);
				}

				if (nblock >= maxBlock) {
					throw new InnoFormatException("bzip2 块符号数溢出");
				}

				_mtfIndex[nblock++] = symbol;
			}
		}

		return nblock;
	}

	/// <summary>MTF 逆变换：MTF 索引序列还原为字符序列（使用集映射）。</summary>
	static private void InverseMtf(int[] mtfIndex, int nblock, int nInUse, ReadOnlySpan<byte> seqToUnseq, Span<byte> bwt) {
		var mtfList = new byte[256];
		for (var i = 0; i < 256; i++) {
			mtfList[i] = (byte)i;
		}

		for (var i = 0; i < nblock; i++) {
			var v = mtfIndex[i];
			if (v >= nInUse) {
				throw new InnoFormatException("bzip2 MTF 索引无效");
			}

			var value = mtfList[v];
			bwt[i] = seqToUnseq[value];
			// 移到头部（小型移动，长度 ≤ nInUse）
			for (var k = v; k > 0; k--) {
				mtfList[k] = mtfList[k - 1];
			}

			mtfList[0] = value;
		}
	}

	/// <summary>BWT 逆变换（计数排序 + LF 映射）与 RLE1 展开，输出块解压数据。</summary>
	static private int InverseBwtAndRle1(
		ReadOnlySpan<byte> bwt,
		Span<int> next,
		int nblock,
		int origPtr,
		Span<byte> output,
		int maxBlock) {
		var cftab = new int[257];
		for (var i = 0; i < nblock; i++) {
			cftab[bwt[i] + 1]++;
		}

		for (var i = 1; i <= 256; i++) {
			cftab[i] += cftab[i - 1];
		}

		for (var i = 0; i < nblock; i++) {
			int c = bwt[i];
			next[cftab[c]++] = i;
		}

		var tPos = next[origPtr];
		var outputLen = 0;
		for (var i = 0; i < nblock; i++) {
			var c = bwt[tPos];
			// RLE1：4 个连续相同字符后跟 1 字节计数（额外重复次数）
			if (i + 4 < nblock
				&& bwt[next[tPos]] == c
				&& bwt[next[next[tPos]]] == c
				&& bwt[next[next[next[tPos]]]] == c) {
				var count = bwt[next[next[next[next[tPos]]]]];
				for (var k = 0; k < 4 + count; k++) {
					output[outputLen++] = c;
				}

				tPos = next[next[next[next[next[tPos]]]]];
				i += 4;
			} else {
				output[outputLen++] = c;
				tPos = next[tPos];
			}

			if (outputLen > maxBlock) {
				throw new InnoFormatException("bzip2 块输出溢出");
			}
		}

		return outputLen;
	}

	private void BuildHuffmanTable(
		int group,
		int nInUse,
		int[] minLen,
		int[] maxLen,
		int[][] prefix,
		int[][] perm,
		int[][] startCode,
		int[][] endCode) {
		// Huffman 符号集 = nInUse 个数据符号 + RUNA/RUNB/EOB（共 nInUse + 2 个）
		// 码长增量编码：首个为 5 位绝对值，其后每位 0=不变、1 后跟方向位（0=+1，1=-1）
		var symbolCount = nInUse + 2;
		var lengths = new int[symbolCount];
		int min = MaxHuffmanBits + 1, max = 0;
		var current = (int)_bits.ReadBits(5);
		for (var i = 0; i < symbolCount; i++) {
			// 先读增量位（0=不变；1 后跟方向位：0=+1，1=-1），再存储
			while (true) {
				if (current is < 1 or > MaxHuffmanBits) {
					throw new InnoFormatException("bzip2 Huffman 码长无效");
				}

				if (!_bits.ReadBit()) {
					break;
				}

				current += _bits.ReadBit() ? -1 : 1;
			}

			lengths[i] = current;
			if (current < min) {
				min = current;
			}

			if (current > max) {
				max = current;
			}
		}

		var count = new int[max + 2];
		for (var i = 0; i < symbolCount; i++) {
			count[lengths[i]]++;
		}

		var pref = new int[max + 2];
		var sum = 0;
		for (var l = 1; l <= max + 1; l++) {
			pref[l] = sum;
			sum += count[l];
		}

		var permTable = new int[symbolCount];
		var pp = 0;
		for (var l = 1; l <= max; l++) {
			for (var j = 0; j < symbolCount; j++) {
				if (lengths[j] == l) {
					permTable[pp++] = j;
				}
			}
		}

		var start = new int[max + 2];
		var end = new int[max + 2];
		var vec = 0;
		for (var l = 1; l <= max; l++) {
			start[l] = vec;
			vec += count[l];
			end[l] = vec - 1;
			vec <<= 1;
		}

		minLen[group] = min;
		maxLen[group] = max;
		prefix[group] = pref;
		perm[group] = permTable;
		startCode[group] = start;
		endCode[group] = end;
	}

	static private void EnsureCapacity(ref byte[] buffer, int required) {
		if (buffer.Length < required) {
			buffer = new byte[required];
		}
	}

	static private void EnsureCapacity(ref int[] buffer, int required) {
		if (buffer.Length < required) {
			buffer = new int[required];
		}
	}

	/// <summary>MSB-first 位读取器（bzip2 位序，块缓冲避免逐字节 ReadByte）。</summary>
	sealed private class BitReader(Stream stream) {
		private const int BufferSize = 8192;
		private readonly byte[] _data = new byte[BufferSize];
		private int _bitCount;
		private uint _buffer;
		private int _pos;
		private int _len;

		public bool ReadBit() {
			if (_bitCount == 0) {
				var b = NextByte();
				if (b < 0) {
					throw new InnoFormatException("bzip2 数据意外结束");
				}

				_buffer = (uint)b;
				_bitCount = 8;
			}

			_bitCount--;
			return (_buffer >> _bitCount & 1) != 0;
		}

		/// <summary>从缓冲读取下一个字节；流结束返回 -1。</summary>
		private int NextByte() {
			if (_pos == _len) {
				_len = stream.Read(_data);
				_pos = 0;
				if (_len <= 0) {
					return -1;
				}
			}

			return _data[_pos++];
		}

		public uint ReadBits(int count) {
			uint value = 0;
			for (var i = 0; i < count; i++) {
				value = value << 1 | (ReadBit() ? 1u : 0u);
			}

			return value;
		}

		/// <summary>连续位流读取 6 字节魔数（块间不字节对齐）。</summary>
		public ulong ReadMagic() {
			ulong value = 0;
			for (var i = 0; i < 6; i++) {
				value = value << 8 | ReadBits(8);
			}

			return value;
		}
	}

	/// <summary>bzip2 CRC-32（非反射，多项式 0x04C11DB7，初始与输出均异或 0xFFFFFFFF）。</summary>
	static private class BZip2Crc32 {
		static private readonly uint[] Table = BuildTable();

		static private uint[] BuildTable() {
			var table = new uint[256];
			for (uint i = 0; i < 256; i++) {
				var c = i << 24;
				for (var k = 0; k < 8; k++) {
					c = (c & 0x80000000) != 0 ? c << 1 ^ 0x04C11DB7 : c << 1;
				}

				table[i] = c;
			}

			return table;
		}

		public static uint Compute(ReadOnlySpan<byte> data) {
			var crc = 0xFFFFFFFF;
			foreach (var b in data) {
				crc = crc << 8 ^ Table[(crc >> 24 ^ b) & 0xFF];
			}

			return crc ^ 0xFFFFFFFF;
		}
	}
}