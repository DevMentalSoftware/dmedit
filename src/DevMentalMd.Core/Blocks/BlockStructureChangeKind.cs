namespace DevMentalMd.Core.Blocks;

/// <summary>
/// The kind of structural change that occurred in a <see cref="BlockDocument"/>.
/// </summary>
public enum BlockStructureChangeKind {
    /// <summary>A new block was inserted.</summary>
    Insert,

    /// <summary>A block was removed.</summary>
    Remove,

    /// <summary>A block was split into two.</summary>
    Split,

    /// <summary>Two adjacent blocks were merged into one.</summary>
    Merge,

    /// <summary>A block's type was changed (e.g., paragraph → heading).</summary>
    TypeChange,
}
