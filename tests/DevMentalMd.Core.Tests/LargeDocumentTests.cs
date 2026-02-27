using System;
using System.IO;
using System.Text;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.IO;

namespace DevMentalMd.Core.Tests;

/// <summary>
/// Memory-stability and correctness tests for large-document support.
/// Uses <see cref="ProceduralBuffer"/> to simulate very large files without needing
/// real multi-GB files on disk.
/// </summary>
public class LargeDocumentTests {
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ProceduralBuffer MakeBuffer(long lineCount, int stride = ProceduralBuffer.DefaultStride)
        => new(lineCount, i => $"This is line {i}", stride);

    private static long MeasureAlloc(Action action) {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        action();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    // -------------------------------------------------------------------------
    // ProceduralBuffer construction
    // -------------------------------------------------------------------------

    [Fact]
    public void ProceduralBuffer_BillionLines_ConstructionAllocatesOnlyIndex() {
        const long lineCount = 1_000_000_000L;
        const int stride = 1000;
        // Expected: 1B / 1000 = 1M sample slots × 8 bytes = ~8 MB
        const long maxBytes = 20L * 1024 * 1024; // 20 MB budget

        var allocBytes = MeasureAlloc(() => {
            using var buf = new ProceduralBuffer(lineCount, i => $"Line {i}", stride);
            // Just constructing — do not trigger lazy sample building
            GC.KeepAlive(buf);
        });

        Assert.True(allocBytes < maxBytes,
            $"Construction allocated {allocBytes / 1024 / 1024} MB, expected < 20 MB");
    }

    [Fact]
    public void ProceduralBuffer_SmallBuffer_LineCountCorrect() {
        using var buf = MakeBuffer(10);
        Assert.Equal(10L, buf.LineCount);
    }

    [Fact]
    public void ProceduralBuffer_GetLineStart_Line0_IsZero() {
        using var buf = MakeBuffer(100);
        Assert.Equal(0L, buf.GetLineStart(0));
    }

    [Fact]
    public void ProceduralBuffer_GetLineStart_Line1_AfterFirstLine() {
        using var buf = MakeBuffer(100);
        // Line 0 = "This is line 0" (14 chars) + '\n' separator
        var line0 = "This is line 0";
        Assert.Equal(line0.Length + 1L, buf.GetLineStart(1));
    }

    [Fact]
    public void ProceduralBuffer_GetLineStart_LargeIdx_Correct() {
        // Use stride=10 so we can verify multi-sample accuracy without huge counts
        using var buf = new ProceduralBuffer(500, i => $"L{i}", stride: 10);
        // Line 25: offset = sum of len("L0")+1 + len("L1")+1 + ... + len("L24")+1
        var expected = 0L;
        for (var i = 0L; i < 25; i++) {
            expected += $"L{i}".Length + 1;
        }
        Assert.Equal(expected, buf.GetLineStart(25));
    }

    [Fact]
    public void ProceduralBuffer_GetLineStart_LastLine_Correct() {
        using var buf = new ProceduralBuffer(5, i => $"X{i}", stride: 2);
        var expected = 0L;
        for (var i = 0L; i < 4; i++) {
            expected += $"X{i}".Length + 1;
        }
        Assert.Equal(expected, buf.GetLineStart(4));
    }

    [Fact]
    public void ProceduralBuffer_IndexAccess_FirstChar() {
        using var buf = MakeBuffer(10);
        // "This is line 0"[0] = 'T'
        Assert.Equal('T', buf[0]);
    }

    [Fact]
    public void ProceduralBuffer_IndexAccess_NewlineSeparator() {
        using var buf = MakeBuffer(10);
        var line0Len = "This is line 0".Length;
        Assert.Equal('\n', buf[line0Len]);
    }

    [Fact]
    public void ProceduralBuffer_CopyTo_FirstLine() {
        using var buf = MakeBuffer(10);
        var expected = "This is line 0";
        var dest = new char[expected.Length];
        buf.CopyTo(0, dest, expected.Length);
        Assert.Equal(expected, new string(dest));
    }

    // -------------------------------------------------------------------------
    // PieceTable + ProceduralBuffer
    // -------------------------------------------------------------------------

    [Fact]
    public void PieceTable_WithProceduralBuffer_ConstructionIsO1() {
        const long lineCount = 1_000_000L;
        const long maxBytes = 5L * 1024 * 1024; // 5 MB

        var allocBytes = MeasureAlloc(() => {
            using var buf = MakeBuffer(lineCount);
            var table = new PieceTable(buf);
            GC.KeepAlive(table);
        });

        Assert.True(allocBytes < maxBytes,
            $"PieceTable construction allocated {allocBytes / 1024} KB, expected < 5 MB");
    }

    [Fact]
    public void PieceTable_WithProceduralBuffer_GetTextFirstChars_Correct() {
        using var buf = MakeBuffer(100);
        var table = new PieceTable(buf);
        var result = table.GetText(0, 14);
        Assert.Equal("This is line 0", result);
    }

    // -------------------------------------------------------------------------
    // Document + ProceduralBuffer (1000 reads memory-bounded)
    // -------------------------------------------------------------------------

    [Fact]
    public void Document_WithProceduralBuffer_1000Reads_MemoryBounded() {
        const long maxPerRead = 5L * 1024 * 1024; // 5 MB per read-batch

        using var buf = MakeBuffer(10_000L);
        var table = new PieceTable(buf);
        var doc = new Document(table);

        var allocBytes = MeasureAlloc(() => {
            for (var i = 0; i < 1000; i++) {
                var text = doc.Table.GetText(0, 100);
                GC.KeepAlive(text);
            }
        });

        Assert.True(allocBytes < maxPerRead,
            $"1000 reads allocated {allocBytes / 1024} KB, expected < 5 MB");
    }

    // -------------------------------------------------------------------------
    // Regression: WholeBufSentinel after split
    // -------------------------------------------------------------------------

    [Fact]
    public void PieceTable_InsertNewline_InProceduralBuffer_CorrectLineCount() {
        // Simulates the user's bug: load 100-line ProceduralBuffer, insert \n after line 9.
        // Before the fix, Length was over-counted and VisitPieces read past the split boundary.
        using var buf = MakeBuffer(100);
        var table = new PieceTable(buf);

        var originalLen = table.Length;
        var originalText = table.GetText();
        Assert.Equal(100L, table.LineCount);

        // Insert a newline at the end of line 9 (just before the \n that already terminates line 9)
        var line9End = table.LineStartOfs(9) + "This is line 9".Length;
        table.Insert(line9End, "\n");

        // Length should grow by exactly 1
        Assert.Equal(originalLen + 1, table.Length);

        // Line count should grow by exactly 1
        Assert.Equal(101L, table.LineCount);

        // Full text should be the original with a \n inserted at the right spot
        var expected = originalText.Insert((int)line9End, "\n");
        var actual = table.GetText();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PieceTable_InsertText_InProceduralBuffer_GetTextSubstring_Correct() {
        // Verify that GetText(start, len) works correctly after a split of a WholeBufSentinel piece.
        using var buf = MakeBuffer(50);
        var table = new PieceTable(buf);

        // Insert "INSERTED" in the middle
        var midpoint = table.Length / 2;
        table.Insert(midpoint, "INSERTED");

        // Read a window around the insertion point
        var windowStart = midpoint - 10;
        var windowLen = 30;
        var window = table.GetText(windowStart, windowLen);

        // The window should contain the inserted text
        Assert.Contains("INSERTED", window);

        // Full text round-trip should also work
        var fullOriginal = buf.Length;
        Assert.Equal(fullOriginal + 8, table.Length); // "INSERTED" is 8 chars
    }

    // -------------------------------------------------------------------------
    // FileLoader / FileSaver round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void FileLoader_SaveLoad_RoundTrip() {
        var path = Path.Combine(Path.GetTempPath(), $"devmentalmd_test_{Guid.NewGuid():N}.md");
        try {
            var original = "# Hello\n\nThis is a test.\n";
            File.WriteAllText(path, original, Encoding.UTF8);

            var doc = FileLoader.Load(path);
            var loaded = doc.Table.GetText();
            Assert.Equal(original, loaded);
        } finally {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void FileSaver_Save_RoundTrip() {
        var path = Path.Combine(Path.GetTempPath(), $"devmentalmd_test_{Guid.NewGuid():N}.md");
        try {
            var original = "# Saved document\n\nContent here.\n";
            var doc = new Document(original);
            FileSaver.Save(doc, path);

            var read = File.ReadAllText(path, Encoding.UTF8);
            Assert.Equal(original, read);
        } finally {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void FileSaver_LargeDoc_StreamsWithoutFullAlloc() {
        // 10_000 lines of ~20 chars each = ~200 KB — small enough for a unit test
        // but exercises the chunked streaming path.
        const long lineCount = 10_000L;
        const long maxBytes = 5L * 1024 * 1024; // 5 MB

        var path = Path.Combine(Path.GetTempPath(), $"devmentalmd_test_{Guid.NewGuid():N}.md");
        try {
            using var buf = new ProceduralBuffer(lineCount, i => $"Line number {i:D6}", stride: 100);
            var table = new PieceTable(buf);
            var doc = new Document(table);

            var allocBytes = MeasureAlloc(() => FileSaver.Save(doc, path));

            Assert.True(allocBytes < maxBytes,
                $"FileSaver allocated {allocBytes / 1024} KB, expected < 5 MB");
        } finally {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
    }
}
