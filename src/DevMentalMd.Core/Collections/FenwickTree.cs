using System;

namespace DevMentalMd.Core.Collections;

/// <summary>
/// A Fenwick tree (Binary Indexed Tree) over double values.
///
/// Supports O(log n) point update, O(log n) prefix sum query, and
/// O(log n) search for the index where a prefix sum target is reached.
///
/// Used by BlockIndex for both character-length and pixel-height prefix sums.
/// The tree is rebuilt from scratch on structural changes (block add/remove)
/// and updated incrementally on content edits (single block height change).
/// </summary>
public sealed class FenwickTree {
    private double[] _tree; // 1-indexed
    private int _n;

    /// <summary>Number of elements in the tree.</summary>
    public int Count => _n;

    /// <summary>Creates an empty tree with the given capacity.</summary>
    public FenwickTree(int size) {
        _n = size;
        _tree = new double[size + 1];
    }

    /// <summary>
    /// Builds a Fenwick tree from an array of values. O(n).
    /// </summary>
    public static FenwickTree FromValues(ReadOnlySpan<double> values) {
        var tree = new FenwickTree(values.Length);
        // Copy values into 1-indexed positions, then propagate in O(n)
        for (var i = 0; i < values.Length; i++) {
            tree._tree[i + 1] = values[i];
        }
        for (var i = 1; i <= tree._n; i++) {
            var parent = i + (i & -i);
            if (parent <= tree._n) {
                tree._tree[parent] += tree._tree[i];
            }
        }
        return tree;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to the value at index <paramref name="i"/> (0-based).
    /// O(log n).
    /// </summary>
    public void Update(int i, double delta) {
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, _n);
        i++; // convert to 1-based
        while (i <= _n) {
            _tree[i] += delta;
            i += i & -i;
        }
    }

    /// <summary>
    /// Returns the sum of elements [0, i] (0-based, inclusive). O(log n).
    /// </summary>
    public double PrefixSum(int i) {
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, _n);
        double sum = 0;
        i++; // convert to 1-based
        while (i > 0) {
            sum += _tree[i];
            i -= i & -i;
        }
        return sum;
    }

    /// <summary>
    /// Returns the total sum of all elements. O(log n).
    /// </summary>
    public double TotalSum() => _n > 0 ? PrefixSum(_n - 1) : 0;

    /// <summary>
    /// Returns the value at a specific index (0-based). O(log n).
    /// Computed as PrefixSum(i) - PrefixSum(i-1).
    /// </summary>
    public double ValueAt(int i) {
        return i == 0 ? PrefixSum(0) : PrefixSum(i) - PrefixSum(i - 1);
    }

    /// <summary>
    /// Finds the smallest 0-based index where the prefix sum exceeds or equals
    /// <paramref name="target"/>. Returns -1 if the total sum is less than the target.
    ///
    /// This is the O(log n) "walk down the tree" variant, not the O(log² n)
    /// binary-search-over-PrefixSum variant.
    ///
    /// Semantics: returns the index i such that PrefixSum(i) >= target and
    /// (i == 0 || PrefixSum(i-1) &lt; target). Useful for mapping a scroll
    /// position to a block index, or a character offset to a block index.
    /// </summary>
    public int FindByPrefixSum(double target) {
        if (_n == 0 || target <= 0) {
            return _n > 0 ? 0 : -1;
        }

        var pos = 0;
        var sum = 0.0;
        var bitMask = HighestOneBit(_n);

        while (bitMask > 0) {
            var next = pos + bitMask;
            if (next <= _n && sum + _tree[next] < target) {
                pos = next;
                sum += _tree[next];
            }
            bitMask >>= 1;
        }

        // pos is now the last 0-based index where prefix sum < target.
        // The answer is pos (which is 0-based because of the walk logic).
        // But if pos == _n, the total sum < target, so return -1.
        return pos < _n ? pos : -1;
    }

    /// <summary>
    /// Rebuilds the tree from new values. Replaces all content.
    /// O(n) where n is the new length.
    /// </summary>
    public void Rebuild(ReadOnlySpan<double> values) {
        _n = values.Length;
        if (_tree.Length < _n + 1) {
            _tree = new double[_n + 1];
        } else {
            Array.Clear(_tree, 0, _n + 1);
        }
        for (var i = 0; i < values.Length; i++) {
            _tree[i + 1] = values[i];
        }
        for (var i = 1; i <= _n; i++) {
            var parent = i + (i & -i);
            if (parent <= _n) {
                _tree[parent] += _tree[i];
            }
        }
    }

    private static int HighestOneBit(int n) {
        var bit = 1;
        while (bit <= n) {
            bit <<= 1;
        }
        return bit >> 1;
    }
}
