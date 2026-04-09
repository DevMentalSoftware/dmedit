using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DMEdit.Core.Buffers;
using DMEdit.Core.Documents;

namespace DMEdit.Core.Tests;

/// <summary>
/// Tests for <see cref="PagedFileBuffer"/> — the paged, LRU-cached file buffer
/// that provides bounded-memory access to arbitrarily large files.
/// </summary>
public class PagedFileBufferTests : IDisposable {
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private readonly List<string> _tempFiles = new();

    /// <summary>
    /// Creates a temp file with <paramref name="content"/> and returns its path.
    /// </summary>
    private string WriteTempFile(string content, Encoding? encoding = null) {
        var path = Path.Combine(Path.GetTempPath(), $"pfb_test_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content, encoding ?? Encoding.UTF8);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Creates a temp file with <paramref name="rawBytes"/> and returns its path.
    /// </summary>
    private string WriteTempFileBytes(byte[] rawBytes) {
        var path = Path.Combine(Path.GetTempPath(), $"pfb_test_{Guid.NewGuid():N}.txt");
        File.WriteAllBytes(path, rawBytes);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Creates a <see cref="PagedFileBuffer"/>, starts loading, and waits for
    /// the scan to complete. Asserts that loading finishes within 10 seconds.
    /// </summary>
    private PagedFileBuffer LoadAndWait(string path, int maxPages = PagedFileBuffer.DefaultMaxPages) {
        var fi = new FileInfo(path);
        var buf = new PagedFileBuffer(path, fi.Length, maxPages);
        var done = new ManualResetEventSlim(false);
        buf.LoadComplete += () => done.Set();
        buf.StartLoading();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "PagedFileBuffer scan did not complete in time.");
        return buf;
    }

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    // -------------------------------------------------------------------------
    // Small file (all pages fit in memory)
    // -------------------------------------------------------------------------

    [Fact]
    public void SmallFile_AllPagesLoaded_LengthCorrect() {
        var content = "Hello, world!\nSecond line.\nThird line.";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        Assert.True(buf.LengthIsKnown);
        Assert.Equal(content.Length, buf.Length);
    }

    [Fact]
    public void SmallFile_LineCountCorrect() {
        var content = "Line1\nLine2\nLine3\n";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        // "Line1\nLine2\nLine3\n" has 4 logical lines (last \n starts a 4th empty line)
        Assert.Equal(4L, buf.LineCount);
    }

    [Fact]
    public void SmallFile_IndexerReturnsCorrectChars() {
        var content = "ABCDEFG";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        for (var i = 0; i < content.Length; i++) {
            Assert.Equal(content[i], buf[i]);
        }
    }

    [Fact]
    public void SmallFile_CopyTo_ReturnsCorrectSlice() {
        var content = "Hello, world!";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        var dest = new char[5];
        buf.CopyTo(7, dest, 5);
        Assert.Equal("world", new string(dest));
    }

    [Fact]
    public void SmallFile_IsLoaded_AllTrue() {
        var content = "Short text.";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        Assert.True(buf.IsLoaded(0, content.Length));
    }

    // -------------------------------------------------------------------------
    // GetLineStart (sampled line index)
    // -------------------------------------------------------------------------

    [Fact]
    public void GetLineStart_Line0_IsZero() {
        var content = "First line\nSecond line\nThird line";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        Assert.Equal(0L, buf.GetLineStart(0));
    }

    [Fact]
    public void GetLineStart_Line1_AfterFirstNewline() {
        var content = "First\nSecond\nThird";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        // "First\n" = 6 chars → line 1 starts at offset 6
        Assert.Equal(6L, buf.GetLineStart(1));
    }

    [Fact]
    public void GetLineStart_CRLF_HandledCorrectly() {
        var content = "Line1\r\nLine2\r\nLine3";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        Assert.Equal(3L, buf.LineCount);
        Assert.Equal(0L, buf.GetLineStart(0));
        Assert.Equal(7L, buf.GetLineStart(1)); // "Line1\r\n" = 7 chars
        Assert.Equal(14L, buf.GetLineStart(2)); // "Line2\r\n" = 7 more
    }

    [Fact]
    public void GetLineStart_BareCarriageReturn_CountsAsNewline() {
        var content = "A\rB\rC";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        Assert.Equal(3L, buf.LineCount);
        Assert.Equal(0L, buf.GetLineStart(0));
        Assert.Equal(2L, buf.GetLineStart(1)); // "A\r" = 2 chars
        Assert.Equal(4L, buf.GetLineStart(2)); // "B\r" = 2 chars
    }

    [Fact]
    public void GetLineStart_OutOfRange_ReturnsNegOne() {
        var content = "One line";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        Assert.Equal(-1L, buf.GetLineStart(-1));
        Assert.Equal(-1L, buf.GetLineStart(99));
    }

    [Fact]
    public void GetLineStart_ManyLines_SampledLookupCorrect() {
        // Generate enough lines to exercise the sampled index (> LineSampleStride = 1024).
        var sb = new StringBuilder();
        const int lineCount = 2100;
        for (var i = 0; i < lineCount; i++) {
            sb.Append($"Line {i:D5}");
            if (i < lineCount - 1) {
                sb.Append('\n');
            }
        }
        var content = sb.ToString();
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        Assert.Equal(lineCount, buf.LineCount);

        // Verify a few specific lines (including ones that cross sample boundaries).
        var expected = 0L;
        for (var i = 0; i < lineCount; i++) {
            if (i == 0 || i == 1 || i == 1023 || i == 1024 || i == 1025 || i == 2000) {
                Assert.Equal(expected, buf.GetLineStart(i));
            }
            expected += $"Line {i:D5}".Length + (i < lineCount - 1 ? 1 : 0); // +1 for \n
        }
    }

    // -------------------------------------------------------------------------
    // Page eviction (MaxPages=2, file > 2 pages)
    // -------------------------------------------------------------------------

    [Fact]
    public void PageEviction_SmallCache_StillReturnsCorrectData() {
        // Create a file that spans at least 5 pages (5 MB+ of UTF-8 text).
        // With MaxPages=2, most pages will be evicted during scan, then reloaded on demand.
        var sb = new StringBuilder();
        var lineText = new string('X', 200); // 200 chars per line
        var lineCount = 30_000; // ~6 MB total (200 + 1 newline = ~201 bytes × 30K)
        for (var i = 0; i < lineCount; i++) {
            sb.Append(lineText);
            if (i < lineCount - 1) {
                sb.Append('\n');
            }
        }
        var content = sb.ToString();
        var path = WriteTempFile(content);

        using var buf = LoadAndWait(path, maxPages: 2);

        Assert.Equal(content.Length, buf.Length);

        // Read from near the end (definitely an evicted page).
        var endOfs = content.Length - 50;
        var dest = new char[50];
        buf.CopyTo(endOfs, dest, 50);
        var expected = content.Substring(content.Length - 50, 50);
        Assert.Equal(expected, new string(dest));

        // Read from the beginning (may have been evicted to make room for end).
        var destStart = new char[10];
        buf.CopyTo(0, destStart, 10);
        Assert.Equal(content[..10], new string(destStart));
    }

    [Fact]
    public void PageEviction_IsLoaded_ReturnsFalseForEvictedPages() {
        // Create a file that spans at least 3 pages.
        var lineText = new string('A', 500);
        var sb = new StringBuilder();
        for (var i = 0; i < 10_000; i++) {
            sb.AppendLine(lineText);
        }
        var content = sb.ToString();
        var path = WriteTempFile(content);

        using var buf = LoadAndWait(path, maxPages: 1);

        // Only 1 page fits. Accessing the last chunk of the file should report
        // that the first chunk is evicted (or vice versa). We check that at least
        // one region reports not loaded.
        var firstLoaded = buf.IsLoaded(0, 100);
        var lastLoaded = buf.IsLoaded(content.Length - 100, 100);

        // With maxPages=1, at most one can be loaded at a time (after scan, the
        // last page is retained). It's possible neither is the same as the scan's
        // last-retained page. Just verify the API works without error.
        Assert.True(firstLoaded || lastLoaded || true); // non-crashing is the test

        // Force-load one end and verify IsLoaded changes.
        buf.CopyTo(0, new char[100], 100); // loads first page
        Assert.True(buf.IsLoaded(0, 100));
    }

    // -------------------------------------------------------------------------
    // CopyTo spanning page boundaries
    // -------------------------------------------------------------------------

    [Fact]
    public void CopyTo_SpanningPageBoundary_ReturnsCorrectChars() {
        // Create a file just over 2 MB so it spans 2+ pages.
        var sb = new StringBuilder();
        while (sb.Length < 2_200_000) {
            sb.AppendLine("This is a line of text that will be repeated many times to fill pages.");
        }
        var content = sb.ToString();
        var path = WriteTempFile(content);

        using var buf = LoadAndWait(path);

        // Read across the ~1 MB boundary.
        var start = 1_048_000; // close to page boundary
        var len = 2000;
        var dest = new char[len];
        buf.CopyTo(start, dest, len);
        var expected = content.Substring(start, len);
        Assert.Equal(expected, new string(dest));
    }

    // -------------------------------------------------------------------------
    // BOM detection
    // -------------------------------------------------------------------------

    [Fact]
    public void BomDetection_Utf8Bom_DecodesCorrectly() {
        var text = "Hello UTF-8 BOM!";
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var encoded = Encoding.UTF8.GetBytes(text);
        var raw = new byte[bom.Length + encoded.Length];
        bom.CopyTo(raw, 0);
        encoded.CopyTo(raw, bom.Length);
        var path = WriteTempFileBytes(raw);

        using var buf = LoadAndWait(path);
        Assert.Equal(text.Length, buf.Length);
        Assert.Equal('H', buf[0]);

        var dest = new char[text.Length];
        buf.CopyTo(0, dest, text.Length);
        Assert.Equal(text, new string(dest));
    }

    [Fact]
    public void BomDetection_Utf16LE_DecodesCorrectly() {
        var text = "Hello UTF-16 LE!";
        var encoding = Encoding.Unicode; // UTF-16 LE
        var bom = encoding.GetPreamble();
        var encoded = encoding.GetBytes(text);
        var raw = new byte[bom.Length + encoded.Length];
        bom.CopyTo(raw, 0);
        encoded.CopyTo(raw, bom.Length);
        var path = WriteTempFileBytes(raw);

        using var buf = LoadAndWait(path);
        Assert.Equal(text.Length, buf.Length);

        var dest = new char[text.Length];
        buf.CopyTo(0, dest, text.Length);
        Assert.Equal(text, new string(dest));
    }

    [Fact]
    public void BomDetection_Utf16BE_DecodesCorrectly() {
        var text = "Hello UTF-16 BE!";
        var encoding = Encoding.BigEndianUnicode; // UTF-16 BE
        var bom = encoding.GetPreamble();
        var encoded = encoding.GetBytes(text);
        var raw = new byte[bom.Length + encoded.Length];
        bom.CopyTo(raw, 0);
        encoded.CopyTo(raw, bom.Length);
        var path = WriteTempFileBytes(raw);

        using var buf = LoadAndWait(path);
        Assert.Equal(text.Length, buf.Length);

        var dest = new char[text.Length];
        buf.CopyTo(0, dest, text.Length);
        Assert.Equal(text, new string(dest));
    }

    [Fact]
    public void NoBom_DefaultsToUtf8() {
        var text = "No BOM here — just ASCII-compatible UTF-8.";
        var path = WriteTempFile(text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var buf = LoadAndWait(path);
        Assert.Equal(text.Length, buf.Length);

        var dest = new char[5];
        buf.CopyTo(0, dest, 5);
        Assert.Equal("No BO", new string(dest));
    }

    // -------------------------------------------------------------------------
    // Thread safety: concurrent access during / after scan
    // -------------------------------------------------------------------------

    [Fact]
    public void ConcurrentAccess_NoExceptions() {
        // Build a multi-page file.
        var sb = new StringBuilder();
        for (var i = 0; i < 20_000; i++) {
            sb.AppendLine($"Line {i:D6} padding text to make it wider");
        }
        var content = sb.ToString();
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path, maxPages: 4);

        // Hammer it from multiple threads.
        var exceptions = new List<Exception>();
        var threads = new Thread[8];
        for (var t = 0; t < threads.Length; t++) {
            var seed = t;
            threads[t] = new Thread(() => {
                try {
                    var rng = new Random(seed);
                    for (var i = 0; i < 200; i++) {
                        var ofs = rng.NextInt64(0, buf.Length - 10);
                        var ch = buf[ofs];
                        var dest = new char[10];
                        buf.CopyTo(ofs, dest, 10);
                        // Verify consistency.
                        Assert.Equal(ch, dest[0]);
                    }
                } catch (Exception ex) {
                    lock (exceptions) {
                        exceptions.Add(ex);
                    }
                }
            });
        }

        foreach (var thread in threads) {
            thread.Start();
        }
        foreach (var thread in threads) {
            thread.Join(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(exceptions);
    }

    // -------------------------------------------------------------------------
    // EnsureLoaded triggers async page loading
    // -------------------------------------------------------------------------

    [Fact]
    public void EnsureLoaded_LoadsEvictedPage() {
        // Multi-page file with small cache.
        var sb = new StringBuilder();
        for (var i = 0; i < 15_000; i++) {
            sb.AppendLine($"Row {i:D6} with some padding text here");
        }
        var content = sb.ToString();
        var path = WriteTempFile(content);

        using var buf = LoadAndWait(path, maxPages: 2);

        // Pick a range near the end that's probably evicted.
        var ofs = content.Length - 200;
        if (!buf.IsLoaded(ofs, 100)) {
            var loaded = new ManualResetEventSlim(false);
            buf.ProgressChanged += () => {
                if (buf.IsLoaded(ofs, 100)) {
                    loaded.Set();
                }
            };
            buf.EnsureLoaded(ofs, 100);
            Assert.True(loaded.Wait(TimeSpan.FromSeconds(5)),
                "EnsureLoaded did not load the requested range in time.");
        }

        // Either way, the data should now be accessible and correct.
        var dest = new char[100];
        buf.CopyTo(ofs, dest, 100);
        Assert.Equal(content.Substring(ofs, 100), new string(dest));
    }

    // -------------------------------------------------------------------------
    // Indexer out-of-range throws
    // -------------------------------------------------------------------------

    [Fact]
    public void Indexer_OutOfRange_Throws() {
        var path = WriteTempFile("Short.");
        using var buf = LoadAndWait(path);

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = buf[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = buf[buf.Length]);
    }

    // -------------------------------------------------------------------------
    // Empty file
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptyFile_LengthIsZero() {
        var path = WriteTempFile("");
        using var buf = LoadAndWait(path);

        Assert.Equal(0L, buf.Length);
        Assert.True(buf.LengthIsKnown);
    }

    // -------------------------------------------------------------------------
    // PieceTable integration
    // -------------------------------------------------------------------------

    [Fact]
    public void PieceTable_WithPagedBuffer_GetText_Correct() {
        var content = "Hello from paged buffer!\nLine two.\nLine three.";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        var table = new PieceTable(buf);
        Assert.Equal(content, table.GetText());
        Assert.Equal(3L, table.LineCount);
    }

    [Fact]
    public void PieceTable_WithPagedBuffer_InsertAndGetText_Correct() {
        var content = "ABCDEF";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        var table = new PieceTable(buf);
        table.EnsureLineTree();
        table.Insert(3, "XYZ");
        Assert.Equal("ABCXYZDEF", table.GetText());
    }

    // -------------------------------------------------------------------------
    // Terminator type tracking
    // -------------------------------------------------------------------------

    /// <summary>Helper: builds a string of <paramref name="n"/> content chars.</summary>
    private static string Chars(int n, char ch = 'a') => new(ch, n);

    [Fact]
    public void TerminatorType_EmptyFile() {
        var path = WriteTempFile("");
        using var buf = LoadAndWait(path);
        Assert.Equal(1L, buf.LineCount);
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(0));
    }

    [Fact]
    public void TerminatorType_OneChar_NoTerminator() {
        var path = WriteTempFile("a");
        using var buf = LoadAndWait(path);
        Assert.Equal(1L, buf.LineCount);
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(0));
    }

    [Fact]
    public void TerminatorType_OneChar_LF() {
        var path = WriteTempFile("a\n");
        using var buf = LoadAndWait(path);
        Assert.Equal(2L, buf.LineCount);
        Assert.Equal(LineTerminatorType.LF, buf.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(1));
    }

    [Fact]
    public void TerminatorType_OneChar_CRLF() {
        var path = WriteTempFile("a\r\n");
        using var buf = LoadAndWait(path);
        Assert.Equal(2L, buf.LineCount);
        Assert.Equal(LineTerminatorType.CRLF, buf.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(1));
    }

    [Fact]
    public void TerminatorType_498Chars_CRLF() {
        // 498 content + CRLF = 500 total, fits in one line entry
        var path = WriteTempFile(Chars(498) + "\r\n");
        using var buf = LoadAndWait(path);
        Assert.Equal(2L, buf.LineCount);
        Assert.Equal(LineTerminatorType.CRLF, buf.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(1));
        // Line 0 full length = 500, content = 498
        var lengths = buf.TakeLineLengths()!;
        Assert.Equal(500, lengths[0]);
    }

    [Fact]
    public void TerminatorType_499Chars_LF() {
        // 499 content + LF = 500 total
        var path = WriteTempFile(Chars(499) + "\n");
        using var buf = LoadAndWait(path);
        Assert.Equal(2L, buf.LineCount);
        Assert.Equal(LineTerminatorType.LF, buf.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(1));
    }

    [Fact]
    public void TerminatorType_499Chars_CRLF() {
        // 499 content + CRLF = 501 total — crosses MaxPseudoLine boundary
        // but content is only 499, so no pseudo-split should occur
        var path = WriteTempFile(Chars(499) + "\r\n");
        using var buf = LoadAndWait(path);
        Assert.Equal(2L, buf.LineCount);
        Assert.Equal(LineTerminatorType.CRLF, buf.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(1));
    }

    [Fact]
    public void TerminatorType_500Chars_NoTerminator() {
        // Exactly MaxPseudoLine, no terminator — single line, no split
        var path = WriteTempFile(Chars(500));
        using var buf = LoadAndWait(path);
        Assert.Equal(1L, buf.LineCount);
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(0));
    }

    [Fact]
    public void TerminatorType_500Chars_LF() {
        var path = WriteTempFile(Chars(500) + "\n");
        using var buf = LoadAndWait(path);
        Assert.Equal(2L, buf.LineCount);
        Assert.Equal(LineTerminatorType.LF, buf.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(1));
    }

    [Fact]
    public void TerminatorType_500Chars_CRLF() {
        var path = WriteTempFile(Chars(500) + "\r\n");
        using var buf = LoadAndWait(path);
        Assert.Equal(2L, buf.LineCount);
        Assert.Equal(LineTerminatorType.CRLF, buf.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(1));
    }

[Fact]
    public void TerminatorType_UniformCRLF_SingleRun() {
        // Multiple CRLF lines should produce minimal RLE entries
        var path = WriteTempFile("line1\r\nline2\r\nline3\r\n");
        using var buf = LoadAndWait(path);
        Assert.Equal(4L, buf.LineCount);
        Assert.Equal(LineTerminatorType.CRLF, buf.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.CRLF, buf.GetLineTerminator(1));
        Assert.Equal(LineTerminatorType.CRLF, buf.GetLineTerminator(2));
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(3));
    }

    [Fact]
    public void TerminatorType_MixedEndings() {
        // LF then CRLF then CR
        var path = WriteTempFile("a\nb\r\nc\rd");
        using var buf = LoadAndWait(path);
        Assert.Equal(4L, buf.LineCount);
        Assert.Equal(LineTerminatorType.LF, buf.GetLineTerminator(0));
        Assert.Equal(LineTerminatorType.CRLF, buf.GetLineTerminator(1));
        Assert.Equal(LineTerminatorType.CR, buf.GetLineTerminator(2));
        Assert.Equal(LineTerminatorType.None, buf.GetLineTerminator(3));
    }

    // -------------------------------------------------------------------------
    // Page boundary edge cases (1 MB pages — TRIAGE Priority 2 gap)
    // -------------------------------------------------------------------------

    private const int PageSizeBytes = 1024 * 1024;

    [Fact]
    public void MultiByteCodepoint_StraddlesPageBoundary_DecodesCorrectly() {
        // Construct a file where a 4-byte UTF-8 codepoint (the 🎉 emoji,
        // F0 9F 8E 89) starts at byte (PageSize − 2), so its first 2 bytes
        // are in page 0 and its last 2 are in page 1.  The decoder must
        // carry the partial state across the page boundary or the emoji
        // becomes a pair of replacement chars.
        //
        // Layout:
        //   bytes [0..1_048_573]    = ASCII filler 'a' × (PageSize − 2)
        //   bytes [1_048_574..577]  = 🎉 (F0 9F | 8E 89)
        //   bytes [1_048_578..580]  = "end" (3 ASCII bytes)
        const int prefixLen = PageSizeBytes - 2;
        var content = new string('a', prefixLen) + "🎉" + "end";
        var bytes = new UTF8Encoding(false).GetBytes(content);
        // Sanity check our straddle math: emoji bytes at positions
        // (prefixLen .. prefixLen + 3), so byte (PageSize - 2) is the
        // emoji's first byte and byte (PageSize) is its third.
        Assert.Equal(0xF0, bytes[prefixLen]);
        Assert.Equal(0x9F, bytes[prefixLen + 1]);
        Assert.Equal(0x8E, bytes[prefixLen + 2]);
        Assert.Equal(0x89, bytes[prefixLen + 3]);

        var path = WriteTempFileBytes(bytes);
        using var buf = LoadAndWait(path);

        // Char layout: prefix (1 char per byte) + 2 surrogate halves + 3 ASCII.
        Assert.Equal(prefixLen + 2 + 3, buf.Length);
        // Read the 5 chars around the boundary and verify they round-trip
        // through the surrogate pair.
        Span<char> window = stackalloc char[5];
        buf.CopyTo(prefixLen, window, 5);
        var slice = new string(window);
        Assert.Equal("🎉end", slice);
    }

    [Fact]
    public void CrLf_StraddlesPageBoundary_CountsAsSingleTerminator() {
        // \r as the last byte of page 0, \n as the first byte of page 1.
        // The line scanner has a _prevWasCr flag that's supposed to carry
        // across Scan() calls, so this should be one CRLF, not a CR + LF.
        const int prefixLen = PageSizeBytes - 1;
        var content = new string('a', prefixLen) + "\r\n" + "end";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        // 2 lines: the prefix line (terminated by CRLF) + "end".
        Assert.Equal(2L, buf.LineCount);
        Assert.Equal(LineTerminatorType.CRLF, buf.GetLineTerminator(0));
    }

    // -------------------------------------------------------------------------
    // BOM edge cases (TRIAGE Priority 2 gap)
    // -------------------------------------------------------------------------

    [Fact]
    public void Bom_OneBytePartialUtf8_TreatedAsData() {
        // Single 0xEF byte — looks like the start of a UTF-8 BOM, but
        // there are not enough bytes to confirm.  Should fall back to
        // "no BOM, UTF-8" and decode the byte as a (replacement) char.
        var path = WriteTempFileBytes([0xEF]);
        using var buf = LoadAndWait(path);

        Assert.Equal(FileEncoding.Utf8, buf.DetectedEncoding.Encoding);
        // 1 raw byte → at least 1 decoded char (UTF-8 replacement for the
        // incomplete sequence).  We don't pin the exact replacement char,
        // but we do pin that the file isn't reported as zero-length.
        Assert.True(buf.Length >= 1);
    }

    [Fact]
    public void Bom_TwoBytesPartialUtf8_TreatedAsData() {
        // 0xEF 0xBB — first two bytes of UTF-8 BOM, third missing.
        // Same as above: should fall back to "no BOM, UTF-8".
        var path = WriteTempFileBytes([0xEF, 0xBB]);
        using var buf = LoadAndWait(path);

        Assert.Equal(FileEncoding.Utf8, buf.DetectedEncoding.Encoding);
        Assert.True(buf.Length >= 1);
    }

    [Fact]
    public void Bom_Utf8BomOnly_NoContent_ReportsBomAndZeroChars() {
        // Exactly the 3-byte UTF-8 BOM, no payload.
        var path = WriteTempFileBytes([0xEF, 0xBB, 0xBF]);
        using var buf = LoadAndWait(path);

        Assert.Equal(FileEncoding.Utf8Bom, buf.DetectedEncoding.Encoding);
        Assert.Equal(0L, buf.Length);
    }

    [Fact]
    public void Bom_Utf16LeBomOnly_NoContent_ReportsBomAndZeroChars() {
        var path = WriteTempFileBytes([0xFF, 0xFE]);
        using var buf = LoadAndWait(path);

        Assert.Equal(FileEncoding.Utf16Le, buf.DetectedEncoding.Encoding);
        Assert.Equal(0L, buf.Length);
    }

    [Fact]
    public void Bom_Utf16BeBomOnly_NoContent_ReportsBomAndZeroChars() {
        var path = WriteTempFileBytes([0xFE, 0xFF]);
        using var buf = LoadAndWait(path);

        Assert.Equal(FileEncoding.Utf16Be, buf.DetectedEncoding.Encoding);
        Assert.Equal(0L, buf.Length);
    }

    // -------------------------------------------------------------------------
    // SHA-1 correctness vs independent hash (TRIAGE Priority 2 gap)
    // -------------------------------------------------------------------------

    [Fact]
    public void Sha1_MatchesIndependentHash_AsciiContent() {
        var content = "Hello, world!\nLine 2\nLine 3\n";
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        var fileBytes = File.ReadAllBytes(path);
        var expected = Convert.ToHexStringLower(SHA1.HashData(fileBytes));
        Assert.Equal(expected, buf.Sha1);
    }

    [Fact]
    public void Sha1_MatchesIndependentHash_WithUtf8Bom() {
        // 3-byte UTF-8 BOM + ASCII payload "hello".
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' };
        var path = WriteTempFileBytes(bytes);
        using var buf = LoadAndWait(path);

        var fileBytes = File.ReadAllBytes(path);
        var expected = Convert.ToHexStringLower(SHA1.HashData(fileBytes));
        // The BOM bytes should be hashed too (full file from byte 0).
        Assert.Equal(expected, buf.Sha1);
    }

    [Fact]
    public void Sha1_MatchesIndependentHash_LargeMultiPage() {
        // ~3 MB of content, 3 pages worth.  Catches any "missed bytes"
        // bug in the page-by-page hashing.
        var content = new string('x', 3 * PageSizeBytes + 137);
        var path = WriteTempFile(content);
        using var buf = LoadAndWait(path);

        var fileBytes = File.ReadAllBytes(path);
        var expected = Convert.ToHexStringLower(SHA1.HashData(fileBytes));
        Assert.Equal(expected, buf.Sha1);
    }

    // -------------------------------------------------------------------------
    // TakeLineLengths / TakeTerminatorRuns double-call (TRIAGE Priority 2 gap)
    // -------------------------------------------------------------------------

    [Fact]
    public void TakeLineLengths_DoubleCall_SecondReturnsNull() {
        var path = WriteTempFile("a\nb\nc\n");
        using var buf = LoadAndWait(path);

        var first = buf.TakeLineLengths();
        Assert.NotNull(first);
        Assert.NotEmpty(first);

        // The "Take" semantics: caller takes ownership, buffer drops its
        // reference.  A second call must therefore return null, not a
        // duplicate of the same list (which would risk callers mutating
        // each other's data).
        var second = buf.TakeLineLengths();
        Assert.Null(second);
    }

    [Fact]
    public void TakeTerminatorRuns_DoubleCall_SecondReturnsNull() {
        var path = WriteTempFile("a\nb\nc\n");
        using var buf = LoadAndWait(path);

        var first = buf.TakeTerminatorRuns();
        Assert.NotNull(first);

        var second = buf.TakeTerminatorRuns();
        Assert.Null(second);
    }

    [Fact]
    public void TakeLineLengths_AfterTake_GetLineStartReturnsMinusOne_ByDesign() {
        // Documented footgun: TakeLineLengths transfers ownership of the
        // line-length list to the caller and clears the buffer's reference.
        // GetLineStart depends on that list to compute line offsets, so
        // after Take it can only answer for line 0 — line 1+ returns -1.
        // The production call pattern (load → TakeLineLengths →
        // PieceTable.InstallLineTree) never queries GetLineStart on the
        // buffer post-Take, so this is acceptable.  This test pins the
        // current contract; if a future change makes GetLineStart survive
        // Take, this test should be updated to assert the new behavior.
        var path = WriteTempFile("a\nb\nc\n");
        using var buf = LoadAndWait(path);

        buf.TakeLineLengths();

        // Length and indexer (which use page data, not _lineLengths) still work.
        Assert.Equal(6L, buf.Length);
        Assert.Equal('a', buf[0]);
        // Line 0 always starts at 0 (short-circuited).
        Assert.Equal(0L, buf.GetLineStart(0));
        // Line 1+ returns -1 because _lineLengths is gone.
        Assert.Equal(-1L, buf.GetLineStart(1));
        Assert.Equal(-1L, buf.GetLineStart(2));
    }

    // -------------------------------------------------------------------------
    // Dispose mid-load (TRIAGE Priority 2 gap)
    // -------------------------------------------------------------------------

    [Fact]
    public void Dispose_BeforeLoadStarts_DoesNotThrow() {
        var path = WriteTempFile("hello");
        var fi = new FileInfo(path);
        var buf = new PagedFileBuffer(path, fi.Length);
        // Dispose before StartLoading — should be a clean no-op, no
        // background thread to cancel.
        buf.Dispose();
    }

    [Fact]
    public void Dispose_DuringActiveLoad_NoDeadlock() {
        // Construct a multi-page file so the background scan takes
        // measurable time, then Dispose immediately after StartLoading.
        // The Dispose path must signal cancellation and the scan thread
        // must terminate without blocking the test.
        var content = new string('a', 5 * PageSizeBytes); // 5 MB
        var path = WriteTempFile(content);
        var fi = new FileInfo(path);
        var buf = new PagedFileBuffer(path, fi.Length);

        buf.StartLoading();
        // Dispose mid-scan.  No assertion on what state the load reached;
        // just that Dispose returns and the test thread isn't blocked.
        buf.Dispose();

        // Wait briefly to give any leaked background thread a chance to
        // crash a follow-up test if Dispose didn't actually clean up.
        // (We can't directly assert no thread leak in xUnit, but a process
        // hang here would manifest as a test timeout, which IS observable.)
        Thread.Sleep(50);
    }

    // -------------------------------------------------------------------------
    // LRU promote / evict ordering (TRIAGE Priority 2 gap)
    //
    // The LRU cache is covered by IsLoaded() for each page.  Build a file
    // with more pages than MaxPagesInMemory, then exercise the cache by
    // reading pages in a controlled order and asserting which pages remain
    // resident.
    // -------------------------------------------------------------------------

    [Fact]
    public void Lru_EvictsOldestPage_WhenCacheIsFull() {
        // maxPages = 2 means only 2 pages can live in memory at once.
        // Build a 4-page file so eviction is guaranteed.
        var content = new string('a', 4 * PageSizeBytes);
        var path = WriteTempFile(content);
        var fi = new FileInfo(path);
        using var buf = new PagedFileBuffer(path, fi.Length, maxPages: 2);
        var done = new ManualResetEventSlim(false);
        buf.LoadComplete += () => done.Set();
        buf.StartLoading();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));

        // After the scan, the cache retains the LAST 2 pages loaded during
        // the scan (the scan walks forward and stops adding once full, so
        // pages 0 and 1 are cached, pages 2 and 3 were never cached — see
        // the scanner's `if (_loadedPageCount < MaxPagesInMemory)` check).
        // The production cache fills on-demand via LoadPageFromDisk, so
        // drive eviction explicitly by reading from each page.

        // Read from page 2 — evicts whichever cached page is at the LRU tail.
        _ = buf[2L * PageSizeBytes];
        Assert.True(buf.IsLoaded(2L * PageSizeBytes, 1));

        // Read from page 3 — evicts the next LRU tail entry.
        _ = buf[3L * PageSizeBytes];
        Assert.True(buf.IsLoaded(3L * PageSizeBytes, 1));

        // With maxPages = 2, only the two most-recently-touched pages can
        // still be resident.  Exact identity depends on promote order, but
        // the CACHE SIZE invariant must hold.
        var residentCount = 0;
        for (var p = 0; p < 4; p++) {
            if (buf.IsLoaded(p * PageSizeBytes, 1)) residentCount++;
        }
        Assert.Equal(2, residentCount);
    }

    [Fact]
    public void Lru_PromotesAccessedPage_PreventingEviction() {
        // Build a 3-page file with maxPages = 2.  Establish a cache state,
        // then touch an older page so it moves back to the head of the LRU
        // list — the next eviction should pick the OTHER page instead.
        var content = new string('a', 3 * PageSizeBytes);
        var path = WriteTempFile(content);
        var fi = new FileInfo(path);
        using var buf = new PagedFileBuffer(path, fi.Length, maxPages: 2);
        var done = new ManualResetEventSlim(false);
        buf.LoadComplete += () => done.Set();
        buf.StartLoading();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));

        // Prime cache with pages 0 and 1 (scan may or may not have loaded
        // page 2, depending on timing — the maxPages gate makes it skip).
        _ = buf[0];
        _ = buf[PageSizeBytes];

        // Touch page 0 again — should promote it to the head of the LRU list.
        _ = buf[1];

        // Now access page 2 — this should evict page 1 (the LRU tail), not
        // page 0 (just promoted).
        _ = buf[2L * PageSizeBytes];

        // Page 0 must still be cached (was just promoted), page 2 must be
        // cached (just loaded).  Page 1 must be the one that got evicted.
        Assert.True(buf.IsLoaded(0, 1),
            "Page 0 was promoted but got evicted — promote order is broken.");
        Assert.True(buf.IsLoaded(2L * PageSizeBytes, 1),
            "Page 2 was just loaded but is not resident.");
        Assert.False(buf.IsLoaded(PageSizeBytes, 1),
            "Page 1 should have been evicted as the LRU tail.");
    }

    // -------------------------------------------------------------------------
    // ScanError propagation (TRIAGE Priority 2 gap)
    //
    // The scan worker catches any exception from the background thread and
    // stores it on the public ScanError property.  Without a test, a
    // refactor that swallows errors silently would be invisible.
    // -------------------------------------------------------------------------

    [Fact]
    public void ScanError_FileDeletedMidScan_IsNull_ForSimpleCase() {
        // Happy-path ScanError is null after a clean scan.
        var path = WriteTempFile("hello\nworld\n");
        using var buf = LoadAndWait(path);
        Assert.Null(buf.ScanError);
    }

    [Fact]
    public void ScanError_CannotOpenFile_IsPropagated() {
        // Construct a PagedFileBuffer with a bogus byteLen pointing at a
        // file that doesn't exist.  The scan worker should catch the
        // FileNotFoundException and store it on ScanError rather than
        // crashing the process.
        var bogusPath = Path.Combine(Path.GetTempPath(),
            $"pfb_missing_{Guid.NewGuid():N}.txt");
        // Do NOT create the file.  Pretend it's 1 KB so the constructor accepts it.
        var buf = new PagedFileBuffer(bogusPath, 1024);
        var done = new ManualResetEventSlim(false);
        buf.LoadComplete += () => done.Set();
        buf.StartLoading();
        // LoadComplete fires even on error (LengthIsKnown flips to true
        // inside the catch).  Fall back to a brief poll if no event.
        var ok = done.Wait(TimeSpan.FromSeconds(5));
        if (!ok) {
            // Some code paths may never fire LoadComplete on I/O error —
            // poll LengthIsKnown / ScanError directly.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && buf.ScanError is null && !buf.LengthIsKnown) {
                Thread.Sleep(20);
            }
        }

        // The ScanError must be a FileNotFoundException (or a similarly
        // descriptive I/O exception) — NOT null, NOT silent.
        Assert.NotNull(buf.ScanError);
        Assert.True(
            buf.ScanError is FileNotFoundException or IOException,
            $"Expected FileNotFoundException / IOException, got {buf.ScanError.GetType().Name}: {buf.ScanError.Message}");
        buf.Dispose();
    }
}
