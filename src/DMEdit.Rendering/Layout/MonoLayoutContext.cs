using Avalonia.Media;

namespace DMEdit.Rendering.Layout;

/// <summary>
/// Shared monospace fast-path state for one <see cref="LayoutResult"/>.
/// All <see cref="MonoLineLayout"/> instances built for a single
/// <c>LayoutLines</c> call share one <see cref="MonoLayoutContext"/> so the
/// glyph-index cache is amortized across the visible window.
/// </summary>
/// <remarks>
/// Engages only when the resolved typeface is monospace
/// (<see cref="FontMetrics.IsFixedPitch"/>) and exposes a glyph typeface.
/// Tab and control characters defeat the fast path on a per-line basis —
/// see <see cref="MonoLineLayout"/>.
/// </remarks>
public sealed class MonoLayoutContext {
    /// <summary>The resolved glyph typeface used for all glyph lookups.</summary>
    public IGlyphTypeface GlyphTypeface { get; }

    /// <summary>Em size in DIPs (the same value passed to <see cref="GlyphRun.FontRenderingEmSize"/>).</summary>
    public double FontSize { get; }

    /// <summary>Width of a single monospace cell in DIPs.</summary>
    public double CharWidth { get; }

    /// <summary>Pixel offset from a row's top to its baseline.</summary>
    public double Baseline { get; }

    /// <summary>Pixel height of one visual row (matches <see cref="LayoutResult.RowHeight"/>).</summary>
    public double RowHeight { get; }

    /// <summary>Hanging-indent X offset applied to wrapped continuation rows.</summary>
    public double HangingIndentPx { get; }

    /// <summary>Hanging indent expressed in character cells.</summary>
    public int HangingIndentChars { get; }

    /// <summary>Foreground brush used by default when drawing a row.</summary>
    public IBrush Foreground { get; }

    private readonly ushort[] _asciiGlyphs = new ushort[128];
    private readonly Dictionary<int, ushort> _extraGlyphs = new();
    private readonly ushort _fallbackGlyph;

    public MonoLayoutContext(
        IGlyphTypeface glyphTypeface,
        double fontSize,
        double rowHeight,
        int hangingIndentChars,
        IBrush foreground) {
        GlyphTypeface = glyphTypeface;
        FontSize = fontSize;
        RowHeight = rowHeight;
        Foreground = foreground;

        var emHeight = (double)glyphTypeface.Metrics.DesignEmHeight;
        // Avalonia's FontMetrics.Ascent is the distance above baseline in
        // design em units.  Sign convention varies (positive in OpenType,
        // negative in some Y-down graphics conventions).  We just want the
        // positive pixel distance from row top to baseline, so Math.Abs.
        Baseline = Math.Abs((double)glyphTypeface.Metrics.Ascent) / emHeight * fontSize;

        // Char width from the space glyph's advance (design em units → pixels).
        ushort spaceGlyph = 0;
        glyphTypeface.TryGetGlyph(' ', out spaceGlyph);
        _fallbackGlyph = spaceGlyph;
        var advance = glyphTypeface.GetGlyphAdvance(spaceGlyph);
        CharWidth = advance / emHeight * fontSize;

        HangingIndentChars = Math.Max(0, hangingIndentChars);
        HangingIndentPx = HangingIndentChars * CharWidth;

        // Pre-populate ASCII printable range so the inner draw/hit-test loop
        // hits a flat array indexed by char rather than a dictionary lookup.
        for (var i = 32; i < 128; i++) {
            _asciiGlyphs[i] = glyphTypeface.TryGetGlyph((uint)i, out var g) ? g : spaceGlyph;
        }
    }

    /// <summary>
    /// Tries to look up the glyph index for <paramref name="c"/>.  ASCII
    /// printable chars hit the cached table; everything else goes through
    /// <see cref="IGlyphTypeface.TryGetGlyph"/> with a per-context dictionary
    /// cache so we only ask once per session per char.
    /// </summary>
    public bool TryGetGlyph(char c, out ushort glyph) {
        if (c < 32) { glyph = 0; return false; }
        if (c < 128) { glyph = _asciiGlyphs[c]; return true; }
        if (_extraGlyphs.TryGetValue(c, out glyph)) return true;
        if (GlyphTypeface.TryGetGlyph(c, out glyph)) {
            _extraGlyphs[c] = glyph;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Determines whether the typeface resolved as a fixed-pitch (monospace)
    /// font.  Computed from <see cref="FontMetrics.IsFixedPitch"/>.
    /// </summary>
    public static bool IsMonospace(IGlyphTypeface glyphTypeface) =>
        glyphTypeface.Metrics.IsFixedPitch;
}
