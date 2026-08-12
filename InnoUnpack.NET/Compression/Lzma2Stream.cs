/*
 * LZMA2 流式解码器（原始 LZMA2 流，Inno Setup 的块/chunk 即此格式）。
 *
 * 结构参考 xz 的 liblzma（lzma2_decoder.c）：
 * - 流以 1 字节属性（字典大小编码，由工厂读取并传入）开始；
 * - 随后为若干 chunk；
 * - 0x00：流结束；0x01/0x02：未压缩 chunk；0x80-0xFF：压缩 chunk。
 * 压缩 chunk 内嵌一段无结束标记的 LZMA1 码流（由 Lzma1Decoder 解码），
 * 码流尾部不足时以 0xFF 填充（与 innoextract 捆绑 liblzma 一致）。
 *
 * 两种输入模式：
 * - 内存模式（推荐）：构造时将整个压缩区域预取到池化缓冲，chunk 头与
 *   码流均直接寻址，省去逐 chunk 的流读取与输入缓冲搬移；
 * - 流模式：输入为不可预取（区域过大或不支持）时的回退路径。
 *
 * 并行解码（对应 7-Zip 的 Lzma2DecMt 架构）：
 * LZMA2 只在带字典复位（ctrl >= 0xE0）的 chunk 处允许独立分段——
 * 复位后匹配只能引用本段内已解出的数据，各段可完全并行。
 * 流含 >= 2 个复位点、输出总量在内存预算内且允许并行时，按复位点分段并发解码。
 * Inno Setup 生成的流仅流首有一个复位点，不会触发该路径。
 */

namespace InnoUnpack.NET.Compression;

using System.Buffers;
using System.Runtime.ExceptionServices;

/// <summary>
///     原始 LZMA2 流解码器（Inno Setup 的块数据格式）。
/// </summary>
sealed class Lzma2Stream : Stream {
	private readonly uint _dictSize;
	private readonly int _maxParallelism;

	// 流模式输入（持有所有权，Dispose 关闭）
	private Stream? _input;
	private readonly LimitedReadStream _limited;

	// 内存模式输入（池化，Dispose 归还）
	private byte[]? _mem;
	private bool _memRented;
	private int _memEnd;
	private int _memPos;

	// MT 装配结果（池化，Dispose 归还）
	private byte[]? _assembled;
	private bool _assembledRented;
	private int _assembledLen;

	private Lzma1Decoder? _decoder;
	private long _dictValid;
	private bool _disposed;
	private bool _eof;
	private byte _lastProp;
	private byte[] _pending = [];
	private bool _pendingRented;
	private int _pendingLen;
	private int _pendingPos;

	/// <summary>压缩区域预取上限（超过则回退流模式）。</summary>
	private const int PrefetchMaxBytes = 96 * 1024 * 1024;

	/// <summary>并行解码的装配缓冲上限（超过则回退串行内存解码）。</summary>
	private const int ParallelMaxOutput = 256 * 1024 * 1024;

	/// <summary>
	///     以 1 字节属性（字典大小编码，0-40）初始化。
	/// </summary>
	/// <param name="input">压缩数据输入流（区域长度已知时自动预取）。</param>
	/// <param name="prop">字典大小属性。</param>
	/// <param name="maxParallelism">允许的并行解码 worker 数（1 = 串行）。</param>
	/// <param name="prefetch">允许预取整个压缩区域（区域过大或输入流不支持长度时自动回退）。</param>
	public Lzma2Stream(Stream input, byte prop, int maxParallelism = 1, bool prefetch = true) {
		_maxParallelism = maxParallelism;
		_limited = new(input, 0);
		if (prop > 40) {
			throw new InnoFormatException($"无效的 LZMA2 属性 {prop}");
		}

		_dictSize = prop == 40 ? uint.MaxValue : (uint)(2 | prop & 1) << prop / 2 + 11;

		if (prefetch && TryGetLength(input, out var regionLength) && regionLength <= PrefetchMaxBytes) {
			// 工厂已在构造前读取 1 字节属性，剩余可读区域少 1 字节
			regionLength -= 1;
			if (regionLength > 0) {
				var buf = ArrayPool<byte>.Shared.Rent((int)regionLength);
				try {
					var total = 0;
					// 区域长度按块头给出，实际数据可能略短（末尾含填充）：读到 EOF 为止，
					// 解码器以 0xFF 填充容忍尾部缺口（与流模式一致）
					while (total < regionLength) {
						var n = input.Read(buf, total, (int)regionLength - total);
						if (n <= 0) {
							break;
						}

						total += n;
					}

					input.Dispose();
					InitMemory(buf, 0, total);
					return;
				} catch {
					ArrayPool<byte>.Shared.Return(buf);
					throw;
				}
			}
		}

		_input = input;
	}

	/// <summary>安全获取流长度（不支持 Length 的流（如解密器）返回 false）。</summary>
	static private bool TryGetLength(Stream input, out long length) {
		try {
			length = input.Length;
			return length > 0;
		} catch (NotSupportedException) {
			length = 0;
			return false;
		}
	}

	/// <summary>以内存中的完整压缩区域初始化（借用语义：调用方保持数组所有权，本流不归还）。</summary>
	internal Lzma2Stream(byte[] input, int offset, int length, byte prop, int maxParallelism = 1) {
		_maxParallelism = maxParallelism;
		_limited = new(Stream.Null, 0);
		if (prop > 40) {
			throw new InnoFormatException($"无效的 LZMA2 属性 {prop}");
		}

		_dictSize = prop == 40 ? uint.MaxValue : (uint)(2 | prop & 1) << prop / 2 + 11;
		InitMemory(input, offset, length, owned: false);
	}

	/// <summary>初始化内存模式：解析 chunk 结构，满足条件时启动分段并行解码。</summary>
	private void InitMemory(byte[] input, int offset, int length, bool owned = true) {
		_mem = input;
		_memRented = owned;
		_memPos = offset;
		_memEnd = offset + length;

		// 仅在允许并行时解析 chunk 表（串行路径保持惰性解析，行为与旧实现一致）
		if (_maxParallelism > 1 && length > 0 && ParseChunks(out var chunks)) {
			var segments = SplitSegments(chunks);
			if (segments.Count >= 2 && TotalOutput(segments) is var totalOut && totalOut > 0 && totalOut <= ParallelMaxOutput) {
				DecodeSegmentsParallel(segments, totalOut);
				// 并行完成后内存输入不再需要（各段解码器已消费），归还以释放内存
				ReturnMemInput();
			}
		}
	}

	/// <summary>解析 chunk 表（仅遍历头与长度，不读取码流）。损坏时抛 InnoFormatException。</summary>
	private bool ParseChunks(out List<ChunkInfo> chunks) {
		chunks = [];
		var p = _memPos;
		var end = _memEnd;
		while (p < end) {
			var ctrl = _mem![p++];
			if (ctrl == 0x00) {
				return true; // 流结束标记
			}

			if (ctrl is 0x01 or 0x02) {
				if (p + 2 > end) {
					throw new InnoFormatException("LZMA2 数据意外结束");
				}

				var usize = ((byte)_mem[p] << 8 | _mem[p + 1]) + 1;
				p += 2;
				if (p + usize > end) {
					throw new InnoFormatException("LZMA2 数据意外结束");
				}

				chunks.Add(new(ChunkKind.Uncompressed, p, 0, usize, 0));
				p += usize;
				continue;
			}

			if ((ctrl & 0x80) == 0) {
				throw new InnoFormatException($"无效的 LZMA2 chunk 控制字节 0x{ctrl:X2}");
			}

			if (p + 4 > end) {
				throw new InnoFormatException("LZMA2 数据意外结束");
			}

			var chunkUsize = ((ctrl & 0x1F) << 16 | (byte)_mem[p] << 8 | _mem[p + 1]) + 1;
			var csize = ((byte)_mem[p + 2] << 8 | _mem[p + 3]) + 1;
			p += 4;
			byte propsByte = 0;
			if (ctrl >= 0xC0) {
				if (p >= end) {
					throw new InnoFormatException("LZMA2 数据意外结束");
				}

				propsByte = _mem[p++];
				if (propsByte >= 9 * 5 * 5) {
					throw new InnoFormatException("无效的 LZMA2 lc/lp/pb 属性");
				}
			}

			if (p + csize > end) {
				// 末尾 chunk 的 csize 可能含填充而超出实际区域：截断到区域末端，
				// 解码器以 0xFF 填充容忍尾部缺口（与流模式一致）
				csize = end - p;
			}

			var kind = ctrl >= 0xE0
				? ChunkKind.CompressedDictReset
				: ctrl >= 0xC0
					? ChunkKind.CompressedProps
					: ctrl >= 0xA0
						? ChunkKind.CompressedStateReset
						: ChunkKind.Compressed;
			chunks.Add(new(kind, p, csize, chunkUsize, propsByte));
			p += csize;
		}

		// 无结束标记但输入耗尽（Inno Setup 的块数据可能截断）：视为合法
		return true;
	}

	/// <summary>按字典复位点分段：每个 0xE0+ chunk 开启新段（该 chunk 属于新段）。</summary>
	static private List<Segment> SplitSegments(List<ChunkInfo> chunks) {
		var segments = new List<Segment>();
		Segment? current = null;
		foreach (var chunk in chunks) {
			if (chunk.Kind == ChunkKind.CompressedDictReset) {
				current = new(chunk.Props);
				segments.Add(current);
			} else if (current is null) {
				// 流首非复位 chunk（容错）：并入首段，属性用 0
				current = new(0);
				segments.Add(current);
			}

			current!.Chunks.Add(chunk);
			current.TotalOut += chunk.Usize;
		}

		return segments;
	}

	static private int TotalOutput(List<Segment> segments) {
		var total = 0;
		foreach (var s in segments) {
			total += s.TotalOut;
		}

		return total;
	}

	/// <summary>分段并行解码：各段独立解码器并发工作，结果装配到 _assembled。</summary>
	private void DecodeSegmentsParallel(List<Segment> segments, int totalOut) {
		_assembled = ArrayPool<byte>.Shared.Rent(totalOut);
		_assembledRented = true;
		_assembledLen = totalOut;

		var segArr = segments.ToArray();
		var offset = 0;
		foreach (var seg in segArr) {
			seg.OutOffset = offset;
			offset += seg.TotalOut;
		}

		var workers = Math.Max(1, Math.Min(_maxParallelism, segArr.Length));
		try {
			Parallel.For(0, segArr.Length, new ParallelOptions { MaxDegreeOfParallelism = workers }, i => {
				var seg = segArr[i];
				var decoder = NewDecoder((byte)seg.Props);
				var outBuf = ArrayPool<byte>.Shared.Rent(seg.TotalOut);
				try {
					var pos = 0;
					foreach (var chunk in seg.Chunks) {
						if (chunk.Kind == ChunkKind.Uncompressed) {
							Array.Copy(_mem!, chunk.InOffset, outBuf, pos, chunk.Usize);
							decoder.WriteUncompressed(outBuf.AsSpan(pos, chunk.Usize));
						} else {
							decoder.SetInput(_mem!, chunk.InOffset, chunk.InOffset + chunk.Csize);
							decoder.ExternalCheckDicSize = (uint)Math.Min((long)pos, uint.MaxValue);
							var n = decoder.Decode(null!, outBuf.AsSpan(pos, chunk.Usize), true);
							if (n != chunk.Usize) {
								throw new InnoFormatException("LZMA2 解码输出不足（疑似损坏）");
							}
						}

						pos += chunk.Usize;
					}

					outBuf.AsSpan(0, seg.TotalOut).CopyTo(_assembled!.AsSpan(seg.OutOffset, seg.TotalOut));
				} finally {
					ArrayPool<byte>.Shared.Return(outBuf);
					decoder.Dispose();
				}
			});
		} catch (AggregateException ae) {
			// 解包并行 worker 的首个异常，保持调用方捕获 InnoFormatException 的语义
			var first = ae.Flatten().InnerExceptions.FirstOrDefault();
			if (first is not null) {
				ExceptionDispatchInfo.Capture(first).Throw();
			}

			throw;
		}
	}

	private void ReturnMemInput() {
		if (_memRented) {
			ArrayPool<byte>.Shared.Return(_mem!);
			_memRented = false;
			_mem = null;
		}
	}

	/// <summary>chunk 描述。</summary>
	private readonly record struct ChunkInfo(ChunkKind Kind, int InOffset, int Csize, int Usize, byte Props);

	/// <summary>chunk 类型（决定解码器状态转移）。</summary>
	private enum ChunkKind : byte {
		Uncompressed,
		Compressed,
		CompressedStateReset,
		CompressedProps,
		CompressedDictReset
	}

	/// <summary>独立解码段（从字典复位点开始到下一复位点前）。</summary>
	sealed private class Segment {
		public readonly int Props;
		public readonly List<ChunkInfo> Chunks = [];
		public int TotalOut;
		public int OutOffset;

		public Segment(int props) { Props = props; }
	}

	public override bool CanRead => true;
	public override bool CanSeek => false;
	public override bool CanWrite => false;
	public override long Length => throw new NotSupportedException();
	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count) { return Read(buffer.AsSpan(offset, count)); }

	public override int Read(Span<byte> buffer) {
		if (buffer.IsEmpty) {
			return 0;
		}

		// 并行装配结果：直接从中供数
		if (_assembled is not null) {
			var remaining = _assembledLen - _pendingPos;
			if (remaining <= 0) {
				return 0;
			}

			var count = Math.Min(remaining, buffer.Length);
			_assembled.AsSpan(_pendingPos, count).CopyTo(buffer);
			_pendingPos += count;
			return count;
		}

		var chunkCount = 0;
		while (_pendingPos == _pendingLen) {
			chunkCount++;
			if (_eof || !NextChunk()) {
				return 0;
			}

			if (_pendingLen == 0 && chunkCount > 100000) {
				throw new InnoFormatException("LZMA2 解码无进展（疑似损坏）");
			}
		}

		var n = Math.Min(_pendingLen - _pendingPos, buffer.Length);
		_pending.AsSpan(_pendingPos, n).CopyTo(buffer);
		_pendingPos += n;

		return n;
	}

	/// <summary>解码下一个 chunk 到 _pending。返回 false 表示流结束。</summary>
	private bool NextChunk() {
		if (_input is not null) {
			return NextChunkStream();
		}

		return NextChunkMemory();
	}

	/// <summary>流模式：从输入流读取 chunk 头并解码。</summary>
	private bool NextChunkStream() {
		var ctrl = _input!.ReadByte();
		if (ctrl < 0) {
			_eof = true;
			return false;
		}

		if (ctrl == 0x00) {
			_eof = true; // 流结束标记
			return false;
		}

		if (ctrl is 0x01 or 0x02) {
			// 未压缩 chunk：2 字节大小（+1）后直接数据（无校验字段）
			var usize = ReadUInt16() + 1;
			if (_eof) {
				throw new InnoFormatException("LZMA2 数据意外结束");
			}

			EnsurePendingCapacity(usize);
			ReadExact(_pending.AsSpan(0, usize));
			_pendingLen = usize;
			_pendingPos = 0;
			_dictValid += usize;
			// 未压缩数据写入 LZMA 窗口（匹配可引用）
			_decoder ??= NewDecoder(0);
			_decoder.WriteUncompressed(_pending.AsSpan(0, usize));
			return true;
		}

		if ((ctrl & 0x80) == 0) {
			throw new InnoFormatException($"无效的 LZMA2 chunk 控制字节 0x{ctrl:X2}");
		}

		// 压缩 chunk（0x80-0xFF）：
		//   usize = (ctrl & 0x1F) << 16 | 2 字节（大端）+ 1
		//   csize = 2 字节（大端）+ 1
		//   [1 字节 lc/lp/pb，若 ctrl >= 0xC0]
		//   LZMA1 码流（csize 字节）
		var chunkUsize = (ctrl & 0x1F) << 16 | ReadUInt16();
		var csize = ReadUInt16() + 1;
		if (_eof) {
			throw new InnoFormatException("LZMA2 数据意外结束");
		}

		chunkUsize += 1;

		var newProps = ctrl >= 0xC0;
		var dictReset = ctrl >= 0xE0;
		var stateReset = ctrl >= 0xA0 && !newProps;

		if (newProps) {
			_lastProp = (byte)ReadByteChecked();
			if (_lastProp >= 9 * 5 * 5) {
				throw new InnoFormatException("无效的 LZMA2 lc/lp/pb 属性");
			}
		}

		if (dictReset || _decoder == null) {
			// 0xE0+：新 props + 全部重置（含字典）
			if (_decoder is null) {
				_decoder = NewDecoder(_lastProp);
			} else {
				// 复用字典与概率数组，避免每次字典重置重新分配数 MB 缓冲
				_decoder.ResetWithProps(_lastProp);
				_decoder.ResetDictionary();
			}

			_dictValid = 0;
		} else if (newProps) {
			// 0xC0-0xDF：新 props + 状态重置（字典保留）
			_decoder.ResetWithProps(_lastProp);
		} else if (stateReset) {
			// 0xA0-0xBF：仅状态重置（概率模型 + range coder）
			_decoder.ResetState();
		} else {
			// 0x80-0x9F：继续——range coder 重新初始化（每个 chunk 有 5 字节初始化头），概率模型延续
			_decoder.ResetRangeOnly();
		}

		_decoder.ResetInput();
		_decoder.ExternalCheckDicSize = (uint)Math.Min(_dictValid, uint.MaxValue);

		// 复用输出缓冲（ArrayPool 租用，chunk 大小不同时按需扩容）
		EnsurePendingCapacity(chunkUsize);

		_limited.Reset(csize);
		var n = _decoder.Decode(_limited, _pending.AsSpan(0, chunkUsize), true);

		_pendingLen = n;
		_pendingPos = 0;
		_dictValid += n;
		return true;
	}

	/// <summary>内存模式：从预取缓冲解析 chunk 头并解码。</summary>
	private bool NextChunkMemory() {
		var mem = _mem!;
		if (_memPos >= _memEnd) {
			_eof = true;
			return false;
		}

		var ctrl = mem[_memPos++];
		if (ctrl == 0x00) {
			_eof = true; // 流结束标记
			return false;
		}

		if (ctrl is 0x01 or 0x02) {
			var usize = ReadMemUInt16(mem) + 1;
			if (_memPos + usize > _memEnd) {
				throw new InnoFormatException("LZMA2 数据意外结束");
			}

			EnsurePendingCapacity(usize);
			Array.Copy(mem, _memPos, _pending, 0, usize);
			_memPos += usize;
			_pendingLen = usize;
			_pendingPos = 0;
			_dictValid += usize;
			// 未压缩数据写入 LZMA 窗口（匹配可引用）
			_decoder ??= NewDecoder(0);
			_decoder.WriteUncompressed(_pending.AsSpan(0, usize));
			return true;
		}

		if ((ctrl & 0x80) == 0) {
			throw new InnoFormatException($"无效的 LZMA2 chunk 控制字节 0x{ctrl:X2}");
		}

		var chunkUsize = (ctrl & 0x1F) << 16 | ReadMemUInt16(mem);
		var csize = ReadMemUInt16(mem) + 1;
		chunkUsize += 1;

		var newProps = ctrl >= 0xC0;
		var dictReset = ctrl >= 0xE0;
		var stateReset = ctrl >= 0xA0 && !newProps;

		if (newProps) {
			if (_memPos >= _memEnd) {
				throw new InnoFormatException("LZMA2 数据意外结束");
			}

			_lastProp = mem[_memPos++];
			if (_lastProp >= 9 * 5 * 5) {
				throw new InnoFormatException("无效的 LZMA2 lc/lp/pb 属性");
			}
		}

		if (dictReset || _decoder == null) {
			if (_decoder is null) {
				_decoder = NewDecoder(_lastProp);
			} else {
				// 复用字典与概率数组，避免每次字典重置重新分配数 MB 缓冲
				_decoder.ResetWithProps(_lastProp);
				_decoder.ResetDictionary();
			}

			_dictValid = 0;
		} else if (newProps) {
			_decoder.ResetWithProps(_lastProp);
		} else if (stateReset) {
			_decoder.ResetState();
		} else {
			_decoder.ResetRangeOnly();
		}

		var dataStart = _memPos;
		var dataEnd = dataStart + csize;
		if (dataEnd > _memEnd) {
			// 末尾 chunk 的 csize 可能含填充而超出实际区域：截断到区域末端，
			// 解码器以 0xFF 填充容忍尾部缺口（与流模式一致）
			dataEnd = _memEnd;
		}

		_memPos = dataEnd;
		_decoder.SetInput(mem, dataStart, dataEnd);
		_decoder.ExternalCheckDicSize = (uint)Math.Min(_dictValid, uint.MaxValue);

		EnsurePendingCapacity(chunkUsize);
		var n = _decoder.Decode(null!, _pending.AsSpan(0, chunkUsize), true);

		_pendingLen = n;
		_pendingPos = 0;
		_dictValid += n;
		return true;
	}

	private int ReadMemUInt16(byte[] mem) {
		if (_memPos + 2 > _memEnd) {
			throw new InnoFormatException("LZMA2 数据意外结束");
		}

		var v = mem[_memPos] << 8 | mem[_memPos + 1];
		_memPos += 2;
		return v;
	}

	/// <summary>用 1 字节 lc/lp/pb + 流属性字典大小构造 LZMA1 解码器。</summary>
	private Lzma1Decoder NewDecoder(byte prop) {
		var props = new byte[5];
		props[0] = prop;
		props[1] = (byte)_dictSize;
		props[2] = (byte)(_dictSize >> 8);
		props[3] = (byte)(_dictSize >> 16);
		props[4] = (byte)(_dictSize >> 24);
		return new(props);
	}

	private int ReadByteChecked() {
		var b = _input!.ReadByte();
		if (b < 0) {
			_eof = true;
		}

		return b;
	}

	private int ReadUInt16() {
		var a = ReadByteChecked();
		var b = ReadByteChecked();
		return a << 8 | b;
	}

	private void ReadExact(Span<byte> buffer) {
		var offset = 0;
		while (offset < buffer.Length) {
			var n = _input!.Read(buffer[offset..]);
			if (n <= 0) {
				throw new InnoFormatException("LZMA2 数据意外结束");
			}

			offset += n;
		}
	}

	/// <summary>确保输出缓冲容量（ArrayPool 租用并按需扩容，避免每 chunk 分配）。</summary>
	private void EnsurePendingCapacity(int required) {
		if (_pending.Length >= required) {
			return;
		}

		if (_pendingRented) {
			ArrayPool<byte>.Shared.Return(_pending);
		}

		_pending = ArrayPool<byte>.Shared.Rent(required);
		_pendingRented = true;
	}

	public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

	public override void SetLength(long value) { throw new NotSupportedException(); }

	public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

	override protected void Dispose(bool disposing) {
		if (!_disposed) {
			_disposed = true;
			if (disposing) {
				_input?.Dispose();
				_decoder?.Dispose();
				if (_pendingRented) {
					ArrayPool<byte>.Shared.Return(_pending);
					_pendingRented = false;
					_pending = [];
				}

				ReturnMemInput();
				if (_assembledRented) {
					ArrayPool<byte>.Shared.Return(_assembled!);
					_assembledRented = false;
					_assembled = null;
				}
			}
		}

		base.Dispose(disposing);
	}

	/// <summary>限制读取范围（最多 size 字节）的输入包装。</summary>
	sealed private class LimitedReadStream(Stream input, int size) : Stream {
		private int _remaining = size;

		/// <summary>重置剩余读取量（chunk 间复用）。</summary>
		public void Reset(int size) { _remaining = size; }

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => _remaining;
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() { }

		public override int Read(byte[] buffer, int offset, int count) {
			if (_remaining <= 0) {
				return 0;
			}

			var n = input.Read(buffer, offset, Math.Min(count, _remaining));
			_remaining -= n;
			return n;
		}

		public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }

		public override void SetLength(long value) { throw new NotSupportedException(); }

		public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
	}
}
