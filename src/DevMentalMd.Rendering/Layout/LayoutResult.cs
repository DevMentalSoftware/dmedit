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

    internal LayoutResult(IReadOnlyList<LayoutLine> lines) {
        Lines = lines;
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
