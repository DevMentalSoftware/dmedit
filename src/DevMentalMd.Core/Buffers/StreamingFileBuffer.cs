using System.Text;

namespace DevMentalMd.Core.Buffers;

/// <summary>
/// An <see cref="IBuffer"/> that reads a file in binary chunks on a background thread,
/// decoding to UTF-8 (or BOM-detected encoding) incrementally. The first chunk is available
/// almost immediately, and the line index is built as each chunk arrives.
/// </summary>
/// <remarks>
/// Thread safety: the background thread writes to <c>_data[_loadedLen..]</c> while the UI
/// thread reads from <c>_data[0.._loadedLen-1]</c>. The volatile <c>_loadedLen</c> field
/// acts as a memory barrier so the UI thread always sees fully-written data. The
/// <c>_lineStarts</c> array is guarded by <c>_lock</c> for safe growth/read.
/// </remarks>
public sealed class StreamingFileBuffer : IBuffer {
    private const int ChunkSize = 1_048_576; // 1 MB

    // Character storage — pre-allocated to worst-case size (1 char per byte for UTF-8).
    private char[]? _data;
    private volatile int _loadedLen;
    private volatile bool _done;

    // Line index — built incrementally as chunks arrive.
    private long[] _lineStarts;
    private volatile int _lineCount; // number of valid entries in _lineStarts
    private bool _prevWasCr;         // \r at end of previous chunk (background thread only)

    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _path;
    private readonly long _byteLen;

    /// <summary>Fired after each chunk is decoded (on the background thread).</summary>
    public event Action? ProgressChanged;

    /// <summary>Fired once when loading finishes (on the background thread).</summary>
    public event Action? LoadComplete;

    /// <summary>
    /// Creates a streaming buffer for the file at <paramref name="path"/>.
    /// Call <see cref="StartLoading"/> to begin reading on a background thread.
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <param name="byteLen">File size in bytes (from <c>new FileInfo(path).Length</c>).</param>
    public StreamingFileBuffer(string path, long byteLen) {
        _path = path;
        _byteLen = byteLen;

        // Worst case for UTF-8: 1 char per byte. Clamp to int.MaxValue.
        var maxChars = (int)Math.Min(byteLen, int.MaxValue);
        _data = new char[maxChars];

        // Initial line-starts: line 0 always starts at offset 0.
        // Pre-allocate a reasonable estimate (~1 line per 80 bytes).
        var estimatedLines = Math.Max(16, (int)Math.Min(byteLen / 80, int.MaxValue / 8));
        _lineStarts = new long[estimatedLines];
        _lineStarts[0] = 0L;
        _lineCount = 1;
    }

    // -----------------------------------------------------------------
    // IBuffer
    // -----------------------------------------------------------------

    public long Length => _loadedLen;

    public bool LengthIsKnown => _done;

    public long LineCount => _lineCount;

    public long GetLineStart(long lineIdx) {
        var count = _lineCount; // snapshot volatile
        if (lineIdx < 0 || lineIdx >= count) {
            return -1L;
        }
        lock (_lock) {
            return _lineStarts[lineIdx];
        }
    }

    public char this[long offset] {
        get {
            var len = _loadedLen; // snapshot volatile
            if (offset < 0 || offset >= len) {
                throw new ArgumentOutOfRangeException(nameof(offset),
                    $"Offset {offset} is out of range [0, {len}).");
            }
            return _data![offset];
        }
    }

    public void CopyTo(long offset, Span<char> destination, int len) {
        var loaded = _loadedLen;
        if (offset < 0 || len < 0 || offset + len > loaded) {
            throw new ArgumentOutOfRangeException(
                $"Range [{offset}, {offset + len}) exceeds loaded length {loaded}.");
        }
        _data.AsSpan((int)offset, len).CopyTo(destination);
    }

    public void Dispose() {
        _cts.Cancel();
        _cts.Dispose();
        _data = null; // allow GC
    }

    // -----------------------------------------------------------------
    // Loading
    // -----------------------------------------------------------------

    /// <summary>
    /// Starts reading the file on a thread-pool worker. Returns immediately.
    /// The first chunk fires <see cref="ProgressChanged"/> within a few milliseconds.
    /// </summary>
    public void StartLoading(CancellationToken externalCt = default) {
        // Link external + internal cancellation.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalCt);
        ThreadPool.QueueUserWorkItem(_ => LoadWorker(linked));
    }

    private void LoadWorker(CancellationTokenSource linkedCts) {
        try {
            var ct = linkedCts.Token;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.Read, ChunkSize, FileOptions.SequentialScan);

            var decoder = DetectEncodingAndCreateDecoder(fs);
            var byteBuf = new byte[ChunkSize];

            while (true) {
                ct.ThrowIfCancellationRequested();

                var bytesRead = fs.Read(byteBuf, 0, ChunkSize);
                if (bytesRead == 0) {
                    break;
                }

                var isLastChunk = fs.Position >= _byteLen;

                // Decode bytes → chars directly into _data past _loadedLen.
                var charCount = decoder.GetCharCount(byteBuf, 0, bytesRead, isLastChunk);

                // Ensure _data has room (multibyte chars mean we may need less than pre-allocated,
                // but defensive resize in case estimate was wrong).
                var needed = _loadedLen + charCount;
                if (needed > _data!.Length) {
                    lock (_lock) {
                        var newSize = Math.Max(_data.Length * 2, needed);
                        var newData = new char[newSize];
                        Array.Copy(_data, newData, _loadedLen);
                        _data = newData;
                    }
                }

                decoder.GetChars(byteBuf, 0, bytesRead, _data, _loadedLen, isLastChunk);

                // Scan the newly decoded chars for newlines.
                ScanNewlines(_data, _loadedLen, charCount);

                // Publish the new length (volatile write = memory barrier).
                _loadedLen += charCount;

                ProgressChanged?.Invoke();

                if (isLastChunk) {
                    break;
                }
            }

            // Finalize: handle trailing bare \r.
            if (_prevWasCr) {
                AppendLineStart(_loadedLen);
            }

            _done = true;
            LoadComplete?.Invoke();
        } catch (OperationCanceledException) {
            // Loading was cancelled (Dispose or external token). Mark as done with partial data.
            _done = true;
        } catch (Exception) {
            // On any I/O error, mark as done with whatever we have.
            _done = true;
        } finally {
            linkedCts.Dispose();
        }
    }

    // -----------------------------------------------------------------
    // BOM detection
    // -----------------------------------------------------------------

    private static Decoder DetectEncodingAndCreateDecoder(FileStream fs) {
        Span<byte> bom = stackalloc byte[3];
        var bomRead = fs.Read(bom);

        if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) {
            // UTF-8 BOM — skip it, continue reading as UTF-8.
            return Encoding.UTF8.GetDecoder();
        }
        if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) {
            // UTF-16 LE BOM.
            fs.Position = 2;
            return Encoding.Unicode.GetDecoder();
        }
        if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) {
            // UTF-16 BE BOM.
            fs.Position = 2;
            return Encoding.BigEndianUnicode.GetDecoder();
        }

        // No BOM — rewind and assume UTF-8.
        fs.Position = 0;
        return Encoding.UTF8.GetDecoder();
    }

    // -----------------------------------------------------------------
    // Newline scanning (background thread only)
    // -----------------------------------------------------------------

    /// <summary>
    /// Scans <paramref name="charCount"/> characters in <paramref name="data"/> starting
    /// at <paramref name="start"/> for newlines. Appends line-start offsets and updates
    /// <c>_prevWasCr</c> for cross-chunk \r\n handling.
    /// </summary>
    private void ScanNewlines(char[] data, int start, int charCount) {
        var end = start + charCount;
        var scanStart = start;

        // Resolve deferred \r from previous chunk.
        if (_prevWasCr && charCount > 0) {
            _prevWasCr = false;
            if (data[start] == '\n') {
                // \r\n crossing chunk boundary.
                AppendLineStart(start + 1);
                scanStart = start + 1;
            } else {
                // Bare \r at end of previous chunk.
                AppendLineStart(start);
            }
        }

        for (var i = scanStart; i < end; i++) {
            var ch = data[i];
            if (ch == '\n') {
                AppendLineStart(i + 1);
            } else if (ch == '\r') {
                if (i + 1 < end) {
                    if (data[i + 1] != '\n') {
                        // Bare \r.
                        AppendLineStart(i + 1);
                    }
                    // else \r\n within chunk — let the \n branch handle it.
                } else {
                    // \r at end of chunk — defer.
                    _prevWasCr = true;
                }
            }
        }
    }

    private void AppendLineStart(long offset) {
        lock (_lock) {
            if (_lineCount >= _lineStarts.Length) {
                var newSize = _lineStarts.Length * 2;
                var newArr = new long[newSize];
                Array.Copy(_lineStarts, newArr, _lineCount);
                _lineStarts = newArr;
            }
            _lineStarts[_lineCount] = offset;
        }
        // Update count AFTER the data is written (volatile write).
        _lineCount = _lineCount + 1;
    }
}
