namespace DevMentalMd.Rendering.Layout;

/// <summary>
/// Owns the <see cref="BlockLayoutLine"/> objects produced by a
/// <see cref="BlockLayoutEngine"/> call. Disposing this releases the
/// underlying Avalonia <c>TextLayout</c> objects.
/// </summary>
public sealed class BlockLayoutResult : IDisposable {
    private bool _disposed;

    /// <summary>The laid-out blocks, in document order.</summary>
    public IReadOnlyList<BlockLayoutLine> Blocks { get; }

    /// <summary>Total height of all blocks stacked vertically.</summary>
    public double TotalHeight { get; }

    /// <summary>
    /// Index of the first block in the <see cref="DevMentalMd.Core.Blocks.BlockDocument"/>
    /// that this result covers. For full-document layout, this is 0.
    /// For windowed layout, this is the first visible block.
    /// </summary>
    public int FirstBlockIndex { get; }

    internal BlockLayoutResult(
        IReadOnlyList<BlockLayoutLine> blocks,
        double totalHeight,
        int firstBlockIndex = 0) {

        Blocks = blocks;
        TotalHeight = totalHeight;
        FirstBlockIndex = firstBlockIndex;
    }

    /// <summary>
    /// Finds the <see cref="BlockLayoutLine"/> at the given Y position.
    /// Returns null if no block covers that position.
    /// </summary>
    public BlockLayoutLine? FindBlockAtY(double y) {
        if (Blocks.Count == 0) {
            return null;
        }
        BlockLayoutLine? best = null;
        foreach (var block in Blocks) {
            if (block.Y <= y) {
                best = block;
            } else {
                break;
            }
        }
        return best;
    }

    /// <summary>
    /// Finds the <see cref="BlockLayoutLine"/> for the given block index.
    /// Returns null if the block is not in this layout result.
    /// </summary>
    public BlockLayoutLine? FindBlockByIndex(int blockIndex) {
        foreach (var block in Blocks) {
            if (block.BlockIndex == blockIndex) {
                return block;
            }
        }
        return null;
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        foreach (var block in Blocks) {
            block.Dispose();
        }
    }
}
