/*
 * LZMA1 流式解码器。
 *
 * 移植自 LZMA SDK (https://www.7-zip.org/sdk.html) 的 LzmaDec.c / LzmaDec.h
 * （作者 Igor Pavlov，Public Domain 许可）。
 * 支持"无结束标记、解压大小未知"的原始 LZMA1 流（Inno Setup 的块与 chunk 数据
 * 即为此格式）：输入耗尽时停止解码。
 */


namespace InnoUnpack.NET.Compression;

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
///     流式 LZMA1 解码器（无结束标记、输出大小未知）。
///     输入流以 5 字节属性头开始（lc/lp/pb + 字典大小），随后为 LZMA 码流。
/// </summary>
sealed class Lzma1Decoder {
	// ---------- 常量（与 LzmaDec.h/LzmaDec.c 一致） ----------
	private const uint KTopValue = 1u << 24;
	private const uint KBitModelTotal = 1u << 11;
	private const int KNumMoveBits = 5;
	private const int KNumPosStatesMax = 1 << 4;
	private const int KLenNumLowBits = 3;
	private const int KLenNumLowSymbols = 1 << KLenNumLowBits;
	private const int KLenNumHighBits = 8;
	private const int KLenNumHighSymbols = 1 << KLenNumHighBits;
	private const int KNumStates = 12;
	private const int KNumLitStates = 7;
	private const int KStartPosModelIndex = 4;
	private const int KEndPosModelIndex = 14;
	private const int KNumFullDistances = 1 << (KEndPosModelIndex >> 1);
	private const int KNumPosSlotBits = 6;
	private const int KNumLenToPosStates = 4;
	private const int KNumAlignBits = 4;
	private const int KAlignTableSize = 1 << KNumAlignBits;
	private const int KMatchMinLen = 2;
	private const int KMatchSpecLenStart = KMatchMinLen + KLenNumLowSymbols * 2 + KLenNumHighSymbols;
	private const int KMatchSpecLenErrorData = 1 << 9;
	private const int KStartOffset = 1664;
	private const int LzmaLitSize = 0x300;
	private const uint LzmaDicMin = 1u << 12;
	private const int RequiredInputMax = 20;
	private const int RcInitSize = 5;

	// probs 相对偏移（GET_PROBS = probs + kStartOffset）
	private const int SpecPos = -KStartOffset;
	private const int IsRep0Long = SpecPos + KNumFullDistances;
	private const int RepLenCoder = IsRep0Long + (16 << 4);
	private const int LenCoder = RepLenCoder + 2 * (KNumPosStatesMax << KLenNumLowBits) + KLenNumHighSymbols;
	private const int IsMatch = LenCoder + 2 * (KNumPosStatesMax << KLenNumLowBits) + KLenNumHighSymbols;
	private const int Align = IsMatch + (16 << 4);
	private const int IsRep = Align + KAlignTableSize;
	private const int IsRepG0 = IsRep + KNumStates;
	private const int IsRepG1 = IsRepG0 + KNumStates;
	private const int IsRepG2 = IsRepG1 + KNumStates;
	private const int PosSlot = IsRepG2 + KNumStates;
	private const int Literal = PosSlot + (KNumLenToPosStates << KNumPosSlotBits);
	private const int NumBaseProbs = Literal + KStartOffset;

	private const int LenLow = 0;
	private const int LenHigh = LenLow + 2 * (KNumPosStatesMax << KLenNumLowBits);
	private const int LenChoice = LenLow;
	private const int LenChoice2 = LenLow + (1 << KLenNumLowBits);

	private const int DummyInputEof = -1;

	// 大缓冲由 ArrayPool 租用并在 Dispose 归还（池预热后整包提取期间无 GC 收集）
	private readonly byte[] _dic;
	private readonly int _dicBufSize;

	private readonly byte[] _inBuf;
	private bool _dicRented;
	private bool _probsRented;
	private bool _inBufRented;

	private byte _lc;
	private byte _lp;
	private int _lpMask;
	private byte _pb;
	private uint _pbMask;

	// ---------- 解码器状态 ----------
	private ushort[] _probs;
	private readonly uint _propDicSize;
	private uint _checkDicSize;
	private uint _code;

	private int _dicPos;
	private int _inLen;
	private int _inPos;
	private bool _inputEnded;
	private uint _processedPos;
	private uint _range;
	private int _remainLen = KMatchSpecLenStart + 2;
	private uint _rep0, _rep1, _rep2, _rep3;
	private int _state;

	/// <summary>以 5 字节属性头（lc/lp/pb + 字典大小）初始化。</summary>
	public Lzma1Decoder(byte[] props) {
		if (props.Length < 5) {
			throw new ArgumentException("LZMA 属性至少需要 5 字节", nameof(props));
		}

		var dicSize = props[1] | (uint)props[2] << 8 | (uint)props[3] << 16 | (uint)props[4] << 24;
		if (dicSize < LzmaDicMin) {
			dicSize = LzmaDicMin;
		}

		var d = props[0];
		if (d >= 9 * 5 * 5) {
			throw new InnoFormatException("无效的 LZMA 属性");
		}

		_lc = (byte)(d % 9);
		d /= 9;
		_pb = (byte)(d / 5);
		_lp = (byte)(d % 5);
		_pbMask = (1u << _pb) - 1;
		_lpMask = (0x100 << _lp) - (0x100 >> _lc);
		_propDicSize = dicSize;

		var mask = dicSize switch { >= 1u << 30 => (1u << 22) - 1, >= 1u << 22 => (1u << 20) - 1, _ => (1u << 12) - 1 };

		_dicBufSize = (int)((ulong)dicSize + mask & ~mask);
		if ((ulong)_dicBufSize < dicSize) {
			_dicBufSize = (int)dicSize;
		}

		_dic = ArrayPool<byte>.Shared.Rent(_dicBufSize);
		_dicRented = true;

		var numProbs = NumBaseProbs + (LzmaLitSize << _lc + _lp);
		_probs = ArrayPool<ushort>.Shared.Rent(numProbs);
		_probsRented = true;
		Array.Fill(_probs, (ushort)(KBitModelTotal >> 1));

		_inBuf = ArrayPool<byte>.Shared.Rent(256 * 1024);
		_inBufRented = true;
	}

	/// <summary>归还 ArrayPool 租用的字典/概率/输入缓冲（流销毁时调用，幂等）。</summary>
	public void Dispose() {
		if (_dicRented) {
			ArrayPool<byte>.Shared.Return(_dic);
			_dicRented = false;
		}

		if (_probsRented) {
			ArrayPool<ushort>.Shared.Return(_probs);
			_probsRented = false;
		}

		if (_inBufRented) {
			ArrayPool<byte>.Shared.Return(_inBuf);
			_inBufRented = false;
		}
	}

	/// <summary>外部设定的字典有效数据量（LZMA2 模式：含未压缩 chunk 写入的数据，用于距离合法性检查）。</summary>
	internal uint ExternalCheckDicSize { get; set; }

	private void RefillInput(Stream input) {
		if (_inputEnded) {
			return;
		}

		var remaining = _inLen - _inPos;
		if (remaining > 0) {
			Buffer.BlockCopy(_inBuf, _inPos, _inBuf, 0, remaining);
		}

		var n = input.Read(_inBuf, remaining, _inBuf.Length - remaining);
		_inLen = remaining + n;
		_inPos = 0;
		if (n <= 0) {
			_inputEnded = true;
		}
	}

	/// <summary>
	///     范围解码器标准化。输入越过真实缓冲边界（<paramref name="inLen" />）时：
	///     截断模式（bufLimit 为 int.MaxValue）用 0xFF 填充，否则抛出 <see cref="InputEofException" />。
	/// </summary>
	static private void Normalize(ref uint range, ref uint code, ref int buf, int bufLimit, ref byte inBuf, int inLen) {
		if (range < KTopValue) {
			if (buf >= inLen) {
				if (bufLimit == int.MaxValue) {
					// 截断模式：0xFF 填充（不消费输入）。range 同样左移，避免 range 停滞
					range <<= 8;
					code = code << 8 | 0xFF;
					return;
				}

				throw new InputEofException();
			}

			range <<= 8;
			code = code << 8 | Unsafe.Add(ref inBuf, buf++);
		}
	}

	/// <summary>
	///     解码一位并更新概率模型（对应 IF_BIT_0 / UPDATE_0 / UPDATE_1）。
	///     bound 分支对真实数据预测良好（概率模型收敛后方向稳定），实测优于算术选择（cmov）形式。
	/// </summary>
	static private int DecodeBit(
		ref ushort prob,
		ref uint range,
		ref uint code,
		ref int buf,
		int bufLimit,
		ref byte inBuf,
		int inLen) {
		uint ttt = prob;
		Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
		var bound = (range >> 11) * ttt;
		if (code < bound) {
			range = bound;
			prob = (ushort)(ttt + (KBitModelTotal - ttt >> KNumMoveBits));
			return 0;
		}

		range -= bound;
		code -= bound;
		prob = (ushort)(ttt - (ttt >> KNumMoveBits));
		return 1;
	}

	/// <summary>仅重新初始化 range coder（LZMA2 连续 chunk 之间），不重置概率模型与状态。</summary>
	internal void ResetRangeOnly() { _remainLen = KMatchSpecLenStart + 1; }

	/// <summary>
	///     重置解码状态（概率模型、匹配位置、range coder），保留字典内容（LZMA2 state reset）。
	///     概率模型重置使用向量化的 <see cref="System.Array.Fill{T}(T[], T)" />。
	/// </summary>
	internal void ResetState() {
		Array.Fill(_probs, (ushort)(KBitModelTotal >> 1));
		_rep0 = _rep1 = _rep2 = _rep3 = 1;
		_state = 0;
		_remainLen = KMatchSpecLenStart + 1;
	}

	/// <summary>
	///     以新的 lc/lp/pb 属性重新初始化解码器（LZMA2 0xC0+ chunk）：
	///     复用字典与概率数组（尺寸不足时才重新分配），重置概率模型与解码状态，保留字典内容。
	/// </summary>
	internal void ResetWithProps(byte prop) {
		var d = prop;
		if (d >= 9 * 5 * 5) {
			throw new InnoFormatException("无效的 LZMA 属性");
		}

		_lc = (byte)(d % 9);
		d /= 9;
		_pb = (byte)(d / 5);
		_lp = (byte)(d % 5);
		_pbMask = (1u << _pb) - 1;
		_lpMask = (0x100 << _lp) - (0x100 >> _lc);

		var numProbs = NumBaseProbs + (LzmaLitSize << _lc + _lp);
		if (_probs.Length < numProbs) {
			// 池化缓冲尺寸不足：换租（仅在 lc/lp 增大时发生）
			if (_probsRented) {
				ArrayPool<ushort>.Shared.Return(_probs);
			}

			_probs = ArrayPool<ushort>.Shared.Rent(numProbs);
			_probsRented = true;
		}

		Array.Fill(_probs, (ushort)(KBitModelTotal >> 1));
		_rep0 = _rep1 = _rep2 = _rep3 = 1;
		_state = 0;
		_remainLen = KMatchSpecLenStart + 1;
	}

	/// <summary>
	///     重置字典有效数据（LZMA2 0xE0+ chunk：字典复位）。
	///     仅重置位置与有效量，字典缓冲内容不清理——距离检查保证复位后匹配
	///     只能引用复位后写入的字节。
	/// </summary>
	internal void ResetDictionary() {
		_dicPos = 0;
		_processedPos = 0;
		_checkDicSize = 0;
	}

	/// <summary>清空输入缓冲（LZMA2 每个 chunk 是独立码流，必须丢弃上一个 chunk 的残留输入）。</summary>
	internal void ResetInput() {
		_inLen = 0;
		_inPos = 0;
		_inputEnded = false;
	}

	/// <summary>
	///     将未压缩 chunk 的数据写入字典（LZMA2：这些数据构成 LZMA 窗口的一部分，匹配可以引用），
	///     并推进逻辑位置（processedPos）。输出由调用方另行提供，此处只更新解码器窗口。
	/// </summary>
	internal void WriteUncompressed(ReadOnlySpan<byte> data) {
		foreach (var b in data) {
			_dic[_dicPos++] = b;
			if (_dicPos == _dicBufSize) {
				_dicPos = 0;
			}
		}

		if (_checkDicSize == 0 && _propDicSize - _processedPos <= (uint)data.Length) {
			_checkDicSize = _propDicSize;
		}

		_processedPos += (uint)data.Length;
	}

	/// <summary>
	///     从输入流解码，输出到 <paramref name="output" />（最多 output.Length 字节）。
	///     输入耗尽时：<paramref name="allowTruncated" /> 为 true 时用 0xFF 填充继续解码
	///     （Inno Setup 块尾部的宽容模式），否则停止。
	/// </summary>
	public int Decode(Stream input, Span<byte> output, bool allowTruncated) {
		if (output.IsEmpty) {
			return 0;
		}

		var total = 0;
		while (total < output.Length) {
			// ---- 初始化 range coder（需要 5 字节输入） ----
			if (_remainLen > KMatchSpecLenStart) {
				if (_remainLen > KMatchSpecLenStart + 2) {
					break; // 内部错误
				}

				while (_inLen - _inPos < RcInitSize && !_inputEnded) {
					RefillInput(input);
				}

				if (_inLen - _inPos < RcInitSize) {
					break; // 输入不足
				}

				_code = (uint)_inBuf[_inPos + 1] << 24 | (uint)_inBuf[_inPos + 2] << 16
					| (uint)_inBuf[_inPos + 3] << 8 | _inBuf[_inPos + 4];
				_inPos += RcInitSize;

				if (_checkDicSize == 0 && _processedPos == 0 && _code >= 0xC0000000 - 0x400) {
					throw new InnoFormatException("LZMA 数据错误");
				}

				_range = 0xFFFFFFFF;

				if (_remainLen > KMatchSpecLenStart + 1) {
					Array.Fill(_probs, (ushort)(KBitModelTotal >> 1));
					_rep0 = _rep1 = _rep2 = _rep3 = 1;
					_state = 0;
				}

				_remainLen = 0;
			}

			if (_remainLen == KMatchSpecLenStart) {
				break; // 结束标记
			}

			// ---- 写入匹配剩余字节（before 需在写入前记录，保证匹配字节计入输出） ----
			var before = _dicPos;
			if (_remainLen > 0) {
				var len = _remainLen;
				var rem = _dicBufSize - _dicPos;
				if (rem < len) {
					len = rem;
				}

				if (len > 0) {
					if (_checkDicSize == 0 && _propDicSize - _processedPos <= (uint)len) {
						_checkDicSize = _propDicSize;
					}

					_processedPos += (uint)len;
					_remainLen -= len;
					ref var dic = ref MemoryMarshal.GetArrayDataReference(_dic);
					for (var i = 0; i < len; i++) {
						Unsafe.Add(ref dic, _dicPos) =
							Unsafe.Add(ref dic, _dicPos - (int)_rep0 + (_dicPos < _rep0 ? _dicBufSize : 0));
						_dicPos++;
					}
				}
			}

			// ---- 解码新符号（字典目标基于匹配写入前的 before，避免匹配字节占用输出容量） ----
			var avail = output.Length - total;
			int target;
			if (avail >= _dicBufSize - before) {
				target = _dicBufSize;
			} else {
				target = before + avail;
			}

			var ok = DecodeToDic(input, target, allowTruncated);
			var produced = _dicPos - before;
			if (produced > 0) {
				var n = Math.Min(produced, avail);
				_dic.AsSpan(before, n).CopyTo(output[total..]);
				total += n;
				if (_dicPos == _dicBufSize) {
					// 字典写满：逻辑位置回绕（环形缓冲，匹配引用仍有效）
					_dicPos = 0;
				}

				continue;
			}

			if (!ok) {
				break; // 输入不足（非截断模式）
			}

			if (_dicPos == _dicBufSize) {
				_dicPos = 0;
				continue; // 回绕后继续（整圈解码才可能到达）
			}

			break; // 没有新输出
		}

		return total;
	}

	private bool DecodeToDic(Stream input, int limit, bool allowTruncated) {
		// 保证输入缓冲中有 RequiredInputMax 的余量（解码器可能超出读取）
		while (_inLen - _inPos < RequiredInputMax && !_inputEnded) {
			RefillInput(input);
		}

		var startPos = _inPos;
		var dicStart = _dicPos;

		var expectedDummy = -1;
		int bufLimit;
		if (_inLen - _inPos < RequiredInputMax) {
			if (allowTruncated) {
				// 截断模式：用 0xFF 填充（Normalize 内处理），不设输入上限
				bufLimit = int.MaxValue;
			} else {
				// 输入不足：先预览解码一个符号，确认输入足以完成该符号
				expectedDummy = TryDummy();
				if (expectedDummy < 0) {
					return false;
				}

				bufLimit = _inLen; // 只解码一个符号，允许使用全部剩余输入
			}
		} else {
			bufLimit = _inPos + (_inLen - _inPos - RequiredInputMax);
		}

		try {
			DecodeReal(limit, bufLimit);
		} catch (InputEofException) {
			// 输入不足导致符号未完成：仅丢弃该符号（DecodeReal 已回滚 _dicPos，与 liblzma 的 LZMA_BUF_ERROR 行为一致）
			_inPos = startPos;
			return false;
		} catch (InnoFormatException) when (expectedDummy >= 0) {
			// 输入不足路径下预览与实际解码不一致（如位不完整导致的错误匹配距离）：
			// 丢弃未完成符号（DecodeReal 已回滚 _dicPos），与 liblzma 在截断码流末尾的行为一致
			_inPos = startPos;
			return false;
		}

		if (expectedDummy >= 0) {
			var processed = _inPos - startPos;
			if (processed != expectedDummy) {
				// 输入不足以完成符号（实际消耗与预览不一致），回滚
				_inPos = startPos;
				_dicPos = dicStart;
				return false;
			}
		}

		return true;
	}

	/// <summary>
	///     只读预览解码（不修改解码器状态、不写字典），判断剩余输入能否完成至少一个符号。
	///     返回预览消耗的输入字节数；输入不足返回 <see cref="DummyInputEof" />。
	/// </summary>
	private int TryDummy() {
		var range = _range;
		var code = _code;
		var state = _state;
		var bufPos = _inPos;
		var bufLimit = _inLen;

		for (;;) {
			var posState = (_processedPos & _pbMask) << 4;
			var probIndex = KStartOffset + IsMatch + (int)(posState + (uint)state);

			var bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probIndex);
			if (bit < 0) {
				return DummyInputEof;
			}

			if (bit == 0) {
				var litProbBase = KStartOffset + Literal;
				if (_checkDicSize != 0 || _processedPos != 0) {
					int prevByte = _dic[(_dicPos == 0 ? _dicBufSize : _dicPos) - 1];
					litProbBase += LzmaLitSize * (((int)(_processedPos & (1u << _lp) - 1) << _lc) + (prevByte >> 8 - _lc));
				}

				if (state < KNumLitStates) {
					var symbol = 1;
					do {
						bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, litProbBase + symbol);
						if (bit < 0) {
							return DummyInputEof;
						}

						symbol = symbol << 1 | bit;
					} while (symbol < 0x100);
				} else {
					uint matchByte = _dic[_dicPos - (int)_rep0 + (_dicPos < _rep0 ? _dicBufSize : 0)];
					uint offs = 0x100;
					var symbol = 1;
					do {
						matchByte += matchByte;
						var b = offs;
						offs &= matchByte;
						bit = DecodeBitCheck(ref range,
							ref code,
							ref bufPos,
							bufLimit,
							_probs,
							litProbBase + (int)(offs + b + (uint)symbol));
						if (bit < 0) {
							return DummyInputEof;
						}

						symbol = symbol << 1 | bit;
						if (bit != 0) {
							offs ^= b;
						}
					} while (symbol < 0x100);
				}
			} else {
				int len;
				probIndex = KStartOffset + IsRep + state;
				bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probIndex);
				if (bit < 0) {
					return DummyInputEof;
				}

				if (bit == 0) {
					state = 0;
					probIndex = KStartOffset + LenCoder;
				} else {
					probIndex = KStartOffset + IsRepG0 + state;
					bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probIndex);
					if (bit < 0) {
						return DummyInputEof;
					}

					if (bit == 0) {
						probIndex = KStartOffset + IsRep0Long + (int)(posState + (uint)state);
						bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probIndex);
						if (bit < 0) {
							return DummyInputEof;
						}

						if (bit == 0) {
							break;
						}
					} else {
						probIndex = KStartOffset + IsRepG1 + state;
						bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probIndex);
						if (bit < 0) {
							return DummyInputEof;
						}

						if (bit != 0) {
							probIndex = KStartOffset + IsRepG2 + state;
							bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probIndex);
							if (bit < 0) {
								return DummyInputEof;
							}
						}
					}

					state = KNumStates;
					probIndex = KStartOffset + RepLenCoder;
				}

				// 长度解码
				var probLenBase = probIndex;
				bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probLenBase + LenChoice);
				if (bit < 0) {
					return DummyInputEof;
				}

				int offset;
				if (bit == 0) {
					var probLen = probLenBase + LenLow + (int)posState;
					len = 1;
					for (var i = 0; i < KLenNumLowBits; i++) {
						bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probLen + len);
						if (bit < 0) {
							return DummyInputEof;
						}

						len = len << 1 | bit;
					}

					len -= 8;
					offset = 0;
				} else {
					bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probLenBase + LenChoice2);
					if (bit < 0) {
						return DummyInputEof;
					}

					if (bit == 0) {
						var probLen = probLenBase + LenLow + (1 << KLenNumLowBits) + (int)posState;
						len = 1;
						for (var i = 0; i < KLenNumLowBits; i++) {
							bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probLen + len);
							if (bit < 0) {
								return DummyInputEof;
							}

							len = len << 1 | bit;
						}

						offset = KLenNumLowSymbols;
					} else {
						var probLen = probLenBase + LenHigh;
						len = 1;
						for (var i = 0; i < KLenNumHighBits; i++) {
							bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probLen + len);
							if (bit < 0) {
								return DummyInputEof;
							}

							len = len << 1 | bit;
						}

						len -= 1 << KLenNumHighBits;
						offset = KLenNumLowSymbols * 2;
					}
				}

				len += offset;

				if (state < 4) {
					var posSlot = 1;
					var probSlot = KStartOffset + PosSlot
						+ (((uint)len < KNumLenToPosStates - 1 ? (int)(uint)len : KNumLenToPosStates - 1) << KNumPosSlotBits);
					for (var i = 0; i < KNumPosSlotBits; i++) {
						bit = DecodeBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, probSlot + posSlot);
						if (bit < 0) {
							return DummyInputEof;
						}

						posSlot = posSlot << 1 | bit;
					}

					posSlot -= 1 << KNumPosSlotBits;
					if (posSlot >= KStartPosModelIndex) {
						var numDirectBits = (posSlot >> 1) - 1;
						if (posSlot < KEndPosModelIndex) {
							// REV_BIT_VAR 反转解码（prob 基址固定，distance 从起始值 +1 开始）
							var distance = (uint)((2 | posSlot & 1) << numDirectBits) + 1;
							uint m = 1;
							const int prob = KStartOffset + SpecPos;
							while (numDirectBits-- > 0) {
								var b = DecodeRevBitCheck(ref range,
									ref code,
									ref bufPos,
									bufLimit,
									_probs,
									prob + (int)distance);
								if (b < 0) {
									return DummyInputEof;
								}

								if (b == 0) {
									distance += m;
									m += m;
								} else {
									m += m;
									distance += m;
								}
							}
						} else {
							numDirectBits -= KNumAlignBits;
							while (numDirectBits-- > 0) {
								if (!NormalizeCheck(ref range, ref code, ref bufPos, bufLimit)) {
									return DummyInputEof;
								}

								range >>= 1;
								code -= range & (code - range >> 31) - 1;
							}

							// REV_BIT_CONST x3 + REV_BIT_LAST
							const int prob = KStartOffset + Align;
							uint i = 1;
							for (var bitNo = 0; bitNo < KNumAlignBits - 1; bitNo++) {
								var m = 1u << bitNo;
								var b = DecodeRevBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, prob + (int)i);
								if (b < 0) {
									return DummyInputEof;
								}

								i += b == 0 ? m : m * 2;
							}

							{
								var b = DecodeRevBitCheck(ref range, ref code, ref bufPos, bufLimit, _probs, prob + (int)i);
								if (b < 0) {
									return DummyInputEof;
								}
							}
						}
					}
				}
			}

			break;
		}

		if (!NormalizeCheck(ref range, ref code, ref bufPos, bufLimit)) {
			return DummyInputEof;
		}

		return bufPos - _inPos;
	}

	/// <summary>预览用位解码（不更新概率，输入不足返回 -1）。</summary>
	private int DecodeBitCheck(ref uint range, ref uint code, ref int bufPos, int bufLimit, ushort[] probs, int probIndex) {
		uint ttt = probs[probIndex];
		if (!NormalizeCheck(ref range, ref code, ref bufPos, bufLimit)) {
			return -1;
		}

		var bound = (range >> 11) * ttt;
		if (code < bound) {
			range = bound;
			return 0;
		}

		code -= bound;
		range -= bound;
		return 1;
	}

	/// <summary>预览用反转位解码（REV_BIT_VAR：返回 0/1 位值）。</summary>
	private int DecodeRevBitCheck(ref uint range, ref uint code, ref int bufPos, int bufLimit, ushort[] probs, int probIndex) {
		uint ttt = probs[probIndex];
		if (!NormalizeCheck(ref range, ref code, ref bufPos, bufLimit)) {
			return -1;
		}

		var bound = (range >> 11) * ttt;
		if (code < bound) {
			range = bound;
			return 0;
		}

		code -= bound;
		range -= bound;
		return 1;
	}

	private bool NormalizeCheck(ref uint range, ref uint code, ref int bufPos, int bufLimit) {
		if (range >= KTopValue) {
			return true;
		}

		if (bufPos >= bufLimit) {
			return false;
		}

		range <<= 8;
		code = code << 8 | _inBuf[bufPos++];
		return true;
	}

	/// <summary>
	///     主解码循环（对应 LzmaDec_DecodeReal）。
	///     热路径使用 <see cref="Unsafe.Add{T}(ref T, int)" /> 直接寻址数组元素，消除逐位边界检查；
	///     索引不变式由距离上限检查（≤ 字典容量）与环回运算保证，与原生 liblzma 的裸指针行为一致。
	///     符号起点保存在局部变量（仅异常回滚时回写字典位置），避免每符号字段写入。
	/// </summary>
	private void DecodeReal(int limit, int bufLimit) {
		int lc = _lc;
		var lpMask = _lpMask;
		var pbMask = _pbMask;
		var dicBufSize = _dicBufSize;

		uint rep0 = _rep0, rep1 = _rep1, rep2 = _rep2, rep3 = _rep3;
		var state = _state;
		var processedPos = _processedPos;
		var checkDicSize = _checkDicSize;
		var len = 0;

		var range = _range;
		var code = _code;
		var buf = _inPos;
		var inLen = _inLen;

		ref var probs = ref MemoryMarshal.GetArrayDataReference(_probs);
		ref var dic = ref MemoryMarshal.GetArrayDataReference(_dic);
		ref var inBuf = ref MemoryMarshal.GetArrayDataReference(_inBuf);

		// 上一已写入字节：字面/匹配写入后本地跟踪，避免每个字面量重复从字典读取
		var prevByte = (uint)Unsafe.Add(ref dic, (_dicPos == 0 ? dicBufSize : _dicPos) - 1);

		var symbolDicStart = _dicPos;
		try {
			do {
				symbolDicStart = _dicPos;
			var posState = (processedPos & pbMask) << 4;
			var probIndex = KStartOffset + IsMatch + (int)(posState + (uint)state);
			if (DecodeBit(ref Unsafe.Add(ref probs, probIndex), ref range, ref code, ref buf, bufLimit, ref inBuf, inLen) ==
				0) {
				int symbol;
				probIndex = KStartOffset + Literal;
				if (processedPos != 0 || checkDicSize != 0) {
					probIndex += 3 * (((int)((processedPos << 8) + prevByte) & lpMask) << lc);
				}

				processedPos++;

				if (state < KNumLitStates) {
					state -= state < 4 ? state : 3;
					symbol = 1;
					for (var i = 0; i < 8; i++) {
						var tb2 = DecodeBit(ref Unsafe.Add(ref probs, probIndex + symbol),
							ref range,
							ref code,
							ref buf,
							bufLimit,
							ref inBuf,
							inLen);
						symbol = symbol << 1 | tb2;
					}
				} else {
					uint matchByte = Unsafe.Add(ref dic, _dicPos - (int)rep0 + (_dicPos < rep0 ? dicBufSize : 0));
					uint offs = 0x100;
					state -= state < 10 ? 3 : 6;
					symbol = 1;
					for (var i = 0; i < 8; i++) {
						matchByte += matchByte;
						var bit = offs;
						offs &= matchByte;
						var b = DecodeBit(ref Unsafe.Add(ref probs, probIndex + (int)(offs + bit + (uint)symbol)),
							ref range,
							ref code,
							ref buf,
							bufLimit,
							ref inBuf,
							inLen);
						symbol = symbol << 1 | b;
						if (b == 0) {
							offs ^= bit;
						}
					}
				}

				Unsafe.Add(ref dic, _dicPos++) = (byte)symbol;
				prevByte = (uint)symbol;
				continue;
			}

			probIndex = KStartOffset + IsRep + state;
			if (DecodeBit(ref Unsafe.Add(ref probs, probIndex), ref range, ref code, ref buf, bufLimit, ref inBuf, inLen) ==
				0) {
				state += KNumStates;
				probIndex = KStartOffset + LenCoder;
			} else {
				probIndex = KStartOffset + IsRepG0 + state;
				if (DecodeBit(ref Unsafe.Add(ref probs, probIndex), ref range, ref code, ref buf, bufLimit, ref inBuf, inLen) ==
					0) {
					probIndex = KStartOffset + IsRep0Long + (int)(posState + (uint)state);
					if (DecodeBit(ref Unsafe.Add(ref probs, probIndex),
						ref range,
						ref code,
						ref buf,
						bufLimit,
						ref inBuf,
						inLen) == 0) {
						Unsafe.Add(ref dic, _dicPos) =
							Unsafe.Add(ref dic, _dicPos - (int)rep0 + (_dicPos < rep0 ? dicBufSize : 0));
						_dicPos++;
						processedPos++;
						state = state < KNumLitStates ? 9 : 11;
						prevByte = Unsafe.Add(ref dic, _dicPos - 1);
						continue;
					}
				} else {
					uint distance;
					probIndex = KStartOffset + IsRepG1 + state;
					if (DecodeBit(ref Unsafe.Add(ref probs, probIndex),
						ref range,
						ref code,
						ref buf,
						bufLimit,
						ref inBuf,
						inLen) == 0) {
						distance = rep1;
					} else {
						probIndex = KStartOffset + IsRepG2 + state;
						if (DecodeBit(ref Unsafe.Add(ref probs, probIndex),
							ref range,
							ref code,
							ref buf,
							bufLimit,
							ref inBuf,
							inLen) == 0) {
							distance = rep2;
						} else {
							distance = rep3;
							rep3 = rep2;
						}

						rep2 = rep1;
					}

					rep1 = rep0;
					rep0 = distance;
				}

				state = state < KNumLitStates ? 8 : 11;
				probIndex = KStartOffset + RepLenCoder;
			}

			// 长度解码
			{
				var probLenBase = probIndex;
				if (DecodeBit(ref Unsafe.Add(ref probs, probLenBase + LenChoice),
					ref range,
					ref code,
					ref buf,
					bufLimit,
					ref inBuf,
					inLen) == 0) {
					var probLen = probLenBase + LenLow + (int)posState;
					len = 1;
					for (var i = 0; i < KLenNumLowBits; i++) {
						len = len << 1 | DecodeBit(ref Unsafe.Add(ref probs, probLen + len),
							ref range,
							ref code,
							ref buf,
							bufLimit,
							ref inBuf,
							inLen);
					}

					len -= 8;
				} else {
					if (DecodeBit(ref Unsafe.Add(ref probs, probLenBase + LenChoice2),
						ref range,
						ref code,
						ref buf,
						bufLimit,
						ref inBuf,
						inLen) == 0) {
						var probLen = probLenBase + LenLow + (1 << KLenNumLowBits) + (int)posState;
						len = 1;
						for (var i = 0; i < KLenNumLowBits; i++) {
							len = len << 1 | DecodeBit(ref Unsafe.Add(ref probs, probLen + len),
								ref range,
								ref code,
								ref buf,
								bufLimit,
								ref inBuf,
								inLen);
						}
					} else {
						var probLen = probLenBase + LenHigh;
						len = 1;
						for (var i = 0; i < KLenNumHighBits; i++) {
							len = len << 1 | DecodeBit(ref Unsafe.Add(ref probs, probLen + len),
								ref range,
								ref code,
								ref buf,
								bufLimit,
								ref inBuf,
								inLen);
						}

						len -= 1 << KLenNumHighBits;
						len += KLenNumLowSymbols * 2;
					}
				}
			}

			if (state >= KNumStates) {
				uint distance;
				var probSlot = KStartOffset + PosSlot
					+ (((uint)len < KNumLenToPosStates ? (int)(uint)len : KNumLenToPosStates - 1) << KNumPosSlotBits);
				distance = 1;
				for (var i = 0; i < KNumPosSlotBits; i++) {
					distance = distance << 1 | (uint)DecodeBit(ref Unsafe.Add(ref probs, probSlot + (int)distance),
						ref range,
						ref code,
						ref buf,
						bufLimit,
						ref inBuf,
						inLen);
				}

				distance -= 1 << KNumPosSlotBits;
				if (distance >= KStartPosModelIndex) {
					var posSlot = (int)distance;
					var numDirectBits = (int)((distance >> 1) - 1);
					distance = 2 | distance & 1;
					if (posSlot < KEndPosModelIndex) {
						distance <<= numDirectBits;
						var prob = KStartOffset + SpecPos;
						uint m = 1;
						distance++;
						while (--numDirectBits >= 0) {
							var probIdx = prob + (int)distance;
							ref var probRef = ref Unsafe.Add(ref probs, probIdx);
							uint ttt = probRef;
							Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
							var bound = (range >> 11) * ttt;
							if (code < bound) {
								range = bound;
								probRef = (ushort)(ttt + (KBitModelTotal - ttt >> KNumMoveBits));
								Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
								distance += m;
								m += m;
							} else {
								code -= bound;
								range -= bound;
								probRef = (ushort)(ttt - (ttt >> KNumMoveBits));
								Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
								m += m;
								distance += m;
							}
						}

						distance -= m;
					} else {
						numDirectBits -= KNumAlignBits;
						while (--numDirectBits >= 0) {
							Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
							range >>= 1;
							code -= range;
							var t = 0 - (code >> 31);
							distance = (distance << 1) + t + 1;
							code += range & t;
						}

						var prob = KStartOffset + Align;
						uint i = 1;
						// 反转解码 4 位（REV_BIT_CONST x3 + REV_BIT_LAST），
						// 每次用累积的 i 作为概率索引
						DecodeRevConst(ref Unsafe.Add(ref probs, prob + (int)i),
							ref range,
							ref code,
							ref buf,
							1,
							ref i,
							bufLimit,
							ref inBuf,
							inLen);
						DecodeRevConst(ref Unsafe.Add(ref probs, prob + (int)i),
							ref range,
							ref code,
							ref buf,
							2,
							ref i,
							bufLimit,
							ref inBuf,
							inLen);
						DecodeRevConst(ref Unsafe.Add(ref probs, prob + (int)i),
							ref range,
							ref code,
							ref buf,
							4,
							ref i,
							bufLimit,
							ref inBuf,
							inLen);
						DecodeRevLast(ref Unsafe.Add(ref probs, prob + (int)i),
							ref range,
							ref code,
							ref buf,
							ref i,
							bufLimit,
							ref inBuf,
							inLen);
						distance <<= KNumAlignBits;
						distance |= i;
						if (distance == 0xFFFFFFFF) {
							len = KMatchSpecLenStart;
							state -= KNumStates;
							break;
						}
					}
				}

				rep3 = rep2;
				rep2 = rep1;
				rep1 = rep0;
				rep0 = distance + 1;
				state = state < KNumStates + KNumLitStates ? KNumLitStates : KNumLitStates + 3;
				var checkLimit = checkDicSize != 0
					? checkDicSize
					: Math.Max(processedPos, ExternalCheckDicSize);
				// 距离上限取字典容量：保证环回索引始终在字典范围内（防御损坏数据越界）
				if (checkLimit > (uint)dicBufSize) {
					checkLimit = (uint)dicBufSize;
				}

				if (distance >= checkLimit) {
					len += KMatchSpecLenErrorData + KMatchMinLen;
					break;
				}
			}

			len += KMatchMinLen;

			{
				var rem = limit - _dicPos;
				if (rem == 0) {
					break;
				}

				var curLen = rem < len ? rem : len;
				var pos = _dicPos - (int)rep0 + (_dicPos < rep0 ? dicBufSize : 0);

				processedPos += (uint)curLen;
				len -= curLen;
				if (curLen <= dicBufSize - pos) {
					var dest = _dicPos;
					_dicPos += curLen;
					if (rep0 >= (uint)curLen) {
						// 区域不重叠：memmove 语义整段复制
						Array.Copy(_dic, pos, _dic, dest, curLen);
					} else if (rep0 == 1) {
						// 距离 1：单字节重复（LZ77 传播语义 = 填充），SIMD 填充替代倍增复制
						var b = Unsafe.Add(ref dic, pos);
						_dic.AsSpan(dest, curLen).Fill(b);
					} else {
						// 重叠：先复制 rep0 字节的种子，再倍增复制覆盖剩余
						Array.Copy(_dic, pos, _dic, dest, (int)rep0);
						var copied = (int)rep0;
						while (copied < curLen) {
							var n = Math.Min(copied, curLen - copied);
							Array.Copy(_dic, dest, _dic, dest + copied, n);
							copied += n;
						}
					}
				} else {
					// 源区跨越字典环回边界：逐字节复制
					for (var i = 0; i < curLen; i++) {
						Unsafe.Add(ref dic, _dicPos++) = Unsafe.Add(ref dic, pos);
						if (++pos == dicBufSize) {
							pos = 0;
						}
					}
				}

				// 记录最后写入字节（字面上下文）
				prevByte = Unsafe.Add(ref dic, (_dicPos == 0 ? dicBufSize : _dicPos) - 1);
			}
		} while (_dicPos < limit && buf < bufLimit);

		if (buf < inLen) {
			Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
		}

		// 与参考实现一致：匹配超出字典属于格式错误，抛出前写回字段（含 _remainLen ≥ ErrorData，
		// 令后续调用以 EOF 终止流）；此处置于 try 内以统一回滚 _dicPos 到符号起点
		if (len >= KMatchSpecLenErrorData) {
			_remainLen = KMatchSpecLenErrorData;
			throw new InnoFormatException("LZMA 数据错误（匹配超出字典）");
		}
		} catch (InputEofException) {
			// 符号未完成：回滚到符号起点（与 liblzma 的 LZMA_BUF_ERROR 行为一致）
			_dicPos = symbolDicStart;
			throw;
		} catch (InnoFormatException) {
			// 格式错误（匹配超出字典）：回滚未完成符号后传播；_remainLen 已置 ErrorData，
			// 与参考实现的"字段写回后回滚"在后续调用上行为一致（EOF 终止）
			_dicPos = symbolDicStart;
			throw;
		}

		_inPos = buf;
		_range = range;
		_code = code;
		_remainLen = len;
		_processedPos = processedPos;
		_checkDicSize = checkDicSize;
		_rep0 = rep0;
		_rep1 = rep1;
		_rep2 = rep2;
		_rep3 = rep3;
		_state = state;
	}

	/// <summary>REV_BIT_CONST：反转解码一位（m 为 1/2/4）。</summary>
	static private void DecodeRevConst(
		ref ushort prob,
		ref uint range,
		ref uint code,
		ref int buf,
		uint m,
		ref uint i,
		int bufLimit,
		ref byte inBuf,
		int inLen) {
		uint ttt = prob;
		Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
		var bound = (range >> 11) * ttt;
		if (code < bound) {
			range = bound;
			prob = (ushort)(ttt + (KBitModelTotal - ttt >> KNumMoveBits));
			Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
			i += m;
		} else {
			code -= bound;
			range -= bound;
			prob = (ushort)(ttt - (ttt >> KNumMoveBits));
			Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
			i += m * 2;
		}
	}

	/// <summary>REV_BIT_LAST：反转解码最后一位（m=8，0 时 i -= 8）。</summary>
	static private void DecodeRevLast(
		ref ushort prob,
		ref uint range,
		ref uint code,
		ref int buf,
		ref uint i,
		int bufLimit,
		ref byte inBuf,
		int inLen) {
		uint ttt = prob;
		Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
		var bound = (range >> 11) * ttt;
		if (code < bound) {
			range = bound;
			prob = (ushort)(ttt + (KBitModelTotal - ttt >> KNumMoveBits));
			Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
			i -= 8;
		} else {
			code -= bound;
			range -= bound;
			prob = (ushort)(ttt - (ttt >> KNumMoveBits));
			Normalize(ref range, ref code, ref buf, bufLimit, ref inBuf, inLen);
		}
	}

	/// <summary>输入不足且不允许截断时抛出的内部异常（符号未完成，不输出）。</summary>
	sealed private class InputEofException : Exception { }
}