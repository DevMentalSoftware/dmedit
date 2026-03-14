using System.Security.Cryptography;
using System.Text;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.Buffers;

/// <summary>
/// An <see cref="IBuffer"/> that reads from a file or arbitrary stream in binary chunks on a
/// background thread, decoding to UTF-8 (or BOM-detected encoding) incrementally. The first
/// chunk is available almost immediately, and the line index is built as each chunk arrives.
/// </summary>
/// <remarks>
/// Thread safety: the background thread writes to <c>_data[_loadedLen..]</c> while the UI
/// thread reads from <c>_data[0.._loadedLen-1]</c>. The volatile <c>_loadedLen</c> field
/// acts as a memory barrier so the UI thread always sees fully-written data. The
/// <c>_lineStarts</c> array is guarded by <c>_lock</c> for safe growth/read.
/// Note: We used to use this for text files of relatively small size, but we found that
///    the PagedFileBuffer was fast enough for those cases too. So now we only use this
///    to support loading zipped text files. 
/// </remarks>
public sealed class StreamingFileBuffer : IProgressBuffer {
    private const int ChunkSize = 1_048_576; // 1 MB

    // Character storage — pre-allocated to worst-case size (1 char per byte for UTF-8).
    private char[]? _data;
    private volatile int _loadedLen;
    private volatile bool _done;

    // Line index — built incrementally as chunks arrive.
    private long[] _lineStarts = null!; // initialized by InitBuffers, called from all constructors
    private volatile int _lineCount;    // number of valid entries in _lineStarts
    private bool _prevWasCr;         // \r at end of previous chunk (background thread only)

    // Line ending counters (accumulated during scan)
    private int _lfCount;
    private int _crlfCount;
    private int _crCount;

    // Indentation counters (accumulated during scan)
    private int _spaceIndentCount;
    private int _tabIndentCount;
    private bool _atLineStart = true;

    private string? _sha1;

    /// <summary>
    /// SHA-1 hash of the decompressed stream bytes (lowercase hex).
    /// Available after <see cref="LoadComplete"/> fires; <c>null</c> before.
    /// </summary>
    public string? Sha1 => _sha1;

    /// <inheritdoc />
    public LineEndingInfo DetectedLineEnding =>
        LineEndingInfo.FromCounts(_lfCount, _crlfCount, _crCount);

    public IndentInfo DetectedIndent =>
        IndentInfo.FromCounts(_spaceIndentCount, _tabIndentCount);

    private Encoding _detectedEncoding = Encoding.UTF8;
    private bool _hadBom;

    public Documents.EncodingInfo DetectedEncoding =>
        Documents.EncodingInfo.FromDetection(_detectedEncoding, _hadBom);

    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _loadedEvent = new(false);
    private readonly string? _path;
    private readonly long _estimatedLen;
    private readonly Stream? _externalStream;
    private readonly IDisposable? _owner;

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
        _estimatedLen = byteLen;
        InitBuffers(byteLen);
    }

    /// <summary>
    /// Creates a streaming buffer that reads from an arbitrary <paramref name="stream"/>.
    /// The caller provides an estimated uncompressed size for pre-allocation.
    /// The <paramref name="owner"/> (e.g., a <c>ZipArchive</c>) is disposed when loading
    /// completes or the buffer is disposed.
    /// </summary>
    public StreamingFileBuffer(Stream stream, long estimatedCharLen, IDisposable? owner = null) {
        _externalStream = stream;
        _owner = owner;
        _estimatedLen = estimatedCharLen;
        InitBuffers(estimatedCharLen);
    }

    private void InitBuffers(long estimatedLen) {
        // Worst case for UTF-8: 1 char per byte. Clamp to int.MaxValue.
        var maxChars = (int)Math.Min(estimatedLen, int.MaxValue);
        _data = new char[Math.Max(maxChars, 1024)];

        // Initial line-starts: line 0 always starts at offset 0.
        // Pre-allocate a reasonable estimate (~1 line per 80 bytes).
        var estimatedLines = Math.Max(16, (int)Math.Min(estimatedLen / 80, int.MaxValue / 8));
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

    public int LongestLine => 10_000;

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

    /// <summary>
    /// Blocks the calling thread until loading finishes.
    /// </summary>
    public void WaitUntilLoaded() => _loadedEvent.Wait();

    public void Dispose() {
        _cts.Cancel();
        _cts.Dispose();
        _loadedEvent.Dispose();
        _owner?.Dispose();
        _data = null; // allow GC
    }

    // -----------------------------------------------------------------
    // Loading
    // -----------------------------------------------------------------

    /// <summary>
    /// Starts reading the file or stream on a thread-pool worker. Returns immediately.
    /// The first chunk fires <see cref="ProgressChanged"/> within a few milliseconds.
    /// </summary>
    public void StartLoading(CancellationToken externalCt = default) {
        // Link external + internal cancellation.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalCt);
        ThreadPool.QueueUserWorkItem(_ => LoadWorker(linked));
    }

    private void LoadWorker(CancellationTokenSource linkedCts) {
        Stream? ownedStream = null;
        try {
            var ct = linkedCts.Token;

            // Open a FileStream if constructed with a path; otherwise use the external stream.
            Stream stream;
            if (_path is not null) {
                ownedStream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, ChunkSize, FileOptions.SequentialScan);
                stream = ownedStream;
            } else {
                stream = _externalStream!;
            }

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var (decoder, prefetched, detEnc, detBom) = DetectEncodingAndCreateDecoder(stream);
            _detectedEncoding = detEnc;
            _hadBom = detBom;
            var byteBuf = new byte[ChunkSize];

            // If BOM detection consumed bytes on a non-seekable stream, decode them first.
            if (prefetched is { Length: > 0 }) {
                hasher.AppendData(prefetched, 0, prefetched.Length);
                DecodeChunk(decoder, prefetched, prefetched.Length, isLastChunk: false);
            }

            var reachedEnd = false;
            while (true) {
                ct.ThrowIfCancellationRequested();

                var bytesRead = stream.Read(byteBuf, 0, ChunkSize);
                if (bytesRead == 0) {
                    break;
                }

                hasher.AppendData(byteBuf, 0, bytesRead);

                // For file streams we know the total byte length; for arbitrary streams
                // we detect end-of-stream when the next Read returns 0.
                var isLastChunk = _path is not null && stream.Position >= _estimatedLen;

                DecodeChunk(decoder, byteBuf, bytesRead, isLastChunk);
                ProgressChanged?.Invoke();

                if (isLastChunk) {
                    reachedEnd = true;
                    break;
                }
            }

            // For non-seekable streams (zip entries), the loop exits on bytesRead == 0
            // without ever setting isLastChunk. Flush the decoder to emit any buffered
            // bytes from an incomplete multi-byte sequence at end-of-stream.
            if (!reachedEnd) {
                DecodeChunk(decoder, [], 0, isLastChunk: true);
            }

            // Finalize: handle trailing bare \r.
            if (_prevWasCr) {
                _crCount++;
                AppendLineStart(_loadedLen);
            }

            _sha1 = Convert.ToHexStringLower(hasher.GetHashAndReset());
            _done = true;
            LoadComplete?.Invoke();
        } catch (OperationCanceledException) {
            // Loading was cancelled (Dispose or external token). Mark as done with partial data.
            _done = true;
        } catch (Exception) {
            // On any I/O error, mark as done with whatever we have.
            _done = true;
        } finally {
            _loadedEvent.Set();
            ownedStream?.Dispose();
            linkedCts.Dispose();
        }
    }

    // -----------------------------------------------------------------
    // Chunk decoding (shared by main loop and prefetched-byte path)
    // -----------------------------------------------------------------

    /// <summary>
    /// Decodes <paramref name="bytesRead"/> bytes from <paramref name="byteBuf"/> using
    /// <paramref name="decoder"/>, appends the resulting chars to <c>_data</c>, scans for
    /// newlines, and publishes the updated <c>_loadedLen</c>.
    /// </summary>
    private void DecodeChunk(Decoder decoder, byte[] byteBuf, int bytesRead, bool isLastChunk) {
        var charCount = decoder.GetCharCount(byteBuf, 0, bytesRead, isLastChunk);

        // Ensure _data has room.
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
    }

    // -----------------------------------------------------------------
    // BOM detection
    // -----------------------------------------------------------------

    /// <summary>
    /// Reads up to 3 bytes from <paramref name="stream"/> to detect a BOM.
    /// Returns the appropriate decoder and any bytes that were consumed but not part
    /// of a BOM (for non-seekable streams where we can't rewind).
    /// </summary>
    private static (Decoder decoder, byte[]? prefetched, Encoding encoding, bool hadBom) DetectEncodingAndCreateDecoder(Stream stream) {
        var bom = new byte[3];
        var bomRead = stream.Read(bom, 0, 3);

        if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) {
            // UTF-8 BOM — consumed, continue reading as UTF-8.
            return (Encoding.UTF8.GetDecoder(), null, Encoding.UTF8, true);
        }
        if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) {
            // UTF-16 LE BOM.
            if (stream.CanSeek) {
                stream.Position = 2;
                return (Encoding.Unicode.GetDecoder(), null, Encoding.Unicode, true);
            }
            // Non-seekable: the 3rd byte (if read) is data, not BOM.
            return bomRead > 2
                ? (Encoding.Unicode.GetDecoder(), [bom[2]], Encoding.Unicode, true)
                : (Encoding.Unicode.GetDecoder(), null, Encoding.Unicode, true);
        }
        if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) {
            // UTF-16 BE BOM.
            if (stream.CanSeek) {
                stream.Position = 2;
                return (Encoding.BigEndianUnicode.GetDecoder(), null, Encoding.BigEndianUnicode, true);
            }
            return bomRead > 2
                ? (Encoding.BigEndianUnicode.GetDecoder(), [bom[2]], Encoding.BigEndianUnicode, true)
                : (Encoding.BigEndianUnicode.GetDecoder(), null, Encoding.BigEndianUnicode, true);
        }

        // No BOM — rewind if possible; otherwise return consumed bytes as prefetched data.
        if (stream.CanSeek) {
            stream.Position = 0;
            return (Encoding.UTF8.GetDecoder(), null, Encoding.UTF8, false);
        }
        // Non-seekable: return the consumed bytes so the caller can decode them.
        return bomRead > 0
            ? (Encoding.UTF8.GetDecoder(), bom[..bomRead], Encoding.UTF8, false)
            : (Encoding.UTF8.GetDecoder(), null, Encoding.UTF8, false);
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
                _crlfCount++;
                AppendLineStart(start + 1);
                scanStart = start + 1;
                _atLineStart = true;
            } else {
                // Bare \r at end of previous chunk.
                _crCount++;
                AppendLineStart(start);
                _atLineStart = true;
            }
        }

        for (var i = scanStart; i < end; i++) {
            var ch = data[i];
            if (ch == '\n') {
                _lfCount++;
                AppendLineStart(i + 1);
                _atLineStart = true;
            } else if (ch == '\r') {
                if (i + 1 < end) {
                    if (data[i + 1] != '\n') {
                        // Bare \r.
                        _crCount++;
                        AppendLineStart(i + 1);
                        _atLineStart = true;
                    }
                    // else \r\n within chunk — let the \n branch handle it
                    // BUT we need to count it and skip the \n.
                    else {
                        _crlfCount++;
                        AppendLineStart(i + 2);
                        i++; // skip the \n
                        _atLineStart = true;
                    }
                } else {
                    // \r at end of chunk — defer.
                    _prevWasCr = true;
                }
            } else if (_atLineStart) {
                // Track indentation style: check first char of each line.
                if (ch == ' ') {
                    _spaceIndentCount++;
                } else if (ch == '\t') {
                    _tabIndentCount++;
                }
                _atLineStart = false;
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
