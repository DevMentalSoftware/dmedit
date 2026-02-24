using Avalonia.Media.TextFormatting;

namespace DevMentalMd.Rendering.Layout;

/// <summary>
/// A single laid-out logical line of text (one per `\n`-separated paragraph).
/// Word-wrap within the line is handled internally by <see cref="TextLayout"/>.
/// Must be disposed when the layout is invalidated.
/// </summary>
public sealed class LayoutLine : IDisposable {
    private bool _disposed;

    /// <summary>Logical character offset of the first character in this line (in the full document string).</summary>
    public int CharStart { get; }

    /// <summary>Number of characters in this line, excluding any trailing newline character.</summary>
    public int CharLen { get; }

    /// <summary>Vertical position of the top of this line, in layout (document) coordinates.</summary>
    public double Y { get; }

    /// <summary>Height of this line (may span multiple visual rows when word-wrap is active).</summary>
    public double Height => Layout.Height;

    /// <summary>Exclusive end offset: CharStart + CharLen.</summary>
    public int CharEnd => CharStart + CharLen;

    /// <summary>
    /// The Avalonia TextLayout for this line.
    /// Used for rendering (<c>Layout.Draw(context, origin)</c>) and hit-testing.
    /// </summary>
    public TextLayout Layout { get; }

    internal LayoutLine(int charStart, int charLen, double y, TextLayout layout) {
        CharStart = charStart;
        CharLen = charLen;
        Y = y;
        Layout = layout;
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        Layout.Dispose();
    }
}
