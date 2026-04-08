using System.IO.Compression;
using System.Text;
using DMEdit.Core.Buffers;
using DMEdit.Core.Documents;
using DMEdit.Core.IO;

namespace DMEdit.Core.Tests;

/// <summary>
/// Tests for <see cref="StreamingFileBuffer"/> — the streaming buffer used for
/// decompressed zip entries.
/// </summary>
public class StreamingFileBufferTests : IDisposable {
    private readonly List<string> _tempFiles = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose() {
        foreach (var d in _disposables) {
            try { d.Dispose(); } catch { }
        }
        foreach (var path in _tempFiles) {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    private string TempPath(string ext = ".zip") {
        var path = Path.Combine(Path.GetTempPath(), $"sfb_test_{Guid.NewGuid():N}{ext}");
        _tempFiles.Add(path);
        return path;
    }

    // UTF-8 without BOM — keeps assertions on byte counts predictable.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static string CreateSingleEntryZip(string zipPath, string entryName, string content) {
        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Utf8NoBom);
        writer.Write(content);
        return zipPath;
    }

    // -----------------------------------------------------------------
    // LongestLine — the entry-22 crash repro
    // -----------------------------------------------------------------

    /// <summary>
    /// Regression: a zipped file containing a single multi-MB line must report
    /// a truthful <see cref="StreamingFileBuffer.LongestLine"/>.  When it lies
    /// (e.g. hardcoded 10_000), the CharWrap-mode decision downstream sees a
    /// small-line buffer and feeds the full line into the TextLayout slow path
    /// — the same path that caused the entry 22 crash.
    /// </summary>
    [Fact]
    public async Task LongestLine_SingleHugeLine_ReportsActualLength() {
        var path = TempPath();
        // 2 MB of 'x' on a single line — well above both the 10_000 lie and
        // the 1_000_000-char MaxGetTextLength ceiling.
        var content = new string('x', 2_000_000);
        CreateSingleEntryZip(path, "giant.json", content);

        var result = await FileLoader.LoadAsync(path);
        await result.Loaded;
        _disposables.Add(result.Document.Table.Buffer);

        var buf = (StreamingFileBuffer)result.Document.Table.Buffer;

        Assert.Equal(2_000_000, buf.Length);
        // The buffer must NOT claim a longest line smaller than reality.
        // Acceptable answers: the true length (2_000_000), or -1 (unknown).
        Assert.True(
            buf.LongestLine == -1 || buf.LongestLine >= 2_000_000,
            $"StreamingFileBuffer.LongestLine lied: reported {buf.LongestLine} " +
            $"for a single-line file of length 2_000_000. " +
            $"Downstream CharWrap trigger will miss this and feed the line " +
            $"into the TextLayout slow path.");
    }

    /// <summary>
    /// Regression: <see cref="PieceTable.MaxLineLength"/> consults
    /// <c>Buffer.LongestLine</c> before the line tree is built.  If the buffer
    /// lies, the piece-table also lies, and the CharWrap trigger will miss the
    /// giant line.
    /// </summary>
    [Fact]
    public async Task PieceTable_MaxLineLength_BeforeLineTreeBuilt_ReflectsReality() {
        var path = TempPath();
        var content = new string('x', 2_000_000);
        CreateSingleEntryZip(path, "giant.json", content);

        var result = await FileLoader.LoadAsync(path);
        await result.Loaded;
        _disposables.Add(result.Document.Table.Buffer);

        var table = result.Document.Table;

        // Without touching anything that forces the line tree to build, ask
        // about MaxLineLength — this is the property the CharWrap trigger
        // would consult.  If it returns the 10_000 lie, the trigger misses.
        var reported = table.MaxLineLength;
        Assert.True(
            reported == -1 || reported >= 2_000_000,
            $"PieceTable.MaxLineLength reported {reported} for a 2_000_000-char " +
            $"single-line buffer.  This would cause ShouldCharWrap to miss the " +
            $"giant line and reproduce the entry 22 TextLayout crash.");
    }

    /// <summary>
    /// Multi-line content with one outlier: a normal log file with a single
    /// huge embedded JSON line.  LongestLine should reflect the outlier, not
    /// the average line length.
    /// </summary>
    [Fact]
    public async Task LongestLine_MultilineWithOneLongLine_TracksOutlier() {
        var path = TempPath();
        var sb = new StringBuilder();
        sb.AppendLine("short line 1");
        sb.AppendLine("short line 2");
        sb.Append(new string('y', 50_000));
        sb.Append('\n');
        sb.AppendLine("short line 3");
        var content = sb.ToString();
        CreateSingleEntryZip(path, "mixed.log", content);

        var result = await FileLoader.LoadAsync(path);
        await result.Loaded;
        _disposables.Add(result.Document.Table.Buffer);

        var buf = (StreamingFileBuffer)result.Document.Table.Buffer;
        // 50_000 'y' chars + the trailing '\n' (terminator counts in length)
        // = 50_001.  Allow a little slack for cross-platform line-ending oddities.
        Assert.True(buf.LongestLine >= 50_000,
            $"Expected LongestLine ≥ 50_000 (the outlier line), got {buf.LongestLine}.");
    }

    /// <summary>
    /// Trailing unterminated line: file ends mid-line with no final newline.
    /// The end-of-scan finalization must measure that line.
    /// </summary>
    [Fact]
    public async Task LongestLine_TrailingUnterminatedLine_IsMeasured() {
        var path = TempPath();
        // No trailing newline — the long line is the very last thing in the file.
        var content = "short\n" + new string('z', 100_000);
        CreateSingleEntryZip(path, "trailing.txt", content);

        var result = await FileLoader.LoadAsync(path);
        await result.Loaded;
        _disposables.Add(result.Document.Table.Buffer);

        var buf = (StreamingFileBuffer)result.Document.Table.Buffer;
        Assert.True(buf.LongestLine >= 100_000,
            $"Trailing unterminated 100_000-char line was not measured: " +
            $"LongestLine reported {buf.LongestLine}.");
    }

    /// <summary>
    /// Empty zip entry: zero lines, longest-line should be 0 (not negative,
    /// not the old 10_000 lie).
    /// </summary>
    [Fact]
    public async Task LongestLine_EmptyContent_IsZero() {
        var path = TempPath();
        CreateSingleEntryZip(path, "empty.txt", "");

        var result = await FileLoader.LoadAsync(path);
        await result.Loaded;
        _disposables.Add(result.Document.Table.Buffer);

        var buf = (StreamingFileBuffer)result.Document.Table.Buffer;
        Assert.Equal(0, buf.Length);
        Assert.Equal(0, buf.LongestLine);
    }

    /// <summary>
    /// <see cref="StreamingFileBuffer.ByteLength"/> exposes the estimated
    /// uncompressed size — used by the ShouldCharWrap size gate so the
    /// trigger no longer requires a PagedFileBuffer cast.
    /// </summary>
    [Fact]
    public async Task ByteLength_ReportsUncompressedSize() {
        var path = TempPath();
        var content = new string('a', 100_000);
        CreateSingleEntryZip(path, "size.txt", content);

        var result = await FileLoader.LoadAsync(path);
        await result.Loaded;
        _disposables.Add(result.Document.Table.Buffer);

        var buf = (StreamingFileBuffer)result.Document.Table.Buffer;
        // ZipArchive reports entry.Length (uncompressed), so ByteLength should
        // match the original content size.
        Assert.Equal(100_000, buf.ByteLength);
    }

    // -----------------------------------------------------------------
    // LoadResult.Buffer wiring (regression for the second bug found while
    // tracing the partial-piece race — LoadZip used to leave Buffer null,
    // breaking the buffer-typed ShouldCharWrap check).
    // -----------------------------------------------------------------

    /// <summary>
    /// <see cref="LoadResult.Buffer"/> must be set for zip-loaded files so
    /// downstream code (e.g. <c>ShouldCharWrap</c>) can inspect the buffer
    /// without reaching through <c>Document.Table.Buffer</c>.
    /// </summary>
    [Fact]
    public async Task LoadResult_Buffer_IsSetForZipLoad() {
        var path = TempPath();
        CreateSingleEntryZip(path, "x.txt", "hello");

        var result = await FileLoader.LoadAsync(path);
        await result.Loaded;
        _disposables.Add(result.Document.Table.Buffer);

        Assert.NotNull(result.Buffer);
        Assert.IsType<StreamingFileBuffer>(result.Buffer);
        // And it's the SAME instance the document is built on.
        Assert.Same(result.Document.Table.Buffer, result.Buffer);
    }

    /// <summary>
    /// Same regression check for the paged path — guards against future
    /// refactors regressing what already worked.
    /// </summary>
    [Fact]
    public async Task LoadResult_Buffer_IsSetForPagedLoad() {
        var path = Path.Combine(Path.GetTempPath(), $"sfb_paged_{Guid.NewGuid():N}.txt");
        _tempFiles.Add(path);
        File.WriteAllText(path, "plain text\nwith two lines\n");

        var result = await FileLoader.LoadAsync(path);
        await result.Loaded;
        _disposables.Add(result.Document.Table.Buffer);

        Assert.NotNull(result.Buffer);
        Assert.Same(result.Document.Table.Buffer, result.Buffer);
    }

    // -----------------------------------------------------------------
    // PieceTable.ReconcileInitialPiece (extracted from InstallLineTree)
    // -----------------------------------------------------------------

    /// <summary>
    /// After loading completes, the document length must reflect the FULL
    /// buffer length, not whatever partial value <see cref="PieceTable.EnsureInitialPiece"/>
    /// happened to capture during the scan.  This is the deterministic check
    /// for the partial-piece reconciliation: the LoadComplete handler in
    /// FileLoader must call ReconcileInitialPiece.
    /// </summary>
    [Fact]
    public async Task PostLoad_DocumentLength_MatchesFullContent() {
        var path = TempPath();
        // Use ~1 MB to make sure the buffer goes through multiple chunks and
        // the BOM-prefetch path completes well before the main loop.
        var content = new string('q', 1_000_000);
        CreateSingleEntryZip(path, "big.txt", content);

        var result = await FileLoader.LoadAsync(path);
        await result.Loaded;
        _disposables.Add(result.Document.Table.Buffer);

        Assert.Equal(1_000_000, result.Document.Table.Length);
        Assert.Equal(content.Length, result.Document.Table.GetText().Length);
    }

    /// <summary>
    /// Direct unit test for <see cref="PieceTable.ReconcileInitialPiece"/>:
    /// simulates the race by manually creating a PieceTable over a partial
    /// buffer, forcing EnsureInitialPiece (via Length), then "growing" the
    /// buffer and calling Reconcile.  The piece-table must adopt the new
    /// length.
    /// </summary>
    [Fact]
    public void ReconcileInitialPiece_AfterPartialEnsure_AdoptsFullLength() {
        // GrowingBuffer mimics a streaming buffer whose Length grows over time.
        var grow = new GrowingBuffer();
        grow.SetCurrentLength(3); // partial: only 3 chars decoded so far
        var table = new PieceTable(grow);

        // Force EnsureInitialPiece to commit the piece at the partial length.
        var partialLen = table.Length;
        Assert.Equal(3, partialLen);

        // Buffer "finishes loading" — full length is now 1000.
        grow.SetCurrentLength(1000);

        // Without reconciliation the piece-table is still locked at 3.
        Assert.Equal(3, table.Length);

        // Reconcile and re-check.
        table.ReconcileInitialPiece();
        Assert.Equal(1000, table.Length);
    }

    /// <summary>
    /// A minimal IBuffer that simulates a streaming buffer whose Length
    /// increases over time.  Only the members exercised by the test are
    /// implemented; others throw if hit.
    /// </summary>
    private sealed class GrowingBuffer : IBuffer {
        private long _len;
        public void SetCurrentLength(long len) => _len = len;
        public long Length => _len;
        public bool LengthIsKnown => false; // forces deferred initial piece
        public char this[long offset] => 'a';
        public void CopyTo(long offset, Span<char> destination, int len) {
            destination[..len].Fill('a');
        }
        public void Dispose() { }
    }
}
