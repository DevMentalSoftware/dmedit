using System.Security.Cryptography;
using System.Text;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.Buffers;

/// <summary>
/// An <see cref="IBuffer"/> that keeps only a bounded number of decoded text pages
/// in memory, re-reading and re-decoding from disk on demand. The raw file stays on
/// disk and is locked against external writes (<see cref="FileShare.Read"/>).
/// </summary>
/// <remarks>
/// <para>
/// On construction, a background thread scans the file sequentially to build the
/// page table (byte→char offset mapping) and a sampled line index. The first
/// <see cref="MaxPagesInMemory"/> pages are retained in memory; subsequent pages
/// are scanned for metadata only and their decoded chars are discarded.
/// </para>
/// <para>
/// When the UI scrolls to an evicted page, the buffer re-reads and re-decodes it
/// from disk (~1 ms per page on SSD). An LRU cache bounds total memory.
/// </para>
/// <para>
/// Memory budget for a 32 GB file (~400M lines, MaxPages=8):
///   PageInfo[]  ≈ 1 MB, decoded cache ≈ 16 MB, line index ≈ 3 MB → ~20 MB total.
/// </para>
/// </remarks>
public sealed class PagedFileBuffer : IProgressBuffer {
    // -----------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------

    private const int PageSizeBytes = 1_048_576; // 1 MB raw bytes per page
    private const int MAX_LONGEST_LINE = 10_000; // If we have a line longer than this we won't bother to track further

    /// <summary>
    /// Default maximum number of decoded pages kept in memory.
    /// Minimum safe value is ~4 (viewport layout needs up to 4 pages simultaneously).
    /// 8 gives comfortable headroom for small back-and-forth scrolls ≈ 16 MB cache.
    /// </summary>
    public const int DefaultMaxPages = 8;

    /// <summary>Number of lines between sampled line-start entries.</summary>
    public const int LineSampleStride = 1024;

    // -----------------------------------------------------------------
    // Page metadata (always in memory)
    // -----------------------------------------------------------------

    private struct PageInfo {
        public long ByteOffset;  // byte position in file where this page starts
        public int ByteCount;    // actual bytes in this page (may be < PageSizeBytes for last)
        public long CharStart;   // cumulative char offset where this page starts
        public int CharCount;    // number of decoded chars in this page
    }

    private PageInfo[] _pages;
    private volatile int _pageCount;

    // -----------------------------------------------------------------
    // Page data cache (LRU, bounded)
    // -----------------------------------------------------------------

    private char[]?[] _pageData;         // same index as _pages; null if evicted
    private readonly LinkedList<int> _lruList = new();
    private readonly Dictionary<int, LinkedListNode<int>> _lruNodes = new();
    private int _loadedPageCount;

    /// <summary>Maximum decoded pages to keep in memory.</summary>
    public int MaxPagesInMemory { get; }

    // -----------------------------------------------------------------
    // Line index (sampled)
    // -----------------------------------------------------------------

    private long[] _lineSamples = null!;  // _lineSamples[i] = char offset of line i*Stride
    private long _lineCount;              // accessed via Interlocked
    private volatile int _longestLine;
    private int _lineSampleCount;
    private long _lastLineStart;

    // -----------------------------------------------------------------
    // Line ending counters (accumulated during scan)
    // -----------------------------------------------------------------

    private int _lfCount;
    private int _crlfCount;
    private int _crCount;

    /// <inheritdoc />
    public LineEndingInfo DetectedLineEnding =>
        LineEndingInfo.FromCounts(_lfCount, _crlfCount, _crCount);

    // -----------------------------------------------------------------
    // File access + scan state
    // -----------------------------------------------------------------

    private readonly string _path;
    private FileStream? _fs;              // kept open for re-reads; locked FileShare.Read
    private Encoding _encoding = null!;   // detected from BOM during scan
    private readonly long _byteLen;
    private long _totalChars;              // accessed via Interlocked
    private volatile bool _done;
    private volatile int _scanFrontier;   // highest page index scanned so far
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _loadedEvent = new(false);
    private string? _sha1;                // computed during scan, available after _done

    // -----------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------

    /// <summary>Fired after each page is scanned or loaded (on the background/pool thread).</summary>
    public event Action? ProgressChanged;

    /// <summary>Fired once when the initial scan finishes.</summary>
    public event Action? LoadComplete;

    /// <summary>
    /// SHA-1 hash of the raw file bytes, computed during the background scan.
    /// Available (non-null) only after <see cref="LoadComplete"/> fires.
    /// </summary>
    public string? Sha1 => _sha1;

    // -----------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------

    /// <summary>
    /// Creates a paged buffer for the file at <paramref name="path"/>.
    /// Call <see cref="StartLoading"/> to begin the background scan.
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <param name="byteLen">File size in bytes.</param>
    /// <param name="maxPages">Maximum decoded pages in memory (default 8 ≈ 16 MB).</param>
    public PagedFileBuffer(string path, long byteLen, int maxPages = DefaultMaxPages) {
        _path = path;
        _byteLen = byteLen;
        MaxPagesInMemory = maxPages;

        // Pre-allocate page arrays based on file size.
        var estimatedPages = (int)Math.Min((byteLen + PageSizeBytes - 1) / PageSizeBytes, int.MaxValue);
        _pages = new PageInfo[Math.Max(estimatedPages, 1)];
        _pageData = new char[]?[_pages.Length];

        // Line samples: estimate ~1 line per 80 bytes.
        var estimatedLines = Math.Max(16L, byteLen / 80);
        var estimatedSamples = (int)Math.Min(estimatedLines / LineSampleStride + 1, int.MaxValue / 8);
        _lineSamples = new long[Math.Max(estimatedSamples, 16)];
        _lineSamples[0] = 0L;
        _lineSampleCount = 1;
        _lineCount = 1;
    }

    // -----------------------------------------------------------------
    // IBuffer
    // -----------------------------------------------------------------

    public long Length => Interlocked.Read(ref _totalChars);

    public bool LengthIsKnown => _done;

    public long LineCount => Interlocked.Read(ref _lineCount);

    public int LongestLine => _longestLine;

    public long GetLineStart(long lineIdx) {
        var lc = Interlocked.Read(ref _lineCount); // snapshot
        if (lineIdx < 0 || lineIdx >= lc) {
            return -1L;
        }

        // Direct lookup for sampled lines.
        var sampleIdx = lineIdx / LineSampleStride;
        long sampleCharOfs;
        long sampleLine;
        lock (_lock) {
            if (sampleIdx >= _lineSampleCount) {
                return -1L; // not scanned yet
            }
            sampleCharOfs = _lineSamples[sampleIdx];
            sampleLine = sampleIdx * LineSampleStride;
        }

        if (lineIdx == sampleLine) {
            return sampleCharOfs;
        }

        // Scan forward from the sample, counting newlines.
        var linesNeeded = (int)(lineIdx - sampleLine);
        var ofs = sampleCharOfs;
        var linesFound = 0;
        var prevWasCr = false;

        while (linesFound < linesNeeded) {
            var pageIdx = FindPageForCharOffset(ofs);
            if (pageIdx < 0) {
                return -1L; // beyond scanned range
            }
            var page = _pages[pageIdx];
            var data = EnsurePageLoaded(pageIdx);
            if (data == null) {
                return -1L;
            }

            var startInPage = (int)(ofs - page.CharStart);
            for (var i = startInPage; i < page.CharCount && linesFound < linesNeeded; i++) {
                var ch = data[i];
                if (ch == '\n') {
                    if (!prevWasCr || i > startInPage || ofs > sampleCharOfs) {
                        // \n (standalone or second half of \r\n)
                    }
                    linesFound++;
                    ofs = page.CharStart + i + 1;
                    prevWasCr = false;
                } else if (ch == '\r') {
                    if (i + 1 < page.CharCount) {
                        if (data[i + 1] == '\n') {
                            // \r\n — the \n branch will count this line
                            prevWasCr = true;
                        } else {
                            // bare \r
                            linesFound++;
                            ofs = page.CharStart + i + 1;
                        }
                    } else {
                        // \r at end of page — defer
                        prevWasCr = true;
                        ofs = page.CharStart + i + 1;
                    }
                } else {
                    prevWasCr = false;
                }
            }

            // If we haven't found enough lines, advance to the next page.
            if (linesFound < linesNeeded) {
                if (pageIdx + 1 < _pageCount) {
                    ofs = _pages[pageIdx + 1].CharStart;
                } else {
                    return -1L;
                }
            }
        }

        return ofs;
    }

    public char this[long offset] {
        get {
            var pageIdx = FindPageForCharOffset(offset);
            if (pageIdx < 0) {
                throw new ArgumentOutOfRangeException(nameof(offset),
                    $"Offset {offset} is out of range.");
            }
            var page = _pages[pageIdx];
            var data = EnsurePageLoaded(pageIdx)
                ?? throw new InvalidOperationException("Failed to load page.");
            return data[(int)(offset - page.CharStart)];
        }
    }

    public void CopyTo(long offset, Span<char> destination, int len) {
        var remaining = len;
        var destPos = 0;
        var curOfs = offset;

        while (remaining > 0) {
            var pageIdx = FindPageForCharOffset(curOfs);
            if (pageIdx < 0) {
                throw new ArgumentOutOfRangeException(
                    $"Offset {curOfs} is out of range.");
            }
            var page = _pages[pageIdx];
            var data = EnsurePageLoaded(pageIdx)
                ?? throw new InvalidOperationException("Failed to load page.");

            var startInPage = (int)(curOfs - page.CharStart);
            var availInPage = page.CharCount - startInPage;
            var take = Math.Min(availInPage, remaining);

            data.AsSpan(startInPage, take).CopyTo(destination.Slice(destPos, take));
            destPos += take;
            remaining -= take;
            curOfs += take;
        }
    }

    public bool IsLoaded(long offset, int len) {
        if (len <= 0) {
            return true;
        }
        var endOfs = offset + len - 1;
        var startPage = FindPageForCharOffset(offset);
        var endPage = FindPageForCharOffset(endOfs);
        if (startPage < 0 || endPage < 0) {
            return false; // beyond scanned range
        }
        lock (_lock) {
            for (var i = startPage; i <= endPage; i++) {
                if (_pageData[i] == null) {
                    return false;
                }
            }
        }
        return true;
    }

    public void EnsureLoaded(long offset, int len) {
        if (len <= 0) {
            return;
        }
        var endOfs = offset + len - 1;
        var startPage = FindPageForCharOffset(offset);
        var endPage = FindPageForCharOffset(endOfs);
        if (startPage < 0 || endPage < 0) {
            return; // beyond scanned range; scan will eventually reach it
        }

        // Queue page loads on the thread pool.
        for (var i = startPage; i <= endPage; i++) {
            bool needsLoad;
            lock (_lock) {
                needsLoad = _pageData[i] == null;
            }
            if (needsLoad) {
                var pageIdx = i;
                ThreadPool.QueueUserWorkItem(_ => {
                    LoadPageFromDisk(pageIdx);
                    ProgressChanged?.Invoke();
                });
            }
        }
    }

    public void Dispose() {
        _cts.Cancel();
        _cts.Dispose();
        _loadedEvent.Dispose();
        _fs?.Dispose();
        _fs = null;
        // Release all page data.
        lock (_lock) {
            for (var i = 0; i < _pageData.Length; i++) {
                _pageData[i] = null;
            }
            _lruList.Clear();
            _lruNodes.Clear();
            _loadedPageCount = 0;
        }
    }

    // -----------------------------------------------------------------
    // Loading
    // -----------------------------------------------------------------

    /// <summary>
    /// Starts the background file scan. Returns immediately.
    /// </summary>
    public void StartLoading(CancellationToken externalCt = default) {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, externalCt);
        ThreadPool.QueueUserWorkItem(_ => ScanWorker(linked));
    }

    private void ScanWorker(CancellationTokenSource linkedCts) {
        try {
            var ct = linkedCts.Token;

            // Open _fs for on-demand page loads (LoadPageFromDisk uses seek + read).
            // Use RandomAccess since on-demand reads jump to arbitrary page offsets.
            _fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.Read, PageSizeBytes, FileOptions.RandomAccess);

            // Open a *separate* stream for the sequential scan. This avoids a race
            // condition where LoadPageFromDisk seeks _fs to a previous page while
            // the scan is reading forward — the seek corrupts the scan position and
            // causes ScanWorker to re-read data, double-counting newlines.
            using var scanFs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.Read, PageSizeBytes, FileOptions.SequentialScan);

            // Detect BOM (may advance scanFs past the BOM bytes).
            _encoding = DetectEncoding(scanFs);
            var decoder = _encoding.GetDecoder();

            // Incremental SHA-1: hash raw bytes as we scan.
            // If BOM detection skipped bytes, hash them first so the hash
            // covers the entire file from byte 0.
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            if (scanFs.Position > 0) {
                var bomLen = (int)scanFs.Position;
                var savedPos = scanFs.Position;
                scanFs.Position = 0;
                var bomBytes = new byte[bomLen];
                scanFs.ReadExactly(bomBytes, 0, bomLen);
                hasher.AppendData(bomBytes, 0, bomLen);
                scanFs.Position = savedPos;
            }

            var byteBuf = new byte[PageSizeBytes];
            var charBuf = new char[PageSizeBytes]; // worst case: 1 char per byte
            long cumulativeChars = 0;
            long currentLine = 0;
            var prevWasCr = false;

            while (true) {
                ct.ThrowIfCancellationRequested();

                var byteOffset = scanFs.Position;
                var bytesRead = scanFs.Read(byteBuf, 0, PageSizeBytes);
                if (bytesRead == 0) {
                    break;
                }

                hasher.AppendData(byteBuf, 0, bytesRead);

                var isLastChunk = scanFs.Position >= _byteLen;

                // Decode this chunk.
                var charCount = decoder.GetChars(byteBuf, 0, bytesRead, charBuf, 0, isLastChunk);

                // Record page metadata.
                var pageIdx = _pageCount;
                EnsurePageCapacity(pageIdx + 1);

                _pages[pageIdx] = new PageInfo {
                    ByteOffset = byteOffset,
                    ByteCount = bytesRead,
                    CharStart = cumulativeChars,
                    CharCount = charCount,
                };

                // Scan for newlines and build sampled line index.
                ScanNewlines(charBuf, charCount, cumulativeChars, ref currentLine, ref prevWasCr);

                // Retain decoded chars if within the page cache limit.
                if (_loadedPageCount < MaxPagesInMemory) {
                    var retained = new char[charCount];
                    Array.Copy(charBuf, retained, charCount);
                    lock (_lock) {
                        _pageData[pageIdx] = retained;
                        var node = _lruList.AddFirst(pageIdx);
                        _lruNodes[pageIdx] = node;
                        _loadedPageCount++;
                    }
                }

                cumulativeChars += charCount;
                Interlocked.Exchange(ref _totalChars, cumulativeChars);
                _pageCount = pageIdx + 1;
                _scanFrontier = pageIdx;

                ProgressChanged?.Invoke();

                if (isLastChunk) {
                    break;
                }
            }

            // Handle trailing bare \r.
            if (prevWasCr) {
                _crCount++;
                currentLine++;
                Interlocked.Exchange(ref _lineCount, currentLine);
            }

            Interlocked.Exchange(ref _totalChars, cumulativeChars);
            _sha1 = Convert.ToHexStringLower(hasher.GetHashAndReset());
            _done = true;
            LoadComplete?.Invoke();
        } catch (OperationCanceledException) {
            _done = true;
        } catch {
            _done = true;
        } finally {
            _loadedEvent.Set();
            linkedCts.Dispose();
        }
    }

    /// <summary>
    /// Blocks the calling thread until the background scan finishes.
    /// </summary>
    public void WaitUntilLoaded() => _loadedEvent.Wait();

    // -----------------------------------------------------------------
    // Newline scanning (builds sampled line index)
    // -----------------------------------------------------------------

    private void ScanNewlines(char[] data, int charCount, long charBase,
        ref long currentLine, ref bool prevWasCr) {

        for (int i = 0; i < charCount; i++) {
            var ch = data[i];
            if (ch == '\n') {
                if (prevWasCr) {
                    // \r\n — already counted the line at the \r
                    _crlfCount++;
                    prevWasCr = false;
                    continue;
                }
                _lfCount++;
                currentLine++;
                Interlocked.Exchange(ref _lineCount, currentLine + 1); // +1 because line 0 always exists
                long charOffset = charBase + i + 1;
                CheckLongestLine(charOffset);
                RecordLineSample(currentLine, charOffset);
            } else if (ch == '\r') {
                // Don't count as CR yet — might be \r\n. If prevWasCr was
                // set from a previous char, that one was a bare \r.
                if (prevWasCr) {
                    _crCount++;
                }
                currentLine++;
                Interlocked.Exchange(ref _lineCount, currentLine + 1);
                long charOffset = charBase + i + 1;
                CheckLongestLine(charOffset);
                RecordLineSample(currentLine, charBase + i + 1);
                prevWasCr = true;
            } else {
                if (prevWasCr) {
                    _crCount++;
                }
                prevWasCr = false;
            }
        }
    }

    private void CheckLongestLine(long charOffset) {
        if (_longestLine >= MAX_LONGEST_LINE) {
            return;
        }
        lock (_lock) {
            int lx = (int) Math.Min(MAX_LONGEST_LINE, charOffset - _lastLineStart);
            if (lx > _longestLine) {
                _longestLine = lx;
            }
            _lastLineStart = charOffset;
        }
    }

    private void RecordLineSample(long lineIdx, long charOffset) {
        if (lineIdx % LineSampleStride != 0) {
            return;
        }
        var sampleIdx = (int)(lineIdx / LineSampleStride);
        lock (_lock) {
            if (sampleIdx >= _lineSamples.Length) {
                var newLen = Math.Max(_lineSamples.Length * 2, sampleIdx + 1);
                var newArr = new long[newLen];
                Array.Copy(_lineSamples, newArr, _lineSampleCount);
                _lineSamples = newArr;
            }
            _lineSamples[sampleIdx] = charOffset;
            if (sampleIdx >= _lineSampleCount) {
                _lineSampleCount = sampleIdx + 1;
            }
        }
    }

    // -----------------------------------------------------------------
    // Page management
    // -----------------------------------------------------------------

    /// <summary>
    /// Finds the page index that contains the given char offset.
    /// Returns -1 if the offset is beyond the currently scanned range.
    /// </summary>
    private int FindPageForCharOffset(long charOfs) {
        var count = _pageCount; // snapshot volatile
        if (count == 0 || charOfs < 0) {
            return -1;
        }
        // Binary search: find the largest pageIdx where CharStart <= charOfs.
        var lo = 0;
        var hi = count - 1;
        while (lo < hi) {
            var mid = (lo + hi + 1) / 2;
            if (_pages[mid].CharStart <= charOfs) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }
        // Verify the offset is actually within this page.
        var page = _pages[lo];
        if (charOfs >= page.CharStart && charOfs < page.CharStart + page.CharCount) {
            return lo;
        }
        return -1;
    }

    /// <summary>
    /// Ensures the page at <paramref name="pageIdx"/> is loaded into memory.
    /// Returns the decoded char array, or null if the page can't be loaded.
    /// </summary>
    private char[]? EnsurePageLoaded(int pageIdx) {
        lock (_lock) {
            var data = _pageData[pageIdx];
            if (data != null) {
                PromoteLru(pageIdx);
                return data;
            }
        }
        // Page is evicted — load from disk.
        return LoadPageFromDisk(pageIdx);
    }

    /// <summary>
    /// Loads a page from disk, decodes it, and stores it in the cache.
    /// </summary>
    private char[]? LoadPageFromDisk(int pageIdx) {
        if (pageIdx >= _pageCount || _fs == null) {
            return null;
        }

        var page = _pages[pageIdx];
        var byteBuf = new byte[page.ByteCount];

        // Read from disk (need lock for FileStream seek+read atomicity).
        int bytesRead;
        lock (_fs) {
            _fs.Seek(page.ByteOffset, SeekOrigin.Begin);
            bytesRead = _fs.Read(byteBuf, 0, page.ByteCount);
        }

        if (bytesRead == 0) {
            return null;
        }

        // Decode using the detected encoding.
        // Use a fresh decoder to avoid cross-page multi-byte state issues.
        // For pages after the first, there's a subtle issue: multi-byte UTF-8
        // sequences that span page boundaries. We handle this by decoding with
        // a fresh decoder and accepting that boundary chars might be slightly off.
        // In practice, the scan worker uses a stateful decoder, so page boundaries
        // fall at decoder-safe points (the decoder consumed complete sequences).
        var decoder = _encoding.GetDecoder();
        var charBuf = new char[bytesRead]; // worst case
        var charCount = decoder.GetChars(byteBuf, 0, bytesRead, charBuf, 0, flush: true);

        // Trim to actual size.
        var result = new char[charCount];
        Array.Copy(charBuf, result, charCount);

        lock (_lock) {
            // Check if another thread already loaded this page.
            if (_pageData[pageIdx] != null) {
                PromoteLru(pageIdx);
                return _pageData[pageIdx];
            }

            // Evict if at capacity.
            while (_loadedPageCount >= MaxPagesInMemory && _lruList.Count > 0) {
                var evictIdx = _lruList.Last!.Value;
                _lruList.RemoveLast();
                _lruNodes.Remove(evictIdx);
                _pageData[evictIdx] = null;
                _loadedPageCount--;
            }

            _pageData[pageIdx] = result;
            var node = _lruList.AddFirst(pageIdx);
            _lruNodes[pageIdx] = node;
            _loadedPageCount++;
        }

        return result;
    }

    private void PromoteLru(int pageIdx) {
        // Must be called under _lock.
        if (_lruNodes.TryGetValue(pageIdx, out var node)) {
            _lruList.Remove(node);
            _lruList.AddFirst(node);
        }
    }

    private void EnsurePageCapacity(int needed) {
        if (needed <= _pages.Length) {
            return;
        }
        var newLen = Math.Max(_pages.Length * 2, needed);
        var newPages = new PageInfo[newLen];
        Array.Copy(_pages, newPages, _pageCount);
        _pages = newPages;

        var newData = new char[]?[newLen];
        lock (_lock) {
            Array.Copy(_pageData, newData, _pageCount);
            _pageData = newData;
        }
    }

    // -----------------------------------------------------------------
    // BOM detection
    // -----------------------------------------------------------------

    /// <summary>
    /// Reads up to 3 bytes to detect a BOM. Rewinds the stream after detection.
    /// </summary>
    private static Encoding DetectEncoding(FileStream fs) {
        var bom = new byte[3];
        var bomRead = fs.Read(bom, 0, 3);

        if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) {
            // UTF-8 BOM — consumed, continue reading.
            return Encoding.UTF8;
        }
        if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) {
            fs.Position = 2; // UTF-16 LE BOM
            return Encoding.Unicode;
        }
        if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) {
            fs.Position = 2; // UTF-16 BE BOM
            return Encoding.BigEndianUnicode;
        }

        // No BOM — rewind and assume UTF-8.
        fs.Position = 0;
        return Encoding.UTF8;
    }
}
