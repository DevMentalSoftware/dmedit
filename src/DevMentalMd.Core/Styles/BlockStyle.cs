using System;
using System.Text.Json.Serialization;

namespace DevMentalMd.Core.Styles;

/// <summary>
/// Visual styling properties for a block type. Controls how blocks of that
/// type are rendered: font, spacing, colors, and height-cap behavior.
///
/// The rendering layer reads these properties for layout and drawing.
/// The height estimator uses them for O(1) per-block height estimation
/// without invoking the full text layout engine.
/// </summary>
public sealed class BlockStyle {
    // -----------------------------------------------------------------
    // Font
    // -----------------------------------------------------------------

    /// <summary>Font family name. Falls back to platform default if not found.</summary>
    public string FontFamily { get; set; } = "Segoe UI";

    /// <summary>Font size in device-independent pixels.</summary>
    public double FontSize { get; set; } = 14.0;

    /// <summary>
    /// Font weight. 400 = normal, 700 = bold.
    /// Uses numeric values for JSON compatibility and flexibility.
    /// </summary>
    public int FontWeight { get; set; } = 400;

    /// <summary>
    /// If true, the font is monospace (all characters same width).
    /// This makes height estimation exact rather than approximate.
    /// </summary>
    public bool IsMonospace { get; set; }

    // -----------------------------------------------------------------
    // Spacing
    // -----------------------------------------------------------------

    /// <summary>Line height as a multiplier on <see cref="FontSize"/>.</summary>
    public double LineHeight { get; set; } = 1.4;

    /// <summary>Vertical margin above the block, in pixels.</summary>
    public double MarginTop { get; set; }

    /// <summary>Vertical margin below the block, in pixels.</summary>
    public double MarginBottom { get; set; } = 8;

    /// <summary>Padding inside the block's visual area (top).</summary>
    public double PaddingTop { get; set; }

    /// <summary>Padding inside the block's visual area (bottom).</summary>
    public double PaddingBottom { get; set; }

    /// <summary>Padding inside the block's visual area (left).</summary>
    public double PaddingLeft { get; set; }

    /// <summary>Padding inside the block's visual area (right).</summary>
    public double PaddingRight { get; set; }

    // -----------------------------------------------------------------
    // Colors (nullable → inherit from document default)
    // -----------------------------------------------------------------

    /// <summary>Text color as a CSS-style hex string (e.g., "#333333"). Null = inherit.</summary>
    public string? ForegroundColor { get; set; }

    /// <summary>Block background color as a CSS-style hex string. Null = transparent.</summary>
    public string? BackgroundColor { get; set; }

    // -----------------------------------------------------------------
    // Capped blocks
    // -----------------------------------------------------------------

    /// <summary>
    /// If set, the block's visible height is capped at this many visual
    /// lines. Blocks exceeding this get an inner scrollbar. Null = no cap.
    /// Primarily used for code blocks and tables.
    /// </summary>
    public int? MaxVisibleLines { get; set; }

    // -----------------------------------------------------------------
    // Cached font metrics (set by renderer, not serialized)
    // -----------------------------------------------------------------

    /// <summary>
    /// Average character width in pixels for this font/size combo. Set by
    /// the rendering layer after measuring actual font metrics. The height
    /// estimator uses this for its O(1) calculation.
    ///
    /// Defaults to a rough approximation of FontSize × 0.5.
    /// </summary>
    [JsonIgnore]
    public double AvgCharWidth { get; set; }

    // -----------------------------------------------------------------
    // Computed properties
    // -----------------------------------------------------------------

    /// <summary>Single visual line height in pixels (FontSize × LineHeight).</summary>
    [JsonIgnore]
    public double ComputedLineHeight => FontSize * LineHeight;

    /// <summary>Total vertical margin (top + bottom).</summary>
    [JsonIgnore]
    public double TotalVerticalMargin => MarginTop + MarginBottom;

    /// <summary>Total vertical padding (top + bottom).</summary>
    [JsonIgnore]
    public double TotalVerticalPadding => PaddingTop + PaddingBottom;

    // -----------------------------------------------------------------
    // Height estimation
    // -----------------------------------------------------------------

    /// <summary>
    /// Estimates the pixel height of a block with the given character count,
    /// given the wrap width. Uses <see cref="AvgCharWidth"/> for proportional
    /// fonts (approximate) and exact math for monospace.
    ///
    /// Includes margins and padding. If <see cref="MaxVisibleLines"/> is set,
    /// the visual line count is capped.
    /// </summary>
    public double EstimateHeight(int charCount, double wrapWidth) {
        var chrome = TotalVerticalMargin + TotalVerticalPadding;

        if (charCount == 0) {
            // Empty block: one visual line plus chrome
            return ComputedLineHeight + chrome;
        }

        // Effective wrap width after padding
        var effectiveWidth = wrapWidth - PaddingLeft - PaddingRight;
        if (effectiveWidth <= 0) {
            effectiveWidth = 1;
        }

        var charWidth = AvgCharWidth > 0 ? AvgCharWidth : FontSize * 0.5;
        var estVisualLines = Math.Max(1, (int)Math.Ceiling(charCount * charWidth / effectiveWidth));

        if (MaxVisibleLines.HasValue && estVisualLines > MaxVisibleLines.Value) {
            estVisualLines = MaxVisibleLines.Value;
        }

        return estVisualLines * ComputedLineHeight + chrome;
    }
}
