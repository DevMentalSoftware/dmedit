using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.Core.Documents;
using DMEdit.Rendering.Layout;

namespace DMEdit.Rendering.Tests;

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

    private static LayoutResult DoLayoutLinesSlow(string text, double maxWidth = WideViewport) {
        var table = MakeTable(text);
        return Engine().LayoutLines(
            table, 0, table.LineCount, DefaultTypeface, FontSize,
            Brushes.Black, maxWidth, 0,
            useFastTextLayout: false);
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

    // (Pseudo-line layout tests removed — pseudo-line system no longer exists.)

    // -------------------------------------------------------------------------
    // Slow-path hardening — binary file viewing crash (real user report v0.5.231)
    // -------------------------------------------------------------------------
    //
    // The crash was: Avalonia's TextLayout PerformTextWrapping → ShapedTextRun.Split
    // throwing "Cannot split: requested length N consumes entire run." when
    // the user scrolled through a binary file.  Mono fast path bails on
    // control chars (c < 32), so binary lines hit the TextLayout slow path.
    //
    // SanitizeForTextLayout strips control chars (≠ tab) and lone surrogates
    // length-preservingly so offsets stay valid; MakeTextLayoutSafe wraps the
    // construct call with NoWrap retry + empty fallback for any leftover
    // crashes.  These tests assert the editor stays alive on hostile input.

    [AvaloniaFact]
    public void LayoutLines_BinaryGarbage_DoesNotThrow() {
        // Random low-ASCII bytes including NUL, BEL, ESC etc — typical
        // binary file content that bails the mono path.
        var binary = "\x00\x01\x02\x03\x04hello\x07\x08\x0B\x0C\x0Eworld\x1B\x1F";
        using var r = DoLayoutLines(binary, maxWidth: 80.0);
        Assert.Single(r.Lines);
    }

    [AvaloniaFact]
    public void LayoutLines_BinaryGarbage_PreservesCharLen() {
        // Length-preserving sanitization: replaced characters become a
        // single U+FFFD each, so CharLen still equals the input length.
        var binary = "\x00\x01\x02hi\x1B";
        using var r = DoLayoutLines(binary);
        Assert.Equal(binary.Length, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_LoneHighSurrogate_DoesNotThrow() {
        // Lone high surrogate without a paired low — illegal Unicode that
        // historically broke shaping.  Sanitizer replaces with U+FFFD.
        var bad = "abc\uD800def";
        using var r = DoLayoutLines(bad, maxWidth: 60.0);
        Assert.Single(r.Lines);
        Assert.Equal(bad.Length, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_LoneLowSurrogate_DoesNotThrow() {
        var bad = "abc\uDC00def";
        using var r = DoLayoutLines(bad);
        Assert.Single(r.Lines);
        Assert.Equal(bad.Length, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_ValidSurrogatePair_PreservedThroughSanitize() {
        // U+1F600 GRINNING FACE = D83D DE00 — must round-trip unchanged.
        var emoji = "hi \uD83D\uDE00 there";
        using var r = DoLayoutLines(emoji);
        Assert.Single(r.Lines);
        Assert.Equal(emoji.Length, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLines_BinaryGarbage_NarrowWrap_DoesNotThrow() {
        // The original crash signature: long line, narrow wrap width, mixed
        // control chars and high bytes — the combination that exercises
        // PerformTextWrapping the most.  Skip CR/LF so it stays one line.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++) {
            var c = (char)(i % 256);
            if (c == '\r' || c == '\n') c = '_';
            sb.Append(c);
        }
        using var r = DoLayoutLines(sb.ToString(), maxWidth: 50.0);
        Assert.Single(r.Lines);
    }

    [AvaloniaFact]
    public void SanitizeForTextLayout_CleanText_ReturnsSameInstance() {
        var clean = "hello world";
        Assert.Same(clean, TextLayoutEngine.SanitizeForTextLayout(clean));
    }

    [AvaloniaFact]
    public void SanitizeForTextLayout_PreservesTab() {
        var withTab = "a\tb";
        Assert.Same(withTab, TextLayoutEngine.SanitizeForTextLayout(withTab));
    }

    [AvaloniaFact]
    public void SanitizeForTextLayout_ReplacesNul() {
        // Note: \u0000 not \x00 — C# \x is variable-length hex and would eat
        // the trailing 'b' as another hex digit.
        var bad = "a\u0000b";
        Assert.Equal("a\uFFFDb", TextLayoutEngine.SanitizeForTextLayout(bad));
    }

    [AvaloniaFact]
    public void SanitizeForTextLayout_ReplacesLoneHighSurrogate() {
        var bad = "a\uD800b";
        Assert.Equal("a\uFFFDb", TextLayoutEngine.SanitizeForTextLayout(bad));
    }

    [AvaloniaFact]
    public void SanitizeForTextLayout_ReplacesLoneLowSurrogate() {
        var bad = "a\uDC00b";
        Assert.Equal("a\uFFFDb", TextLayoutEngine.SanitizeForTextLayout(bad));
    }

    [AvaloniaFact]
    public void SanitizeForTextLayout_PreservesValidSurrogatePair() {
        var good = "a\uD83D\uDE00b";
        Assert.Same(good, TextLayoutEngine.SanitizeForTextLayout(good));
    }

    // -------------------------------------------------------------------------
    // Slow-path-only variants — simulate user setting "Disable fast path" or
    // the previous beta where TextLayout was the only path.  Same hostile
    // inputs as above but with useFastTextLayout: false so even monospace
    // ASCII routes through Avalonia.
    // -------------------------------------------------------------------------

    [AvaloniaFact]
    public void LayoutLinesSlow_BinaryGarbage_DoesNotThrow() {
        var binary = "\x00\x01\x02\x03\x04hello\x07\x08\x0B\x0C\x0Eworld\x1B\x1F";
        using var r = DoLayoutLinesSlow(binary, maxWidth: 80.0);
        Assert.Single(r.Lines);
        Assert.Equal(binary.Length, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLinesSlow_LoneSurrogate_DoesNotThrow() {
        var bad = "abc\uD800def\uDC00ghi";
        using var r = DoLayoutLinesSlow(bad, maxWidth: 60.0);
        Assert.Single(r.Lines);
        Assert.Equal(bad.Length, r.Lines[0].CharLen);
    }

    [AvaloniaFact]
    public void LayoutLinesSlow_BinaryGarbage_NarrowWrap_DoesNotThrow() {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++) {
            var c = (char)(i % 256);
            if (c == '\r' || c == '\n') c = '_';
            sb.Append(c);
        }
        using var r = DoLayoutLinesSlow(sb.ToString(), maxWidth: 50.0);
        Assert.Single(r.Lines);
    }

    [AvaloniaFact]
    public void LayoutLinesSlow_PlainAsciiStillWorks() {
        // Sanity check: forcing the slow path doesn't break the happy path.
        using var r = DoLayoutLinesSlow("hello world");
        Assert.Single(r.Lines);
        Assert.Equal(11, r.Lines[0].CharLen);
    }

}
