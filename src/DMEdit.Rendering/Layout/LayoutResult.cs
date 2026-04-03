namespace DMEdit.Rendering.Layout;

/// <summary>
/// Owns the <see cref="LayoutLine"/> objects produced by a <see cref="TextLayoutEngine"/> call.
/// Disposing this releases the underlying Avalonia <c>TextLayout</c> objects.
/// </summary>
public sealed class LayoutResult : IDisposable {
    private bool _disposed;

    public IReadOnlyList<LayoutLine> Lines { get; }

    /// <summary>Pixel height of a single visual row.  Multiply by <see cref="LayoutLine.Row"/>
    /// or <see cref="LayoutLine.HeightInRows"/> to convert row units to pixels.</summary>
    public double RowHeight { get; }

    /// <summary>Total height of all lines stacked vertically, in pixels.</summary>
    public double TotalHeight { get; }

    /// <summary>Total height of all lines in visual-row units.</summary>
    public int TotalRows { get; }

    /// <summary>
    /// The document-level character offset at which this layout window begins.
    /// <see cref="LayoutLine.CharStart"/> values are relative to this base.
    /// Currently always 0 (full-document layout); set to a non-zero value
    /// when windowed rendering is introduced.
    /// </summary>
    public long ViewportBase { get; }

    /// <summary>
    /// The logical line index of the first line in this layout window.
    /// Used by the gutter to avoid an expensive <c>LineFromOfs</c> lookup.
    /// </summary>
    public long TopLine { get; set; }

    public LayoutResult(IReadOnlyList<LayoutLine> lines, double rowHeight, long viewportBase = 0L) {
        Lines = lines;
        RowHeight = rowHeight;
        ViewportBase = viewportBase;
        TotalRows = lines.Count > 0
            ? lines[^1].Row + lines[^1].HeightInRows
            : 0;
        TotalHeight = TotalRows * rowHeight;
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        foreach (var line in Lines) {
            line.Dispose();
        }
    }
}
