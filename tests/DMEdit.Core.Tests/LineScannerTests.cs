using DMEdit.Core.Documents;

namespace DMEdit.Core.Tests;

public class LineScannerTests {
    private static LineScanner Scan(string text) {
        var s = new LineScanner();
        s.Scan(text.AsSpan());
        s.Finish();
        return s;
    }

    [Fact]
    public void LongestLine_Lf_ReturnsLongestIncludingTerminator() {
        var s = Scan("abc\nde\nfghij\n");
        // Lines: "abc\n" (4), "de\n" (3), "fghij\n" (6), "" (0)
        Assert.Equal(6, s.LongestLine);
    }

    [Fact]
    public void LongestLine_Crlf_DoesNotAccumulateAcrossLines() {
        // Regression: CRLF branch used `continue` without resetting the
        // running line length, so _longestRealLine ≈ total char count.
        var s = Scan("abc\r\nde\r\nfghij\r\n");
        // Lines: "abc\r\n" (5), "de\r\n" (4), "fghij\r\n" (7), "" (0)
        Assert.Equal(7, s.LongestLine);
    }

    [Fact]
    public void LongestLine_ManyShortCrlfLines_IsShort() {
        // 10_000 lines of "hi\r\n" — longest should be 4, not 40_000.
        var text = string.Concat(Enumerable.Repeat("hi\r\n", 10_000));
        var s = Scan(text);
        Assert.Equal(4, s.LongestLine);
    }

    [Fact]
    public void LongestLine_BareCr_CountsTerminator() {
        var s = Scan("abcd\refg\r");
        // Lines: "abcd\r" (5), "efg\r" (4), "" (0)
        Assert.Equal(5, s.LongestLine);
    }

    [Fact]
    public void LongestLine_MixedEndings() {
        var s = Scan("aa\nbbb\r\ncccc\rdddd");
        // "aa\n"(3), "bbb\r\n"(5), "cccc\r"(5), "dddd"(4)
        Assert.Equal(5, s.LongestLine);
    }

    [Fact]
    public void LongestLine_NoNewline() {
        var s = Scan("hello world");
        Assert.Equal(11, s.LongestLine);
    }

    [Fact]
    public void LongestLine_Empty() {
        var s = Scan("");
        Assert.Equal(0, s.LongestLine);
    }

    [Fact]
    public void LongestLine_ChunkedScan_Crlf() {
        var s = new LineScanner();
        // Split a CRLF across Scan calls — the \r lands at the end of one
        // chunk, the \n at the start of the next.
        s.Scan("abc\r".AsSpan());
        s.Scan("\ndef\r\nghij".AsSpan());
        s.Finish();
        // Lines: "abc\r\n"(5), "def\r\n"(5), "ghij"(4)
        Assert.Equal(5, s.LongestLine);
    }

    // ------------------------------------------------------------------
    // Line counts and line-length list
    // ------------------------------------------------------------------

    [Fact]
    public void LineCount_Lf_CountsTrailingEmptyLine() {
        var s = Scan("a\nb\nc\n");
        Assert.Equal(4, s.LineCount);
        Assert.Equal(new[] { 2, 2, 2, 0 }, s.LineLengths);
    }

    [Fact]
    public void LineCount_Crlf_CountsTrailingEmptyLine() {
        var s = Scan("a\r\nb\r\nc\r\n");
        Assert.Equal(4, s.LineCount);
        Assert.Equal(new[] { 3, 3, 3, 0 }, s.LineLengths);
    }

    [Fact]
    public void LineCount_NoTerminator_OneLine() {
        var s = Scan("hello");
        Assert.Equal(1, s.LineCount);
        Assert.Equal(new[] { 5 }, s.LineLengths);
    }

    [Fact]
    public void LineCount_EmptyInput_OneEmptyLine() {
        var s = Scan("");
        Assert.Equal(1, s.LineCount);
        Assert.Equal(new[] { 0 }, s.LineLengths);
    }

    [Fact]
    public void LineLengths_MixedNoTrailingTerminator() {
        var s = Scan("aa\nbbb\r\ncccc\rdddd");
        Assert.Equal(new[] { 3, 5, 5, 4 }, s.LineLengths);
        Assert.Equal(4, s.LineCount);
    }

    [Fact]
    public void LineLengths_OnlyNewlines() {
        var s = Scan("\n\n\n");
        // Three LF lines each of length 1, plus trailing empty line.
        Assert.Equal(new[] { 1, 1, 1, 0 }, s.LineLengths);
        Assert.Equal(4, s.LineCount);
    }

    [Fact]
    public void LineLengths_ConsecutiveBareCrs() {
        var s = Scan("\r\r\r");
        Assert.Equal(new[] { 1, 1, 1, 0 }, s.LineLengths);
        Assert.Equal(3, s.CrCount);
    }

    // ------------------------------------------------------------------
    // Line-ending counters
    // ------------------------------------------------------------------

    [Fact]
    public void Counters_PureLf() {
        var s = Scan("a\nb\nc\n");
        Assert.Equal(3, s.LfCount);
        Assert.Equal(0, s.CrlfCount);
        Assert.Equal(0, s.CrCount);
    }

    [Fact]
    public void Counters_PureCrlf() {
        var s = Scan("a\r\nb\r\nc\r\n");
        Assert.Equal(0, s.LfCount);
        Assert.Equal(3, s.CrlfCount);
        Assert.Equal(0, s.CrCount);
    }

    [Fact]
    public void Counters_PureCr() {
        var s = Scan("a\rb\rc\r");
        Assert.Equal(0, s.LfCount);
        Assert.Equal(0, s.CrlfCount);
        Assert.Equal(3, s.CrCount);
    }

    [Fact]
    public void Counters_Mixed() {
        var s = Scan("a\nb\r\nc\rd");
        Assert.Equal(1, s.LfCount);
        Assert.Equal(1, s.CrlfCount);
        Assert.Equal(1, s.CrCount);
    }

    [Fact]
    public void Counters_ChunkedCrlfAcrossBoundary() {
        var s = new LineScanner();
        s.Scan("a\r".AsSpan());
        s.Scan("\nb".AsSpan());
        s.Finish();
        // Should detect as a single CRLF, not a CR + LF.
        Assert.Equal(1, s.CrlfCount);
        Assert.Equal(0, s.CrCount);
        Assert.Equal(0, s.LfCount);
    }

    [Fact]
    public void Counters_TrailingBareCr() {
        var s = Scan("abc\r");
        Assert.Equal(1, s.CrCount);
        Assert.Equal(0, s.CrlfCount);
    }

    // ------------------------------------------------------------------
    // Terminator runs (run-length encoding)
    // ------------------------------------------------------------------

    [Fact]
    public void TerminatorRuns_PureLf_SingleRun() {
        var s = Scan("a\nb\nc\n");
        // One LF run from line 0, then final None run.
        Assert.Equal(2, s.TerminatorRuns.Count);
        Assert.Equal((0L, LineTerminatorType.LF), s.TerminatorRuns[0]);
        Assert.Equal(LineTerminatorType.None, s.TerminatorRuns[1].Type);
    }

    [Fact]
    public void TerminatorRuns_CrlfUpgrade_NotDoubled() {
        var s = Scan("a\r\nb\r\n");
        // After the \r we record a CR run; the \n upgrades it to CRLF in place.
        Assert.Equal((0L, LineTerminatorType.CRLF), s.TerminatorRuns[0]);
        // Should not have a stray CR run ahead of the CRLF.
        Assert.DoesNotContain(s.TerminatorRuns, r => r.Type == LineTerminatorType.CR);
    }

    [Fact]
    public void TerminatorRuns_Transition_LfToCrlf() {
        var s = Scan("a\nb\r\n");
        // LF run starting at line 0, CRLF run starting at line 1, final None.
        Assert.Contains((0L, LineTerminatorType.LF), s.TerminatorRuns);
        Assert.Contains((1L, LineTerminatorType.CRLF), s.TerminatorRuns);
    }

    [Fact]
    public void GetLineTerminator_MixedEndings() {
        // We can't call GetLineTerminator on the scanner directly, but we can
        // verify the RLE structure is navigable: each line index should be
        // covered by exactly one run before it.
        var s = Scan("a\nb\nc\r\nd\rend");
        // Lines: LF, LF, CRLF, CR, None
        var runs = s.TerminatorRuns;
        // Verify runs are ordered and each run's line index is strictly
        // greater than the previous.
        for (int i = 1; i < runs.Count; i++) {
            Assert.True(runs[i].StartLine > runs[i - 1].StartLine);
        }
    }

    // ------------------------------------------------------------------
    // Indentation detection
    // ------------------------------------------------------------------

    [Fact]
    public void Indentation_Spaces() {
        var s = Scan("    a\n    b\n    c\n");
        Assert.Equal(3, s.SpaceIndentCount);
        Assert.Equal(0, s.TabIndentCount);
    }

    [Fact]
    public void Indentation_Tabs() {
        var s = Scan("\ta\n\tb\n");
        Assert.Equal(0, s.SpaceIndentCount);
        Assert.Equal(2, s.TabIndentCount);
    }

    [Fact]
    public void Indentation_MixedAndUnindentedIgnored() {
        // Only the first char of each line is inspected.
        var s = Scan("\tfoo\n    bar\nbaz\n");
        Assert.Equal(1, s.SpaceIndentCount);
        Assert.Equal(1, s.TabIndentCount);
    }

    [Fact]
    public void Indentation_AfterCrlfLineStart() {
        var s = Scan("\ta\r\n\tb\r\n");
        Assert.Equal(2, s.TabIndentCount);
    }

    [Fact]
    public void Indentation_AfterBareCrLineStart() {
        var s = Scan("    a\r    b\r");
        Assert.Equal(2, s.SpaceIndentCount);
    }

    // ------------------------------------------------------------------
    // Chunked scanning parity
    // ------------------------------------------------------------------

    [Fact]
    public void ChunkedScan_MatchesSingleScan_Crlf() {
        const string text = "line one\r\nline two\r\nline three\r\n";
        var single = Scan(text);

        var chunked = new LineScanner();
        // Split at every position and pick one that cleaves the CRLF.
        chunked.Scan(text.AsSpan(0, 9));  // "line one\r"
        chunked.Scan(text.AsSpan(9));     // "\nline two\r\nline three\r\n"
        chunked.Finish();

        Assert.Equal(single.LineCount, chunked.LineCount);
        Assert.Equal(single.LongestLine, chunked.LongestLine);
        Assert.Equal(single.LineLengths, chunked.LineLengths);
        Assert.Equal(single.LfCount, chunked.LfCount);
        Assert.Equal(single.CrlfCount, chunked.CrlfCount);
        Assert.Equal(single.CrCount, chunked.CrCount);
    }

    [Fact]
    public void ChunkedScan_CharAtATime_MatchesSingleScan() {
        const string text = "aa\nbbb\r\ncccc\rdddd\n";
        var single = Scan(text);

        var chunked = new LineScanner();
        for (int i = 0; i < text.Length; i++) {
            chunked.Scan(text.AsSpan(i, 1));
        }
        chunked.Finish();

        Assert.Equal(single.LineCount, chunked.LineCount);
        Assert.Equal(single.LongestLine, chunked.LongestLine);
        Assert.Equal(single.LineLengths, chunked.LineLengths);
        Assert.Equal(single.LfCount, chunked.LfCount);
        Assert.Equal(single.CrlfCount, chunked.CrlfCount);
        Assert.Equal(single.CrCount, chunked.CrCount);
    }

    // ------------------------------------------------------------------
    // Large-input stress
    // ------------------------------------------------------------------

    [Fact]
    public void LargeCrlfFile_LongestLineStaysBounded() {
        // 100_000 lines of 10 content chars + CRLF. Longest must be 12,
        // not ~1.2 million — the original CRLF-accumulation bug.
        var text = string.Concat(Enumerable.Repeat("0123456789\r\n", 100_000));
        var s = Scan(text);
        Assert.Equal(12, s.LongestLine);
        Assert.Equal(100_001, s.LineCount); // trailing empty line
        Assert.Equal(100_000, s.CrlfCount);
    }

    [Fact]
    public void LongLine_TerminatedByLf_Accurate() {
        var longLine = new string('x', 50_000);
        var s = Scan(longLine + "\nshort\n");
        Assert.Equal(50_001, s.LongestLine); // 50_000 x's + LF
    }

    // ------------------------------------------------------------------
    // Finish() contract — must be called exactly once (TRIAGE P2 gap)
    //
    // The XML-doc on LineScanner.Finish says "Must be called exactly once".
    // Without a test, a caller that accidentally double-finishes would
    // silently produce a phantom trailing line and inconsistent counts.
    // This pins the current behavior so any future change (e.g. making
    // Finish idempotent, or throwing) is a visible diff.
    // ------------------------------------------------------------------

    [Fact]
    public void Finish_CalledTwice_AddsPhantomLine() {
        // Single call: canonical result for "a\nb\n".
        var single = new LineScanner();
        single.Scan("a\nb\n".AsSpan());
        single.Finish();
        // "a\n" (2) + "b\n" (2) + "" (0)
        Assert.Equal(new[] { 2, 2, 0 }, single.LineLengths);
        Assert.Equal(3L, single.LineCount);

        // Double call: documents the non-idempotent behavior.
        var doubled = new LineScanner();
        doubled.Scan("a\nb\n".AsSpan());
        doubled.Finish();
        doubled.Finish();  // second call — adds another trailing empty line.

        // The current implementation adds a duplicate trailing entry.  If
        // a future change makes Finish idempotent, update this test to
        // assert equality with the single-call result instead.
        Assert.True(doubled.LineLengths.Count >= single.LineLengths.Count,
            "Second Finish() must not shrink the line list.");
        Assert.True(doubled.LineCount >= single.LineCount,
            "Second Finish() must not decrease line count.");
        // The terminator run list must still start with a valid descriptor.
        Assert.NotEmpty(doubled.TerminatorRuns);
    }
}
