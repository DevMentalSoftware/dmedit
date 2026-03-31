using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DevMentalMd.Core.Documents;
using DevMentalMd.Rendering.Layout;

namespace DevMentalMd.Rendering.Tests;

public class TextLayoutEngineTests {
    private static readonly Typeface DefaultTypeface = new(new FontFamily("Courier New"));
    private const double FontSize = 14.0;
    private const double WideViewport = 10000.0;

    private static TextLayoutEngine Engine() => new();

    private static LayoutResult DoLayout(string text, double maxWidth = WideViewport) =>
        DoLayoutLines(text, maxWidth);

    // -------------------------------------------------------------------------
    // Single line, no wrap
    // -------------------------------------------------------------------------

    [AvaloniaFact]
    public void SingleLine_ProducesOneLine() {
        using var r = DoLayout("hello");
        Assert.Single(r.Lines);
    }

    [AvaloniaFact]
    public void SingleLine_CharStartIsZero() {
        using var r = DoLayout("hello");
        Assert.Equal(0, r.Lines[0].CharStart);
    }

    [AvaloniaFact]
    public void SingleLine_CharLenMatchesText() {
        using var r = DoLayout("hello");
        Assert.Equal(5, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void SingleLine_RowIsZero() {
        using var r = DoLayout("hello");
        Assert.Equal(0, r.Lines[0].Row);
    }

    [AvaloniaFact]
    public void SingleLine_HeightInRowsIsOne() {
        using var r = DoLayout("hello");
        Assert.Equal(1, r.Lines[0].HeightInRows);
    }

    // -------------------------------------------------------------------------
    // Multi-line (newlines)
    // -------------------------------------------------------------------------

    [AvaloniaFact]
    public void TwoLines_SplitAtNewline() {
        using var r = DoLayout("hello\nworld");
        Assert.Equal(2, r.Lines.Count);
    }

    [AvaloniaFact]
    public void TwoLines_CharOffsetsAreCorrect() {
        using var r = DoLayout("hello\nworld");
        Assert.Equal(0, r.Lines[0].CharStart);
        Assert.Equal(5, r.Lines[0].CharLen);
        Assert.Equal(6, r.Lines[1].CharStart); // skip '\n'
        Assert.Equal(5, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void TwoLines_SecondLineRowIsGreater() {
        using var r = DoLayout("hello\nworld");
        Assert.True(r.Lines[1].Row > r.Lines[0].Row);
    }

    [AvaloniaFact]
    public void TrailingNewline_ProducesExtraEmptyLine() {
        using var r = DoLayout("hello\n");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(0, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void EmptyString_ProducesOneLine() {
        using var r = DoLayout("");
        Assert.Single(r.Lines);
        Assert.Equal(0, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void EmptyString_LineHasPositiveHeightInRows() {
        using var r = DoLayout("");
        Assert.Equal(1, r.Lines[0].HeightInRows);
    }

    [AvaloniaFact]
    public void CrLf_CountedAsOneNewline() {
        using var r = DoLayout("a\r\nb");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(0, r.Lines[0].CharStart);
        Assert.Equal(1, r.Lines[0].CharLen);
        Assert.Equal(3, r.Lines[1].CharStart); // skip '\r\n'
        Assert.Equal(1, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void TotalHeight_SumOfLineHeightsInRows() {
        using var r = DoLayout("hello\nworld");
        var expectedRows = r.Lines.Sum(l => l.HeightInRows);
        Assert.Equal(expectedRows * r.RowHeight, r.TotalHeight, precision: 1);
    }

    // -------------------------------------------------------------------------
    // Word wrap
    // -------------------------------------------------------------------------

    [AvaloniaFact]
    public void WrapAtNarrowWidth_TextLayoutWraps() {
        // With narrow width the TextLayout itself wraps; the LayoutLine count is still 1
        // (one per logical line) but the line height increases.
        using var rNarrow = DoLayout("abcdefghijklmnop", maxWidth: 20.0);
        using var rWide = DoLayout("abcdefghijklmnop", maxWidth: WideViewport);
        // Narrow layout has more internal visual rows → greater HeightInRows
        Assert.True(rNarrow.Lines[0].HeightInRows >= rWide.Lines[0].HeightInRows,
            $"Expected narrow rows {rNarrow.Lines[0].HeightInRows} >= wide rows {rWide.Lines[0].HeightInRows}");
    }

    // -------------------------------------------------------------------------
    // HitTest
    // -------------------------------------------------------------------------

    [AvaloniaFact]
    public void HitTest_AtOrigin_ReturnsZero() {
        using var r = DoLayout("hello");
        var ofs = Engine().HitTest(new Point(0, 0), r);
        Assert.Equal(0, ofs);
    }

    [AvaloniaFact]
    public void HitTest_FarRight_ReturnsLastChar() {
        using var r = DoLayout("hello");
        var ofs = Engine().HitTest(new Point(WideViewport, 0), r);
        Assert.Equal(5, ofs);
    }

    [AvaloniaFact]
    public void HitTest_SecondLine_ReturnsOffsetInSecondLine() {
        using var r = DoLayout("hello\nworld");
        var ofs = Engine().HitTest(new Point(0, r.Lines[1].Row * r.RowHeight + 1), r);
        Assert.True(ofs >= 6, $"Expected offset >= 6 but got {ofs}");
    }

    // -------------------------------------------------------------------------
    // GetCaretBounds
    // -------------------------------------------------------------------------

    [AvaloniaFact]
    public void GetCaretBounds_AtZero_IsOnFirstLine() {
        using var r = DoLayout("hello");
        var rect = Engine().GetCaretBounds(0, r);
        Assert.Equal(0.0, rect.Y);
        Assert.True(rect.Height > 0);
    }

    [AvaloniaFact]
    public void GetCaretBounds_AtEnd_StaysOnFirstLine() {
        using var r = DoLayout("hello");
        var rect = Engine().GetCaretBounds(5, r);
        Assert.Equal(0.0, rect.Y);
    }

    [AvaloniaFact]
    public void GetCaretBounds_SecondLine_HasHigherY() {
        using var r = DoLayout("hello\nworld");
        var r0 = Engine().GetCaretBounds(0, r);
        var r1 = Engine().GetCaretBounds(6, r); // start of "world"
        Assert.True(r1.Y > r0.Y);
    }

    [AvaloniaFact]
    public void HitTest_RoundTrip_CaretBounds() {
        var text = "hello world";
        using var r = DoLayout(text);
        var eng = Engine();
        for (var ofs = 0; ofs <= text.Length; ofs++) {
            var rect = eng.GetCaretBounds(ofs, r);
            var hit = eng.HitTest(new Point(rect.X + 0.5, rect.Y + rect.Height / 2), r);
            Assert.True(Math.Abs(hit - ofs) <= 1,
                $"Round-trip failed at offset {ofs}: HitTest returned {hit}");
        }
    }

    // -------------------------------------------------------------------------
    // LayoutLines — line-at-a-time from PieceTable
    // -------------------------------------------------------------------------

    private static PieceTable MakeTable(string text) {
        var t = new PieceTable();
        if (text.Length > 0) t.Insert(0, text);
        return t;
    }

    private static LayoutResult DoLayoutLines(string text, double maxWidth = WideViewport) {
        var table = MakeTable(text);
        return Engine().LayoutLines(
            table, 0, table.LineCount, DefaultTypeface, FontSize,
            Brushes.Black, maxWidth, 0);
    }

    [AvaloniaFact]
    public void LayoutLines_SingleLine() {
        using var r = DoLayoutLines("hello");
        Assert.Single(r.Lines);
        Assert.Equal(0, r.Lines[0].CharStart);
        Assert.Equal(5, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_TwoLines_LF() {
        using var r = DoLayoutLines("hello\nworld");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(0, r.Lines[0].CharStart);
        Assert.Equal(5, r.Lines[0].CharLen);
        Assert.Equal(6, r.Lines[1].CharStart);
        Assert.Equal(5, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_TwoLines_CRLF() {
        using var r = DoLayoutLines("a\r\nb");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(0, r.Lines[0].CharStart);
        Assert.Equal(1, r.Lines[0].CharLen);
        Assert.Equal(3, r.Lines[1].CharStart);
        Assert.Equal(1, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_TwoLines_CR() {
        using var r = DoLayoutLines("a\rb");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(0, r.Lines[0].CharStart);
        Assert.Equal(1, r.Lines[0].CharLen);
        Assert.Equal(2, r.Lines[1].CharStart);
        Assert.Equal(1, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_TrailingNewline_ProducesExtraEmptyLine() {
        using var r = DoLayoutLines("hello\n");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(0, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_EmptyString() {
        using var r = DoLayoutLines("");
        Assert.Single(r.Lines);
        Assert.Equal(0, r.Lines[0].CharLen);
        Assert.Equal(1, r.Lines[0].HeightInRows);
    }

    [AvaloniaFact]
    public void LayoutLines_ThreeLines_RowsIncrement() {
        using var r = DoLayoutLines("a\nb\nc");
        Assert.Equal(3, r.Lines.Count);
        Assert.True(r.Lines[1].Row > r.Lines[0].Row);
        Assert.True(r.Lines[2].Row > r.Lines[1].Row);
    }

    [AvaloniaFact]
    public void LayoutLines_MixedEndings() {
        using var r = DoLayoutLines("a\nb\r\nc\rd");
        Assert.Equal(4, r.Lines.Count);
        Assert.Equal(0, r.Lines[0].CharStart);  // "a"
        Assert.Equal(1, r.Lines[0].CharLen);
        Assert.Equal(2, r.Lines[1].CharStart);  // "b"
        Assert.Equal(1, r.Lines[1].CharLen);
        Assert.Equal(5, r.Lines[2].CharStart);  // "c"
        Assert.Equal(1, r.Lines[2].CharLen);
        Assert.Equal(7, r.Lines[3].CharStart);  // "d"
        Assert.Equal(1, r.Lines[3].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_PartialRange() {
        // Layout only lines 1-2 out of 4
        var table = MakeTable("aaa\nbbb\nccc\nddd");
        using var r = Engine().LayoutLines(
            table, 1, 3, DefaultTypeface, FontSize,
            Brushes.Black, WideViewport, table.LineStartOfs(1));
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(3, r.Lines[0].CharLen); // "bbb"
        Assert.Equal(3, r.Lines[1].CharLen); // "ccc"
    }

    [AvaloniaFact]
    public void LayoutLines_HitTest_RoundTrip() {
        using var r = DoLayoutLines("hello world");
        var eng = Engine();
        for (var ofs = 0; ofs <= 11; ofs++) {
            var rect = eng.GetCaretBounds(ofs, r);
            var hit = eng.HitTest(new Point(rect.X + 0.5, rect.Y + rect.Height / 2), r);
            Assert.True(Math.Abs(hit - ofs) <= 1,
                $"Round-trip failed at offset {ofs}: HitTest returned {hit}");
        }
    }

    // -------------------------------------------------------------------------
    // Pseudo-line layout (MaxPseudoLine = 10)
    // -------------------------------------------------------------------------

    private const int T = 10; // test MaxPseudoLine

    private static PieceTable MakeTableSmall(string text) {
        var t = new PieceTable(maxPseudoLine: T);
        if (text.Length > 0) t.Insert(0, text);
        return t;
    }

    private static LayoutResult DoLayoutSmall(string text) {
        var table = MakeTableSmall(text);
        return Engine().LayoutLines(
            table, 0, table.LineCount, DefaultTypeface, FontSize,
            Brushes.Black, WideViewport, 0);
    }

    [AvaloniaFact]
    public void PseudoLine_ContentOnly_NoExtraChar() {
        // 11 chars → pseudo-split [10] + [1].
        // Line 0 should have CharLen=10, line 1 CharLen=1.
        using var r = DoLayoutSmall(new string('a', T + 1));
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(T, r.Lines[0].CharLen);
        Assert.Equal(1, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void PseudoLine_CharStartsAreCorrect() {
        // 11 chars → pseudo-split [10] + [1].
        // CharStart is doc-space: line 0 = 0, line 1 = 11 (10 content + 1 virtual terminator).
        using var r = DoLayoutSmall(new string('a', T + 1));
        Assert.Equal(0, r.Lines[0].CharStart);
        Assert.Equal(T + 1, r.Lines[1].CharStart);
    }

    [AvaloniaFact]
    public void PseudoLine_WithLF_TerminatorNotInContent() {
        // 10 content + LF — no pseudo-split needed.
        // CharLen should be 10, not 11.
        using var r = DoLayoutSmall(new string('a', T) + "\n");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(T, r.Lines[0].CharLen);
        Assert.Equal(0, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void PseudoLine_WithCRLF_TerminatorNotInContent() {
        // 10 content + CRLF — no pseudo-split needed.
        // CharLen should be 10, not 12.
        using var r = DoLayoutSmall(new string('a', T) + "\r\n");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(T, r.Lines[0].CharLen);
        Assert.Equal(0, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void PseudoLine_11Chars_LF_ThreeLines() {
        // 11 content + LF → pseudo [10] + [1+LF] + [empty]
        using var r = DoLayoutSmall(new string('a', T + 1) + "\n");
        Assert.Equal(3, r.Lines.Count);
        Assert.Equal(T, r.Lines[0].CharLen);   // pseudo
        Assert.Equal(1, r.Lines[1].CharLen);    // content before LF
        Assert.Equal(0, r.Lines[2].CharLen);    // empty after LF
    }

    [AvaloniaFact]
    public void PseudoLine_11Chars_CRLF_ThreeLines() {
        // 11 content + CRLF → pseudo [10] + [1+CRLF] + [empty]
        using var r = DoLayoutSmall(new string('a', T + 1) + "\r\n");
        Assert.Equal(3, r.Lines.Count);
        Assert.Equal(T, r.Lines[0].CharLen);   // pseudo
        Assert.Equal(1, r.Lines[1].CharLen);    // content before CRLF
        Assert.Equal(0, r.Lines[2].CharLen);    // empty after CRLF
    }

    [AvaloniaFact]
    public void PseudoLine_CharStartGap_RealNewline() {
        // "hello\nworld" with MPL=10: no pseudo-split.
        // Line 0: CharStart=0, CharLen=5. Line 1: CharStart=6, CharLen=5.
        // Gap of 1 between CharEnd(5) and CharStart(6) = the LF.
        using var r = DoLayoutSmall("hello\nworld");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(5, r.Lines[0].CharLen);
        Assert.Equal(6, r.Lines[1].CharStart);
        Assert.True(r.Lines[0].CharEnd < r.Lines[1].CharStart,
            "Real newline should create a gap between CharEnd and next CharStart");
    }

    [AvaloniaFact]
    public void PseudoLine_CharStartGap_Pseudo() {
        // 20 chars, no newline, MPL=10 → two pseudo-lines.
        // Doc-space: Line 0: CharStart=0, CharLen=10. Line 1: CharStart=11, CharLen=10.
        // There IS a 1-char gap: CharEnd(10) + 1 == CharStart(11).
        // This virtual gap is the whole point of dual offsets.
        using var r = DoLayoutSmall(new string('a', T * 2));
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(T, r.Lines[0].CharLen);
        Assert.Equal(T + 1, r.Lines[1].CharStart);
        Assert.Equal(r.Lines[0].CharEnd + 1, r.Lines[1].CharStart);
    }

    [AvaloniaFact]
    public void PseudoLine_9Chars_CRLF_NoSplit() {
        // 9 content + CRLF = 11 total. Content < MPL, so no pseudo-split.
        using var r = DoLayoutSmall(new string('a', T - 1) + "\r\n");
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(T - 1, r.Lines[0].CharLen);
        Assert.Equal(0, r.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void PseudoLine_InsertPastMPL_LayoutSplits() {
        // Start with 10 chars (single line), insert one more → layout should show 2 lines
        var table = new PieceTable(maxPseudoLine: T);
        table.Insert(0, new string('a', T));

        // Layout before: 1 line
        using var r1 = Engine().LayoutLines(
            table, 0, table.LineCount, DefaultTypeface, FontSize,
            Brushes.Black, WideViewport, 0);
        Assert.Single(r1.Lines);
        Assert.Equal(T, r1.Lines[0].CharLen);

        // Insert one more char
        table.Insert(T, "b");

        // Layout after: should be 2 lines
        Assert.Equal(2, table.LineCount);
        using var r2 = Engine().LayoutLines(
            table, 0, table.LineCount, DefaultTypeface, FontSize,
            Brushes.Black, WideViewport, 0);
        Assert.Equal(2, r2.Lines.Count);
        Assert.Equal(T, r2.Lines[0].CharLen);
        Assert.Equal(1, r2.Lines[1].CharLen);
    }

    [AvaloniaFact]
    public void PseudoLine_ExactlyMPL_NoNewline_SingleLine() {
        // Exactly 10 chars, no newline → single line, no split.
        using var r = DoLayoutSmall(new string('a', T));
        Assert.Single(r.Lines);
        Assert.Equal(T, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void PseudoLine_TextContent_NoOverlap() {
        // "abcdefghijK" with MPL=10 → line 0 should have "abcdefghij", line 1 "K"
        var table = MakeTableSmall("abcdefghijK");
        using var r = Engine().LayoutLines(
            table, 0, table.LineCount, DefaultTypeface, FontSize,
            Brushes.Black, WideViewport, 0);
        Assert.Equal(2, r.Lines.Count);
        // Line 0: 10 chars "abcdefghij"
        Assert.Equal(10, r.Lines[0].CharLen);
        Assert.Equal("abcdefghij", table.GetText(0, r.Lines[0].CharLen));
        // Line 1: 1 char "K"
        Assert.Equal(1, r.Lines[1].CharLen);
        var line1Start = table.LineStartOfs(1);
        Assert.Equal("K", table.GetText(line1Start, r.Lines[1].CharLen));
    }
}
