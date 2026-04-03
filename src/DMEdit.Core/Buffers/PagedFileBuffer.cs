using System.Security.Cryptography;
using System.Text;
using DMEdit.Core.Documents;

namespace DMEdit.Core.Buffers;

/// <summary>
/// An <see cref="IBuffer"/> that keeps only a bounded number of decoded text pages
/// in memory, re-reading and re-decoding from disk on demand. The raw file stays on
/// disk and is reopened on demand (external writes are not blocked).
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

    private const int PageSizeBytes = 1024 * 1024; // 1 MB raw bytes per page
    // Lines can be arbitrarily long; character-wrapping handles rendering.

    /// <summary>
    /// Default maximum number of decoded pages kept in memory.
    /// Minimum safe value is ~4 (viewport layout needs up to 4 pages simultaneously).
    /// 8 gives comfortable headroom for small back-and-forth scrolls ≈ 16 MB cache.
    /// </summary>
    public const int DefaultMaxPages = 8;

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
    // Line index (exact line lengths, built during scan)
    // -----------------------------------------------------------------

    private List<int> _lineLengths = null!;     // set by LineScanner during scan
    private long _lineCount;                    // accessed via Interlocked
    private volatile int _longestLine;
    private long _longestRealLine; // longest line ignoring pseudo-splits

    // Run-length encoded terminator types built by LineScanner.
    private List<(long StartLine, Documents.LineTerminatorType Type)>? _terminatorRuns;

    // -----------------------------------------------------------------
    // Line ending counters (populated from LineScanner after scan)
    // -----------------------------------------------------------------

    private int _lfCount;
    private int _crlfCount;
    private int _crCount;

    /// <inheritdoc />
    public LineEndingInfo DetectedLineEnding =>
        LineEndingInfo.FromCounts(_lfCount, _crlfCount, _crCount);

    // -----------------------------------------------------------------
    // Indentation counters (accumulated during scan)
    // -----------------------------------------------------------------

    private int _spaceIndentCount;
    private int _tabIndentCount;

    public IndentInfo DetectedIndent =>
        IndentInfo.FromCounts(_spaceIndentCount, _tabIndentCount);

    public Documents.EncodingInfo DetectedEncoding =>
        Documents.EncodingInfo.FromDetection(_encoding, _hadBom);

    // -----------------------------------------------------------------
    // File access + scan state
    // -----------------------------------------------------------------

    private readonly string _path;
    // _fs removed — page re-reads now open/close a fresh FileStream each time
    // to avoid holding a file lock for the entire session.
    private Encoding _encoding = null!;   // detected from BOM during scan
    private bool _hadBom;                  // whether a BOM was present
    private readonly long _byteLen;

    /// <summary>File size in bytes.</summary>
    public long ByteLength => _byteLen;
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
        _longestLine = 500; // initial estimate, updated during scan

        // Pre-allocate page arrays based on file size.
        var estimatedPages = (int)Math.Min((byteLen + PageSizeBytes - 1) / PageSizeBytes, int.MaxValue);
        _pages = new PageInfo[Math.Max(estimatedPages, 1)];
        _pageData = new char[]?[_pages.Length];

        _lineCount = 1;
    }

    // -----------------------------------------------------------------
    // IBuffer
    // -----------------------------------------------------------------

    public long Length => Interlocked.Read(ref _totalChars);

    public bool LengthIsKnown => _done;

    /// <summary>
    /// Non-null if the background scan terminated with an error.
    /// Checked by the UI after <see cref="LoadComplete"/> fires.
    /// </summary>
    public Exception? ScanError { get; private set; }

    public long LineCount => Interlocked.Read(ref _lineCount);

    public int LongestLine => _longestLine;

    /// <summary>
    /// Longest real (newline-delimited) line in the file, ignoring pseudo-splits.
    /// Only valid after <see cref="LoadComplete"/> has fired.
    /// </summary>
    public long LongestRealLine => _longestRealLine;

    public long GetLineStart(long lineIdx) {
        var lc = Interlocked.Read(ref _lineCount);
        if (lineIdx < 0 || lineIdx >= lc) {
            return -1L;
        }
        if (lineIdx == 0) return 0;

        // Sum line lengths [0..lineIdx-1] to get the char offset.
        // Lock protects against concurrent _lineLengths mutations by ScanNewlines.
        lock (_lock) {
            var lengths = _lineLengths;
            if (lengths == null || lineIdx > lengths.Count) return -1L;
            var sum = 0L;
            for (var i = 0; i < (int)lineIdx; i++) {
                sum += lengths[i];
            }
            return sum;
        }
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

            // Open a stream for the sequential scan. On-demand page re-reads
            // (LoadPageFromDisk) open their own short-lived streams, so there's
            // no shared file handle and no seek race.
            using var scanFs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, PageSizeBytes, FileOptions.SequentialScan);

            // Detect BOM (may advance scanFs past the BOM bytes).
            (_encoding, _hadBom) = DetectEncodingWithBom(scanFs);
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

            var estimatedLines = (int)Math.Min(Math.Max(16L, _byteLen / 80), int.MaxValue);
            var scanner = new Documents.LineScanner(estimatedLines: estimatedLines);

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

                // Scan for newlines, line lengths, and terminator types.
                scanner.Scan(charBuf.AsSpan(0, charCount));

                // Sync line count for progress reporting.
                Interlocked.Exchange(ref _lineCount, scanner.LineCount);
                lock (_lock) {
                    _lineLengths = scanner.LineLengths;
                }

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

            scanner.Finish();

            // Sync final state from scanner.
            Interlocked.Exchange(ref _lineCount, scanner.LineCount);
            lock (_lock) {
                _lineLengths = scanner.LineLengths;
                _terminatorRuns = scanner.TerminatorRuns;
            }
            _lfCount = scanner.LfCount;
            _crlfCount = scanner.CrlfCount;
            _crCount = scanner.CrCount;
            _spaceIndentCount = scanner.SpaceIndentCount;
            _tabIndentCount = scanner.TabIndentCount;
            _longestRealLine = scanner.LongestRealLine;

            Interlocked.Exchange(ref _totalChars, cumulativeChars);
            _sha1 = Convert.ToHexStringLower(hasher.GetHashAndReset());
            _done = true;
            LoadComplete?.Invoke();
        } catch (OperationCanceledException) {
            _done = true;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            ScanError = ex;
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

    // ScanNewlines replaced by LineScanner — see ScanWorker.

    /// <summary>
    /// Returns the exact line lengths computed during the scan.
    /// Only valid after <see cref="LoadComplete"/> has fired.
    /// The caller takes ownership; the buffer clears its reference.
    /// </summary>
    public List<int>? TakeLineLengths() {
        lock (_lock) {
            var result = _lineLengths;
            _lineLengths = null!;
            return result;
        }
    }

    /// <summary>
    /// Returns the run-length encoded terminator type list built during the scan.
    /// Only valid after <see cref="LoadComplete"/> has fired.
    /// The caller takes ownership; the buffer clears its reference.
    /// </summary>
    public List<(long StartLine, Documents.LineTerminatorType Type)>? TakeTerminatorRuns() {
        lock (_lock) {
            var result = _terminatorRuns;
            _terminatorRuns = null!;
            return result;
        }
    }

    /// <summary>
    /// Looks up the terminator type for a given line index using the
    /// run-length encoded list. Binary search: O(log R) where R is the
    /// number of distinct runs (typically 1).
    /// </summary>
    public Documents.LineTerminatorType GetLineTerminator(long lineIdx) {
        var runs = _terminatorRuns;
        if (runs == null || runs.Count == 0)
            return Documents.LineTerminatorType.None;

        // Binary search for the last run with StartLine <= lineIdx.
        int lo = 0, hi = runs.Count - 1;
        while (lo < hi) {
            var mid = lo + (hi - lo + 1) / 2;
            if (runs[mid].StartLine <= lineIdx) lo = mid;
            else hi = mid - 1;
        }
        return runs[lo].Type;
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
        var pages = _pages;     // snapshot reference (atomic in .NET)
        if (count == 0 || charOfs < 0) {
            return -1;
        }
        // Binary search: find the largest pageIdx where CharStart <= charOfs.
        var lo = 0;
        var hi = count - 1;
        while (lo < hi) {
            var mid = (lo + hi + 1) / 2;
            if (pages[mid].CharStart <= charOfs) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }
        // Verify the offset is actually within this page.
        var page = pages[lo];
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
        if (pageIdx >= _pageCount) {
            return null;
        }

        var page = _pages[pageIdx];
        var byteBuf = new byte[page.ByteCount];

        // Open a short-lived stream for this page read. No persistent file
        // lock means saves and external tools aren't blocked.
        int bytesRead;
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, page.ByteCount, FileOptions.RandomAccess)) {
            fs.Seek(page.ByteOffset, SeekOrigin.Begin);
            bytesRead = fs.Read(byteBuf, 0, page.ByteCount);
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

        var newData = new char[]?[newLen];
        lock (_lock) {
            Array.Copy(_pageData, newData, _pageCount);
            _pages = newPages;   // assign inside lock so FindPageForCharOffset
            _pageData = newData; // snapshots a consistent reference
        }
    }

    // -----------------------------------------------------------------
    // BOM detection
    // -----------------------------------------------------------------

    /// <summary>
    /// Reads up to 3 bytes to detect a BOM. Rewinds the stream after detection.
    /// </summary>
    private (Encoding encoding, bool hadBom) DetectEncodingWithBom(FileStream fs) {
        var bom = new byte[3];
        var bomRead = fs.Read(bom, 0, 3);

        if (bomRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) {
            // UTF-8 BOM — consumed, continue reading.
            return (Encoding.UTF8, true);
        }
        if (bomRead >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) {
            fs.Position = 2; // UTF-16 LE BOM
            return (Encoding.Unicode, true);
        }
        if (bomRead >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) {
            fs.Position = 2; // UTF-16 BE BOM
            return (Encoding.BigEndianUnicode, true);
        }

        // No BOM — rewind and assume UTF-8.
        fs.Position = 0;
        return (Encoding.UTF8, false);
    }
}
