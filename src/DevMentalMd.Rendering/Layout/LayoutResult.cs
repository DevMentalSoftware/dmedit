namespace DevMentalMd.Rendering.Layout;

/// <summary>
/// Owns the <see cref="LayoutLine"/> objects produced by a <see cref="TextLayoutEngine"/> call.
/// Disposing this releases the underlying Avalonia <c>TextLayout</c> objects.
/// </summary>
public sealed class LayoutResult : IDisposable {
    private bool _disposed;

    public IReadOnlyList<LayoutLine> Lines { get; }

    /// <summary>Total height of all lines stacked vertically.</summary>
    public double TotalHeight { get; }

    /// <summary>
    /// The document-level character offset at which this layout window begins.
    /// <see cref="LayoutLine.CharStart"/> values are relative to this base.
    /// Currently always 0 (full-document layout); set to a non-zero value
    /// when windowed rendering is introduced.
    /// </summary>
    public long ViewportBase { get; }

    internal LayoutResult(IReadOnlyList<LayoutLine> lines, long viewportBase = 0L) {
        Lines = lines;
        ViewportBase = viewportBase;
        TotalHeight = lines.Count > 0
            ? lines[^1].Y + lines[^1].Height
            : 0.0;
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
