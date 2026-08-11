namespace InnoUnpack.NET.Reading;

/// <summary>
///     提供对 setup 数据（chunk 与块）的切片读取。
///     支持两种模式：
///     1. 单文件模式：数据内嵌于安装包 exe 中，从 <c>dataOffset</c> 起读取；
///     2. 外部切片模式：数据存放于 <c>setup-N.bin</c> 文件中（多磁盘安装包）。
///     每个切片文件以 8 字节魔数（"idska16\x1a" / "idska32\x1a"）和 4 字节总大小开头。
/// </summary>
sealed class SliceReader : IDisposable {
	private readonly string _baseFilename;
	private readonly string _baseFilename2;
	private readonly ulong _dataOffset;

	private readonly Stream? _embedded;
	private readonly string? _sliceDirectory;
	private readonly int _slicesPerDisk;
	private readonly bool _ownsEmbedded;
	private uint _currentSlice = uint.MaxValue;

	private FileStream? _sliceFile;
	private ulong _sliceSize;

	private SliceReader(Stream embedded, ulong dataOffset, bool ownsEmbedded) {
		_embedded = embedded;
		_dataOffset = dataOffset;
		_baseFilename = string.Empty;
		_baseFilename2 = string.Empty;
		_slicesPerDisk = 1;
		_ownsEmbedded = ownsEmbedded;
	}

	private SliceReader(string sliceDirectory, string baseFilename, string baseFilename2, int slicesPerDisk) {
		_embedded = null;
		_dataOffset = 0;
		_sliceDirectory = sliceDirectory;
		_baseFilename = baseFilename;
		_baseFilename2 = baseFilename2;
		_slicesPerDisk = slicesPerDisk;
	}

	static private ReadOnlySpan<byte> SliceId16 => "idska16\u001A"u8;
	static private ReadOnlySpan<byte> SliceId32 => "idska32\u001A"u8;

	private Stream CurrentStream => _embedded ?? _sliceFile!;

	public void Dispose() {
		CloseSliceFile();
		if (_ownsEmbedded) {
			_embedded?.Dispose();
		}
	}

	/// <summary>创建单文件模式读取器（setup 数据内嵌于安装包中）。</summary>
	public static SliceReader CreateEmbedded(Stream stream, ulong dataOffset) { return new(stream, dataOffset, false); }

	/// <summary>
	///     创建持有内嵌流所有权的单文件模式读取器（并行提取时每个 worker 独立打开文件句柄，
	///     Dispose 时关闭该流）。
	/// </summary>
	public static SliceReader CreateEmbeddedOwned(Stream stream, ulong dataOffset) { return new(stream, dataOffset, true); }

	/// <summary>创建外部切片模式读取器（多磁盘安装包）。</summary>
	public static SliceReader CreateExternal(
		string sliceDirectory,
		string baseFilename,
		string baseFilename2,
		int slicesPerDisk) {
		return new(sliceDirectory, baseFilename, baseFilename2, slicesPerDisk);
	}

	/// <summary>
	///     定位到指定切片中的偏移位置。
	/// </summary>
	/// <returns>定位失败（偏移超出切片范围或切片不存在）时返回 false。</returns>
	public bool Seek(uint slice, ulong offset) {
		if (!OpenSlice(slice)) {
			return false;
		}

		var target = offset + _dataOffset;
		if (target > _sliceSize) {
			return false;
		}

		try {
			CurrentStream.Position = (long)target;
			return true;
		} catch (IOException) {
			return false;
		} catch (ArgumentOutOfRangeException) {
			return false;
		}
	}

	/// <summary>
	///     从当前位置读取数据，必要时自动切换到下一个切片。
	/// </summary>
	/// <returns>实际读取的字节数。</returns>
	public int Read(Span<byte> buffer) {
		var total = 0;
		while (buffer.Length > 0) {
			var stream = CurrentStream;
			var readPos = stream.Position;
			if (readPos >= (long)_sliceSize) {
				if (!OpenSlice(_currentSlice + 1)) {
					break;
				}

				stream = CurrentStream;
				readPos = stream.Position;
				if (readPos >= (long)_sliceSize) {
					break;
				}
			}

			var remaining = _sliceSize - (ulong)readPos;
			var toRead = (int)Math.Min((ulong)buffer.Length, remaining);
			var n = stream.Read(buffer[..toRead]);
			if (n <= 0) {
				break;
			}

			buffer = buffer[n..];
			total += n;
		}

		return total;
	}

	/// <summary>跳过指定数量的字节。</summary>
	public void Skip(long count) {
		Span<byte> temp = stackalloc byte[4096];
		while (count > 0) {
			var n = Read(temp[..(int)Math.Min(count, temp.Length)]);
			if (n <= 0) {
				throw new InnoFormatException("切片数据不足，无法跳过");
			}

			count -= n;
		}
	}

	private bool OpenSlice(uint slice) {
		if (slice == _currentSlice && CurrentStream.CanSeek) {
			return true;
		}

		if (_embedded is not null) {
			if (slice != 0) {
				return false;
			}

			_currentSlice = 0;
			_sliceSize = (ulong)(_embedded.Length > 0 ? _embedded.Length : 0);
			_embedded.Position = 0;
			return true;
		}

		_currentSlice = slice;
		CloseSliceFile();

		var filename = SliceFilename(_baseFilename, slice, _slicesPerDisk);
		if (TryOpenFile(Path.Combine(_sliceDirectory!, filename))) {
			return true;
		}

		if (!string.IsNullOrEmpty(_baseFilename2)) {
			var filename2 = SliceFilename(_baseFilename2, slice, _slicesPerDisk);
			if (filename2 != filename && TryOpenFile(Path.Combine(_sliceDirectory!, filename2))) {
				return true;
			}
		}

		return false;
	}

	private bool TryOpenFile(string path) {
		if (!File.Exists(path)) {
			return false;
		}

		try {
			FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			Span<byte> magic = stackalloc byte[8];
			if (file.Read(magic) != 8 || !IsSliceMagic(magic)) {
				file.Dispose();
				throw new InnoFormatException($"无效的切片文件魔数：{path}");
			}

			Span<byte> sizeBytes = stackalloc byte[4];
			if (file.Read(sizeBytes) != 4) {
				file.Dispose();
				throw new InnoFormatException($"无法读取切片大小：{path}");
			}

			var sliceSize = sizeBytes[0] | (ulong)sizeBytes[1] << 8 | (ulong)sizeBytes[2] << 16 | (ulong)sizeBytes[3] << 24;
			if (sliceSize > (ulong)file.Length || sliceSize < 12) {
				file.Dispose();
				throw new InnoFormatException($"切片大小无效：{path}");
			}

			_sliceFile = file;
			_sliceSize = sliceSize;
			return true;
		} catch (IOException) {
			return false;
		} catch (UnauthorizedAccessException) {
			return false;
		}
	}

	static private bool IsSliceMagic(ReadOnlySpan<byte> magic) {
		return magic.SequenceEqual(SliceId16) || magic.SequenceEqual(SliceId32);
	}

	/// <summary>
	///     生成切片文件名：slices_per_disk == 1 时为 <c>setup-N.bin</c>，
	///     否则为 <c>setup-Na.bin</c>（N 为磁盘号，a 为盘内切片序号）。
	/// </summary>
	internal static string SliceFilename(string basename, uint slice, int slicesPerDisk) {
		if (slicesPerDisk == 1) {
			return $"{basename}-{slice + 1}.bin";
		}

		var major = slice / (uint)slicesPerDisk + 1;
		var minor = (int)(slice % (uint)slicesPerDisk);
		return $"{basename}-{major}{(char)('a' + minor)}.bin";
	}

	private void CloseSliceFile() {
		_sliceFile?.Dispose();
		_sliceFile = null;
	}

	public long GetPosition() { return CurrentStream.Position; }
}