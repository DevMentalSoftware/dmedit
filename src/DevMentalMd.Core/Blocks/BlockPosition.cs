namespace DevMentalMd.Core.Blocks;

/// <summary>
/// A position within a <see cref="BlockDocument"/>, identifying both the
/// block and the character offset within that block's text.
/// </summary>
/// <param name="BlockIndex">0-based index of the block in the document.</param>
/// <param name="LocalOffset">Character offset within the block's text.</param>
public readonly record struct BlockPosition(int BlockIndex, int LocalOffset) {
    /// <summary>Creates a position at the start of the given block.</summary>
    public static BlockPosition StartOf(int blockIndex) => new(blockIndex, 0);

    /// <summary>Creates a position at the end of the given block's text.</summary>
    public static BlockPosition EndOf(int blockIndex, int textLength) => new(blockIndex, textLength);
}
