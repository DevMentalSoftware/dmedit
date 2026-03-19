using System;

namespace DevMentalMd.Core.Collections;

/// <summary>
/// A Fenwick tree (Binary Indexed Tree) storing int values with long prefix sums.
///
/// Individual elements are int (e.g. line lengths — a single line > 2 GB is
/// pathological).  Prefix sums and search targets are long because the total
/// document size can exceed int.MaxValue.
///
/// Used by PieceTable for O(log L) line-start and line-from-offset queries.
/// </summary>
public sealed class IntFenwickTree {
    private long[] _tree; // 1-indexed; partial sums stored as long
    private int _n;

    /// <summary>Number of elements in the tree.</summary>
    public int Count => _n;

    /// <summary>Creates an empty tree with the given capacity.</summary>
    public IntFenwickTree(int size) {
        _n = size;
        _tree = new long[size + 1];
    }

    /// <summary>Builds a Fenwick tree from int values. O(n).</summary>
    public static IntFenwickTree FromValues(ReadOnlySpan<int> values) {
        var tree = new IntFenwickTree(values.Length);
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
    public void Update(int i, int delta) {
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, _n);
        i++; // convert to 1-based
        while (i <= _n) {
            _tree[i] += delta;
            i += i & -i;
        }
    }

    /// <summary>
    /// Returns the prefix sum of elements [0, i] (0-based, inclusive). O(log n).
    /// Result is long because the sum of line lengths is a character offset.
    /// </summary>
    public long PrefixSum(int i) {
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, _n);
        var sum = 0L;
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
    public long TotalSum() => _n > 0 ? PrefixSum(_n - 1) : 0;

    /// <summary>
    /// Returns the individual value at index <paramref name="i"/> (0-based). O(log n).
    /// </summary>
    public int ValueAt(int i) {
        var sum = PrefixSum(i);
        return (int)(i == 0 ? sum : sum - PrefixSum(i - 1));
    }

    /// <summary>
    /// Extracts all individual values into a new array.  O(n).
    /// Reverses the build-phase propagation in-place, reads the raw values,
    /// then re-propagates to restore the tree.  Three linear passes, no
    /// per-element PrefixSum calls.
    /// </summary>
    public int[] ExtractValues() {
        // Undo propagation: walk backwards, subtract each node from its parent.
        for (var i = _n; i >= 1; i--) {
            var parent = i + (i & -i);
            if (parent <= _n) {
                _tree[parent] -= _tree[i];
            }
        }

        // Read raw values (tree[i+1] now holds the original value[i]).
        var values = new int[_n];
        for (var i = 0; i < _n; i++) {
            values[i] = (int)_tree[i + 1];
        }

        // Re-propagate to restore the tree to its queryable state.
        for (var i = 1; i <= _n; i++) {
            var parent = i + (i & -i);
            if (parent <= _n) {
                _tree[parent] += _tree[i];
            }
        }

        return values;
    }

    /// <summary>
    /// Finds the smallest 0-based index where the prefix sum reaches or exceeds
    /// <paramref name="target"/>.  Returns -1 if the total sum is less than the target.
    ///
    /// Target is long because it represents a character offset in the document.
    /// </summary>
    public int FindByPrefixSum(long target) {
        if (_n == 0 || target <= 0) {
            return _n > 0 ? 0 : -1;
        }

        var pos = 0;
        var sum = 0L;
        var bitMask = HighestOneBit(_n);

        while (bitMask > 0) {
            var next = pos + bitMask;
            if (next <= _n && sum + _tree[next] < target) {
                pos = next;
                sum += _tree[next];
            }
            bitMask >>= 1;
        }

        return pos < _n ? pos : -1;
    }

    /// <summary>Rebuilds the tree from int values. O(n).</summary>
    public void Rebuild(ReadOnlySpan<int> values) {
        _n = values.Length;
        if (_tree.Length < _n + 1) {
            _tree = new long[_n + 1];
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
