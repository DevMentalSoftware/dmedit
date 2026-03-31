using System;
using System.Collections.Generic;
using DMEdit.Core.Collections;

namespace DMEdit.Core.Blocks;

/// <summary>
/// A structured document represented as an ordered list of <see cref="Block"/>
/// objects. Provides O(log n) lookups from character offset or scroll position
/// to block index via parallel Fenwick trees.
///
/// This is the core editing model: all text editing is scoped to a single
/// block, and structural operations (insert, remove, split, merge, type change)
/// modify the block list. Markdown is an import/export format only; the user
/// never edits raw markdown.
/// </summary>
public sealed class BlockDocument {
    private readonly List<Block> _blocks = [];
    private FenwickTree _charTree;
    private FenwickTree _heightTree;

    /// <summary>
    /// Optional delegate that estimates a block's pixel height. If not set,
    /// a built-in rough estimate is used. The renderer / style system should
    /// replace this with a style-aware estimator.
    /// </summary>
    public Func<Block, double>? HeightEstimator { get; set; }

    // -----------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------

    /// <summary>Creates an empty document.</summary>
    public BlockDocument() {
        _charTree = new FenwickTree(0);
        _heightTree = new FenwickTree(0);
    }

    /// <summary>Creates a document with the given blocks.</summary>
    public BlockDocument(IEnumerable<Block> blocks) : this() {
        foreach (var block in blocks) {
            _blocks.Add(block);
            block.Changed += OnBlockChanged;
        }
        RebuildTrees();
    }

    /// <summary>
    /// Creates a simple single-paragraph document from a text string.
    /// Convenience for testing and initial document creation.
    /// </summary>
    public static BlockDocument FromText(string text) {
        var lines = text.Split('\n');
        var blocks = new List<Block>();
        foreach (var line in lines) {
            // Strip trailing \r for \r\n line endings
            var clean = line.EndsWith('\r') ? line[..^1] : line;
            blocks.Add(new Block(BlockType.Paragraph, clean));
        }
        if (blocks.Count == 0) {
            blocks.Add(new Block(BlockType.Paragraph));
        }
        return new BlockDocument(blocks);
    }

    // -----------------------------------------------------------------
    // Properties
    // -----------------------------------------------------------------

    /// <summary>Number of blocks in the document.</summary>
    public int BlockCount => _blocks.Count;

    /// <summary>Read-only view of the block list.</summary>
    public IReadOnlyList<Block> Blocks => _blocks;

    /// <summary>Access a block by index.</summary>
    public Block this[int index] => _blocks[index];

    /// <summary>Total character length across all blocks.</summary>
    public long TotalCharLength => _blocks.Count > 0 ? (long)_charTree.TotalSum() : 0;

    /// <summary>Total estimated document height in pixels.</summary>
    public double TotalHeight => _blocks.Count > 0 ? _heightTree.TotalSum() : 0;

    // -----------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------

    /// <summary>
    /// Fired when the block structure changes (block added, removed, split,
    /// merged, or type changed). The Fenwick trees have already been updated
    /// when this fires.
    /// </summary>
    public event EventHandler<BlockStructureChangedEventArgs>? StructureChanged;

    /// <summary>
    /// Fired when a block's content changes (text edited, spans modified).
    /// The Fenwick trees have already been updated when this fires.
    /// The sender is the <see cref="BlockDocument"/>.
    /// </summary>
    public event EventHandler? ContentChanged;

    // -----------------------------------------------------------------
    // Structural operations
    // -----------------------------------------------------------------

    /// <summary>
    /// Inserts a block at the given index. The new block is wired for
    /// change tracking and the Fenwick trees are rebuilt.
    /// </summary>
    public void InsertBlock(int index, Block block) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, _blocks.Count);

        _blocks.Insert(index, block);
        block.Changed += OnBlockChanged;
        RebuildTrees();
        StructureChanged?.Invoke(this, new BlockStructureChangedEventArgs(
            BlockStructureChangeKind.Insert, index));
    }

    /// <summary>
    /// Removes the block at the given index and returns it. The block is
    /// unwired from change tracking.
    /// </summary>
    public Block RemoveBlock(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _blocks.Count);

        var block = _blocks[index];
        _blocks.RemoveAt(index);
        block.Changed -= OnBlockChanged;
        RebuildTrees();
        StructureChanged?.Invoke(this, new BlockStructureChangedEventArgs(
            BlockStructureChangeKind.Remove, index));
        return block;
    }

    /// <summary>
    /// Splits the block at <paramref name="blockIndex"/> at the given local
    /// offset. Returns the new right block, which is inserted at blockIndex + 1.
    /// </summary>
    public Block SplitBlock(int blockIndex, int localOffset) {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockIndex, _blocks.Count);

        var block = _blocks[blockIndex];
        var right = block.SplitAt(localOffset);
        _blocks.Insert(blockIndex + 1, right);
        right.Changed += OnBlockChanged;
        RebuildTrees();
        StructureChanged?.Invoke(this, new BlockStructureChangedEventArgs(
            BlockStructureChangeKind.Split, blockIndex));
        return right;
    }

    /// <summary>
    /// Merges block at <paramref name="blockIndex"/> with the block immediately
    /// after it. The second block's text and spans are appended to the first.
    /// The second block is removed from the document.
    /// </summary>
    public void MergeBlocks(int blockIndex) {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        if (blockIndex >= _blocks.Count - 1) {
            throw new ArgumentOutOfRangeException(nameof(blockIndex),
                "Cannot merge the last block — there is no next block.");
        }

        var second = _blocks[blockIndex + 1];
        _blocks[blockIndex].MergeFrom(second);
        _blocks.RemoveAt(blockIndex + 1);
        second.Changed -= OnBlockChanged;
        RebuildTrees();
        StructureChanged?.Invoke(this, new BlockStructureChangedEventArgs(
            BlockStructureChangeKind.Merge, blockIndex));
    }

    /// <summary>
    /// Changes the type of the block at the given index. The height estimate
    /// may change (e.g., paragraph → heading), so the Fenwick trees are updated.
    /// </summary>
    public void ChangeBlockType(int blockIndex, BlockType newType) {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockIndex, _blocks.Count);

        _blocks[blockIndex].Type = newType;
        UpdateTreesForBlock(blockIndex);
        StructureChanged?.Invoke(this, new BlockStructureChangedEventArgs(
            BlockStructureChangeKind.TypeChange, blockIndex));
    }

    // -----------------------------------------------------------------
    // Lookup operations
    // -----------------------------------------------------------------

    /// <summary>
    /// Given a global character offset (0-based), returns the block index and
    /// local offset within that block. O(log n) via the char-length Fenwick tree.
    ///
    /// If the offset falls exactly on a block boundary, it maps to the start
    /// of the next block (like a caret positioned between blocks).
    /// </summary>
    public BlockPosition FindBlockByCharOffset(long charOffset) {
        if (_blocks.Count == 0) {
            return new BlockPosition(0, 0);
        }
        if (charOffset <= 0) {
            return new BlockPosition(0, 0);
        }

        var totalChars = (long)_charTree.TotalSum();
        if (charOffset >= totalChars) {
            var lastIdx = _blocks.Count - 1;
            return new BlockPosition(lastIdx, _blocks[lastIdx].Length);
        }

        // FindByPrefixSum returns the smallest index where prefixSum(index) >= target.
        // We want the block that contains the character at 'charOffset'. The prefix
        // sum at block i covers characters [0, prefixSum(i)). So if charOffset < prefixSum(i),
        // block i is the one. FindByPrefixSum(charOffset + 1) gives us that block because
        // we need prefixSum(index) >= charOffset + 1, meaning prefixSum(index) > charOffset.
        var blockIdx = _charTree.FindByPrefixSum(charOffset + 1);
        if (blockIdx < 0) {
            blockIdx = _blocks.Count - 1;
        }

        // Local offset = charOffset - sum of all preceding blocks
        var prefixBefore = blockIdx > 0 ? (long)_charTree.PrefixSum(blockIdx - 1) : 0;
        var localOffset = (int)(charOffset - prefixBefore);

        return new BlockPosition(blockIdx, localOffset);
    }

    /// <summary>
    /// Given a scroll Y position (pixels from document top), returns the
    /// block index at that position. O(log n) via the height Fenwick tree.
    /// </summary>
    public int FindBlockByScrollPosition(double scrollY) {
        if (_blocks.Count == 0) {
            return 0;
        }
        if (scrollY <= 0) {
            return 0;
        }

        var idx = _heightTree.FindByPrefixSum(scrollY);
        return idx >= 0 ? idx : _blocks.Count - 1;
    }

    /// <summary>
    /// Returns the Y coordinate (pixels from document top) of the top edge
    /// of the given block. O(log n).
    /// </summary>
    public double BlockTopY(int blockIndex) {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockIndex, _blocks.Count);

        return blockIndex == 0 ? 0 : _heightTree.PrefixSum(blockIndex - 1);
    }

    /// <summary>
    /// Returns the global character offset where the given block's text
    /// begins. O(log n).
    /// </summary>
    public long BlockCharStart(int blockIndex) {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockIndex, _blocks.Count);

        return blockIndex == 0 ? 0 : (long)_charTree.PrefixSum(blockIndex - 1);
    }

    /// <summary>
    /// Updates the estimated height for a specific block. Called by the
    /// renderer when the actual layout height is computed and differs from
    /// the estimate. O(log n).
    /// </summary>
    public void UpdateBlockHeight(int blockIndex, double newHeight) {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockIndex, _blocks.Count);

        var currentHeight = _heightTree.ValueAt(blockIndex);
        var delta = newHeight - currentHeight;
        if (Math.Abs(delta) > 0.001) {
            _heightTree.Update(blockIndex, delta);
        }
    }

    /// <summary>
    /// Returns the current estimated height of the given block.
    /// </summary>
    public double GetBlockHeight(int blockIndex) {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockIndex, _blocks.Count);

        return _heightTree.ValueAt(blockIndex);
    }

    // -----------------------------------------------------------------
    // Internal helpers
    // -----------------------------------------------------------------

    private void OnBlockChanged(object? sender, EventArgs e) {
        if (sender is Block block) {
            var idx = _blocks.IndexOf(block);
            if (idx >= 0) {
                UpdateTreesForBlock(idx);
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Incrementally updates both Fenwick trees for a single block that
    /// changed content or type. O(log n) per tree.
    /// </summary>
    private void UpdateTreesForBlock(int index) {
        var block = _blocks[index];

        // Update char-length tree
        var currentCharLen = _charTree.ValueAt(index);
        var newCharLen = (double)block.Length;
        var charDelta = newCharLen - currentCharLen;
        if (Math.Abs(charDelta) > 0.001) {
            _charTree.Update(index, charDelta);
        }

        // Update height tree with new estimate
        var currentHeight = _heightTree.ValueAt(index);
        var newHeight = EstimateHeight(block);
        var heightDelta = newHeight - currentHeight;
        if (Math.Abs(heightDelta) > 0.001) {
            _heightTree.Update(index, heightDelta);
        }
    }

    /// <summary>
    /// Rebuilds both Fenwick trees from scratch. Called after structural
    /// changes (block add/remove/split/merge). O(n).
    /// </summary>
    private void RebuildTrees() {
        var charLengths = new double[_blocks.Count];
        var heights = new double[_blocks.Count];
        for (var i = 0; i < _blocks.Count; i++) {
            charLengths[i] = _blocks[i].Length;
            heights[i] = EstimateHeight(_blocks[i]);
        }
        _charTree.Rebuild(charLengths);
        _heightTree.Rebuild(heights);
    }

    /// <summary>
    /// Estimates a block's pixel height. Uses the injected
    /// <see cref="HeightEstimator"/> if set; otherwise falls back to a
    /// rough built-in estimate based on block type only.
    /// </summary>
    private double EstimateHeight(Block block) {
        if (HeightEstimator is not null) {
            return HeightEstimator(block);
        }
        return DefaultEstimateHeight(block);
    }

    /// <summary>
    /// Rough height estimate based only on block type. Does not account for
    /// content length, wrap width, or font metrics. Replaced by the style
    /// system in a later milestone.
    /// </summary>
    private static double DefaultEstimateHeight(Block block) {
        return block.Type switch {
            BlockType.Heading1 => 48.0,
            BlockType.Heading2 => 40.0,
            BlockType.Heading3 => 34.0,
            BlockType.Heading4 => 28.0,
            BlockType.Heading5 => 24.0,
            BlockType.Heading6 => 22.0,
            BlockType.HorizontalRule => 16.0,
            _ => 24.0,
        };
    }
}
