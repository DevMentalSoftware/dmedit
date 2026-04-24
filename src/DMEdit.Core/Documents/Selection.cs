namespace DMEdit.Core.Documents;

/// <summary>
/// Represents the editor selection as a pair of logical character offsets.
/// <see cref="Anchor"/> is the fixed end; <see cref="Active"/> is the moving end (where the caret is).
/// A collapsed selection (Anchor == Active) is just a caret position.
/// </summary>
public readonly record struct Selection(long Anchor, long Active) {
    public long Start => Math.Min(Anchor, Active);
    public long End => Math.Max(Anchor, Active);
    public long Len => End - Start;
    public bool IsEmpty => Anchor == Active;

    /// <summary>The caret position (the moving end of the selection).</summary>
    public long Caret => Active;

    /// <summary>Creates a collapsed selection (caret only) at <paramref name="ofs"/>.</summary>
    public static Selection Collapsed(long ofs) => new(ofs, ofs);

    /// <summary>Returns a new selection with the caret moved to <paramref name="ofs"/>, keeping the anchor.</summary>
    public Selection ExtendTo(long ofs) => this with { Active = ofs };

    /// <summary>Collapses the selection to the given end: Start or End of the current range.</summary>
    public Selection CollapseToStart() => Collapsed(Start);
    public Selection CollapseToEnd() => Collapsed(End);
}
