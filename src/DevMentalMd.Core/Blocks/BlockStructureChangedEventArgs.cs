using System;

namespace DevMentalMd.Core.Blocks;

/// <summary>
/// Event arguments for <see cref="BlockDocument.StructureChanged"/>.
/// Describes which block was affected and how.
/// </summary>
public sealed class BlockStructureChangedEventArgs : EventArgs {
    /// <summary>The kind of structural change.</summary>
    public BlockStructureChangeKind Kind { get; }

    /// <summary>
    /// The index of the affected block. For <see cref="BlockStructureChangeKind.Split"/>,
    /// this is the original block that was split (the new block is at BlockIndex + 1).
    /// For <see cref="BlockStructureChangeKind.Merge"/>, this is the surviving block
    /// (the one that absorbed the next block).
    /// </summary>
    public int BlockIndex { get; }

    public BlockStructureChangedEventArgs(BlockStructureChangeKind kind, int blockIndex) {
        Kind = kind;
        BlockIndex = blockIndex;
    }
}
