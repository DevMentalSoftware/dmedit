namespace DevMentalMd.Core.Documents;

/// <summary>
/// Represents the indentation style of a document.
/// </summary>
public enum IndentStyle {
    /// <summary>Indentation uses spaces.</summary>
    Spaces,

    /// <summary>Indentation uses tabs.</summary>
    Tabs,
}

/// <summary>
/// Result of detecting indentation style in a text buffer.
/// </summary>
public readonly record struct IndentInfo(IndentStyle Dominant, bool IsMixed,
    int SpaceCount = 0, int TabCount = 0) {
    /// <summary>Display label for the status bar.</summary>
    public string Label => Dominant switch {
        IndentStyle.Spaces => "Spaces",
        IndentStyle.Tabs => "Tabs",
        _ => "Spaces",
    };

    /// <summary>Default indentation style (spaces).</summary>
    public static IndentInfo Default => new(IndentStyle.Spaces, false);

    /// <summary>
    /// Builds an <see cref="IndentInfo"/> from explicit counts of lines
    /// whose leading whitespace starts with a space or a tab.
    /// Returns <see cref="Default"/> when both counts are zero.
    /// </summary>
    public static IndentInfo FromCounts(int spaces, int tabs) {
        if (spaces == 0 && tabs == 0) {
            return Default;
        }

        var dominant = tabs > spaces ? IndentStyle.Tabs : IndentStyle.Spaces;
        var mixed = spaces > 0 && tabs > 0;
        return new IndentInfo(dominant, mixed, spaces, tabs);
    }
}
