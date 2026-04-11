using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using DMEdit.App.Controls;
using DMEdit.Core.Documents;

namespace DMEdit.App.Tests;

/// <summary>
/// Tests that <see cref="EditorControl.ComputeLineRowCount"/> and the
/// renderer agree on the number of visual rows for various line content
/// patterns.  The Debug invariant in LayoutWindowed catches divergence
/// at runtime, but these tests exercise specific content patterns that
/// are known to be tricky: tabs, mixed ASCII/non-ASCII, trailing spaces,
/// lines exactly at the wrap boundary, and empty lines.
///
/// Each test creates a document, forces a layout, and verifies no
/// Debug.Assert fires (the test host converts Debug.Assert failures
/// to DebugAssertException).
/// </summary>
public class RowCountConsistencyTests {
    private const double VpW = 600;
    private const double VpH = 400;

    private static EditorControl CreateEditor(Document doc) {
        var editor = new EditorControl {
            Document = doc,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 14,
            Width = VpW,
            Height = VpH,
            WrapLines = true, // wrapping on — the mode where row counts matter
        };
        editor.Measure(new Size(VpW, VpH));
        editor.Arrange(new Rect(0, 0, VpW, VpH));
        return editor;
    }

    private static void Relayout(EditorControl editor) {
        editor.Measure(new Size(VpW, VpH));
        editor.Arrange(new Rect(0, 0, VpW, VpH));
    }

    /// <summary>
    /// Creates a doc, scrolls through it line by line, forcing layout
    /// for every viewport position.  If any line's rendered row count
    /// disagrees with ComputeLineRowCount, the Debug invariant in
    /// LayoutWindowed fires as a DebugAssertException.
    /// </summary>
    private static void WalkEntireDoc(Document doc) {
        var editor = CreateEditor(doc);
        var lineCount = doc.Table.LineCount;
        var rh = editor.RowHeightValue;

        // Walk through the document in viewport-sized steps.
        var step = Math.Max(1, (int)(VpH / rh) - 2);
        for (var line = 0; line < lineCount; line += step) {
            editor.GoToPosition(doc.Table.LineStartOfs(
                Math.Min(line, lineCount - 1)));
            Relayout(editor);
        }
        // Also visit the very end.
        editor.GoToPosition(doc.Table.Length);
        Relayout(editor);
    }

    // ------------------------------------------------------------------
    //  Plain ASCII lines of varying lengths
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void PlainAscii_VaryingLengths_NoRowCountMismatch() {
        var sb = new StringBuilder();
        // Lines from 1 to 200 chars — some fit in one row, others wrap.
        for (var len = 1; len <= 200; len++) {
            sb.Append(new string('a', len));
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }

    // ------------------------------------------------------------------
    //  Lines exactly at wrap boundary (±1 char)
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void ExactWrapBoundary_NoRowCountMismatch() {
        var editor = CreateEditor(new Document());
        // Determine chars per row from a throwaway layout.
        var cpr = (int)(VpW / editor.RowHeightValue); // rough approximation
        // Use actual GetCharsPerRow for accuracy — but it's private.
        // Instead, test a range around the likely boundary.
        var sb = new StringBuilder();
        for (var len = Math.Max(1, cpr - 5); len <= cpr + 10; len++) {
            sb.Append(new string('b', len));
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }

    // ------------------------------------------------------------------
    //  Tab-containing lines
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void TabLines_VaryingTabPositions_NoRowCountMismatch() {
        var sb = new StringBuilder();
        for (var i = 0; i < 50; i++) {
            // Tab at different column positions.
            var prefix = new string('a', i % 20);
            sb.Append(prefix);
            sb.Append('\t');
            sb.Append(new string('b', 60));
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }

    [AvaloniaFact]
    public void MultipleTabs_NoRowCountMismatch() {
        var sb = new StringBuilder();
        for (var i = 0; i < 50; i++) {
            // Multiple tabs creating wide gaps.
            sb.Append("col1\tcol2\tcol3\tcol4\tpadding text here\n");
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }

    // ------------------------------------------------------------------
    //  Trailing spaces (can affect TextLayout wrapping)
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void TrailingSpaces_NoRowCountMismatch() {
        var sb = new StringBuilder();
        for (var i = 0; i < 50; i++) {
            var text = new string('c', 55);
            var spaces = new string(' ', i % 20 + 1);
            sb.Append(text + spaces + '\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }

    // ------------------------------------------------------------------
    //  Empty lines interspersed with content
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void EmptyLines_NoRowCountMismatch() {
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++) {
            if (i % 3 == 0) {
                sb.Append('\n'); // empty line
            } else {
                sb.Append(new string('d', 80));
                sb.Append('\n');
            }
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }

    // ------------------------------------------------------------------
    //  Lines with spaces that create word-wrap break points
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void WordWrapBreaks_NoRowCountMismatch() {
        var sb = new StringBuilder();
        for (var i = 0; i < 50; i++) {
            // Words of varying length — creates different break points.
            for (var w = 0; w < 15; w++) {
                var wordLen = (i + w) % 10 + 3;
                sb.Append(new string('e', wordLen));
                sb.Append(' ');
            }
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }

    // ------------------------------------------------------------------
    //  Very long lines (1000+ chars)
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void VeryLongLines_NoRowCountMismatch() {
        var sb = new StringBuilder();
        for (var i = 0; i < 10; i++) {
            sb.Append(new string('f', 1000 + i * 500));
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }

    // ------------------------------------------------------------------
    //  Mixed content: short, long, empty, tab, spaces
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void MixedContent_NoRowCountMismatch() {
        var sb = new StringBuilder();
        sb.Append("short\n");
        sb.Append(new string('a', 200) + "\n");
        sb.Append("\n");
        sb.Append("has\ttab\there\n");
        sb.Append(new string(' ', 80) + "\n");
        sb.Append("normal line with words and stuff\n");
        sb.Append(new string('z', 500) + "\n");
        sb.Append("\t\t\t\n");
        for (var i = 0; i < 30; i++) {
            sb.Append($"line {i:D3} with some content\n");
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }

    // ------------------------------------------------------------------
    //  FindNext through mixed content (exercises ComputeRowOfCharInLine)
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void FindThroughMixedContent_NoRowCountMismatch() {
        var sb = new StringBuilder();
        for (var i = 0; i < 50; i++) {
            if (i % 5 == 0) {
                sb.Append($"MARKER{i:D2} " + new string('g', 100) + "\n");
            } else if (i % 5 == 1) {
                sb.Append($"MARKER{i:D2}\t" + new string('h', 50) + "\n");
            } else if (i % 5 == 2) {
                sb.Append($"MARKER{i:D2}\n"); // short
            } else if (i % 5 == 3) {
                sb.Append($"MARKER{i:D2} " + new string('i', 200) + "\n");
            } else {
                sb.Append($"MARKER{i:D2} normal length line\n");
            }
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        doc.Selection = Selection.Collapsed(0);
        var editor = CreateEditor(doc);

        editor.LastSearchTerm = "MARKER";
        for (var step = 0; step < 55; step++) {
            if (!editor.FindNext()) break;
            Relayout(editor);
            // The invariant check fires during layout if row counts disagree.
        }
    }

    // ------------------------------------------------------------------
    //  Scroll through entire document (forces all lines through layout)
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void ScrollEntireDoc_VaryingContent_NoRowCountMismatch() {
        var sb = new StringBuilder();
        var rng = new Random(42); // deterministic
        for (var i = 0; i < 200; i++) {
            var len = rng.Next(1, 300);
            var hasTabs = rng.Next(5) == 0;
            if (hasTabs) {
                sb.Append(new string('j', len / 3));
                sb.Append('\t');
                sb.Append(new string('k', len / 3));
                sb.Append('\t');
                sb.Append(new string('l', len / 3));
            } else {
                sb.Append(new string('m', len));
            }
            sb.Append('\n');
        }
        var doc = new Document();
        doc.Insert(sb.ToString());
        WalkEntireDoc(doc);
    }
}
