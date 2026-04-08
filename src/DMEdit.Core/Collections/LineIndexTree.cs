using System;
using System.Runtime.CompilerServices;

namespace DMEdit.Core.Collections;

/// <summary>
/// An implicit treap (randomized BST) storing int values with long prefix sums.
/// Supports O(log n) insert/remove of individual elements — unlike a Fenwick tree,
/// no O(n) rebuild is needed when the element count changes.
///
/// Used by PieceTable for the line-length index.  Each element stores one value:
/// the line length (including any terminator).  Provides O(log n) prefix-sum
/// queries for offset lookups and O(log n) incremental updates.
/// </summary>
public sealed class LineIndexTree {
    private const int Nil = -1;

    private int[] _val;   // element value (line length)
    private long[] _sum;  // subtree sum including self
    private int[] _max;   // subtree max including self
    private int[] _sz;    // subtree size including self
    private int[] _pri;   // random priority (max-heap)
    private int[] _left;
    private int[] _right;

    private int _root = Nil;
    private int _count;       // number of live elements
    private int _allocated;   // number of node slots in use (including free-listed)
    private int _freeHead = Nil;

    private LineIndexTree(int capacity) {
        capacity = Math.Max(capacity, 4);
        _val   = new int[capacity];
        _sum   = new long[capacity];
        _max   = new int[capacity];
        _sz    = new int[capacity];
        _pri   = new int[capacity];
        _left  = new int[capacity];
        _right = new int[capacity];
    }

    // -------------------------------------------------------------------
    //  Public API — queries
    // -------------------------------------------------------------------

    public int Count => _count;

    public long TotalSum() => _root == Nil ? 0 : _sum[_root];

    /// <summary>
    /// Returns the maximum value across all elements.  O(1).
    /// Assumes element values are non-negative (line lengths are ≥ 0 by
    /// domain): the absent-child sentinel in <c>_max</c> bookkeeping is 0,
    /// so a tree containing only negative values would incorrectly report
    /// 0 instead of its true (negative) maximum.  Returns 0 for an empty tree.
    /// </summary>
    public int MaxValue() => _root == Nil ? 0 : _max[_root];

    /// <summary>Returns sum of elements [0..i] inclusive (0-based).</summary>
    public long PrefixSum(int i) {
        if (i < 0) return 0;
        var node = _root;
        var result = 0L;
        var remaining = i + 1; // number of elements to include
        while (node != Nil) {
            var leftSize = LeftSize(node);
            if (remaining <= leftSize) {
                node = _left[node];
            } else {
                result += LeftSum(node) + _val[node];
                remaining -= leftSize + 1;
                if (remaining == 0) break;
                node = _right[node];
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the smallest 0-based index where the prefix sum reaches or exceeds
    /// <paramref name="target"/>.  Returns -1 if total sum &lt; target.
    /// </summary>
    public int FindByPrefixSum(long target) {
        if (_root == Nil || target > _sum[_root]) return -1;
        if (target <= 0) return 0;
        var node = _root;
        var index = 0;
        while (node != Nil) {
            var ls = LeftSum(node);
            if (target <= ls) {
                node = _left[node];
            } else if (target <= ls + _val[node]) {
                return index + LeftSize(node);
            } else {
                target -= ls + _val[node];
                index += LeftSize(node) + 1;
                node = _right[node];
            }
        }
        return index; // shouldn't reach here if target <= TotalSum
    }

    /// <summary>Returns the value of the element at 0-based index <paramref name="i"/>.</summary>
    public int ValueAt(int i) {
        var node = _root;
        var remaining = i;
        while (node != Nil) {
            var leftSize = LeftSize(node);
            if (remaining < leftSize) {
                node = _left[node];
            } else if (remaining == leftSize) {
                return _val[node];
            } else {
                remaining -= leftSize + 1;
                node = _right[node];
            }
        }
        throw new ArgumentOutOfRangeException(nameof(i));
    }

    // -------------------------------------------------------------------
    //  Public API — point update
    // -------------------------------------------------------------------

    /// <summary>Adds <paramref name="delta"/> to the value at index <paramref name="i"/>.</summary>
    public void Update(int i, int delta) {
        UpdateNode(_root, i, delta);
    }

    private void UpdateNode(int node, int i, int delta) {
        if (node == Nil) throw new ArgumentOutOfRangeException(nameof(i));
        _sum[node] += delta;
        var leftSize = LeftSize(node);
        if (i < leftSize) {
            UpdateNode(_left[node], i, delta);
        } else if (i == leftSize) {
            _val[node] += delta;
        } else {
            UpdateNode(_right[node], i - leftSize - 1, delta);
        }
        // Recompute max bottom-up: unlike sum, max is non-linear and can't be
        // updated with a simple += delta — each ancestor must recompute from children.
        _max[node] = Math.Max(_val[node], Math.Max(LeftMax(node), RightMax(node)));
    }

    // -------------------------------------------------------------------
    //  Public API — structural mutations
    // -------------------------------------------------------------------

    /// <summary>Inserts an element at 0-based index <paramref name="i"/>.</summary>
    public void InsertAt(int i, int value) {
        var newNode = AllocNode(value);
        Split(_root, i, out var left, out var right);
        _root = Merge(left, Merge(newNode, right));
    }

    /// <summary>Removes the element at 0-based index <paramref name="i"/>.</summary>
    public void RemoveAt(int i) {
        Split(_root, i, out var left, out var rest);
        Split(rest, 1, out var removed, out var right);
        if (removed != Nil) FreeNode(removed);
        _root = Merge(left, right);
    }

    /// <summary>Inserts multiple elements starting at 0-based index <paramref name="i"/>.</summary>
    public void InsertRange(int i, ReadOnlySpan<int> values) {
        if (values.IsEmpty) return;
        var subtree = BuildBalanced(values, 0, values.Length - 1);
        Split(_root, i, out var left, out var right);
        _root = Merge(left, Merge(subtree, right));
    }

    /// <summary>Removes <paramref name="count"/> elements starting at index <paramref name="i"/>.</summary>
    public void RemoveRange(int i, int count) {
        if (count <= 0) return;
        Split(_root, i, out var left, out var rest);
        Split(rest, count, out var middle, out var right);
        FreeSubtree(middle);
        _root = Merge(left, right);
    }

    // -------------------------------------------------------------------
    //  Public API — bulk construction
    // -------------------------------------------------------------------

    /// <summary>Creates a new tree from a value span.  O(n).</summary>
    public static LineIndexTree FromValues(ReadOnlySpan<int> values) {
        var tree = new LineIndexTree(values.Length);
        if (values.Length > 0) {
            tree._root = tree.BulkBuild(values);
        }
        return tree;
    }

    /// <summary>Replaces all content with new values.  O(n).</summary>
    public void Rebuild(ReadOnlySpan<int> values) {
        // Reset all state.
        _root = Nil;
        _count = 0;
        _allocated = 0;
        _freeHead = Nil;
        EnsureCapacity(values.Length);
        if (values.Length > 0) {
            _root = BulkBuild(values);
        }
    }

    /// <summary>Extracts all values via in-order traversal.  O(n).  For tests/debug.</summary>
    public int[] ExtractValues() {
        var result = new int[_count];
        var idx = 0;
        InOrder(_root, result, ref idx);
        return result;
    }

    // -------------------------------------------------------------------
    //  Split / Merge — core treap primitives
    // -------------------------------------------------------------------

    /// <summary>
    /// Splits the subtree rooted at <paramref name="node"/> into two treaps:
    /// <paramref name="left"/> with the first <paramref name="k"/> elements,
    /// and <paramref name="right"/> with the rest.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Split(int node, int k, out int left, out int right) {
        if (node == Nil) { left = Nil; right = Nil; return; }
        var leftSize = LeftSize(node);
        if (k <= leftSize) {
            Split(_left[node], k, out left, out var lr);
            _left[node] = lr;
            Push(node);
            right = node;
        } else {
            Split(_right[node], k - leftSize - 1, out var rl, out right);
            _right[node] = rl;
            Push(node);
            left = node;
        }
    }

    /// <summary>
    /// Merges two treaps.  All elements in <paramref name="left"/> have smaller
    /// implicit indices than all elements in <paramref name="right"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Merge(int left, int right) {
        if (left == Nil) return right;
        if (right == Nil) return left;
        if (_pri[left] >= _pri[right]) {
            _right[left] = Merge(_right[left], right);
            Push(left);
            return left;
        } else {
            _left[right] = Merge(left, _left[right]);
            Push(right);
            return right;
        }
    }

    // -------------------------------------------------------------------
    //  Node helpers
    // -------------------------------------------------------------------

    /// <summary>Recomputes Size, Sum, and Max from children.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(int node) {
        _sz[node] = 1 + LeftSize(node) + RightSize(node);
        _sum[node] = _val[node] + LeftSum(node) + RightSum(node);
        _max[node] = Math.Max(_val[node], Math.Max(LeftMax(node), RightMax(node)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LeftSize(int node) => _left[node] == Nil ? 0 : _sz[_left[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RightSize(int node) => _right[node] == Nil ? 0 : _sz[_right[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long LeftSum(int node) => _left[node] == Nil ? 0 : _sum[_left[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long RightSum(int node) => _right[node] == Nil ? 0 : _sum[_right[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LeftMax(int node) => _left[node] == Nil ? 0 : _max[_left[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RightMax(int node) => _right[node] == Nil ? 0 : _max[_right[node]];

    // -------------------------------------------------------------------
    //  Node allocation
    // -------------------------------------------------------------------

    private int AllocNode(int value) {
        int id;
        if (_freeHead != Nil) {
            id = _freeHead;
            _freeHead = _left[_freeHead]; // free list linked through _left
        } else {
            EnsureCapacity(_allocated + 1);
            id = _allocated++;
        }
        _val[id] = value;
        _sum[id] = value;
        _max[id] = value;
        _sz[id] = 1;
        _pri[id] = Random.Shared.Next();
        _left[id] = Nil;
        _right[id] = Nil;
        _count++;
        return id;
    }

    private void FreeNode(int id) {
        _left[id] = _freeHead;
        _freeHead = id;
        _count--;
    }

    private void FreeSubtree(int node) {
        if (node == Nil) return;
        // Iterative traversal using an explicit stack to avoid O(n) recursion
        // and improve cache locality by processing nodes in array order.
        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.Count > 0) {
            var n = stack.Pop();
            var l = _left[n];
            var r = _right[n];
            if (l != Nil) stack.Push(l);
            if (r != Nil) stack.Push(r);
            FreeNode(n);
        }
    }

    private void EnsureCapacity(int needed) {
        if (needed <= _val.Length) return;
        var newCap = Math.Max(needed, _val.Length * 2);
        Array.Resize(ref _val,   newCap);
        Array.Resize(ref _sum,   newCap);
        Array.Resize(ref _max,   newCap);
        Array.Resize(ref _sz,    newCap);
        Array.Resize(ref _pri,   newCap);
        Array.Resize(ref _left,  newCap);
        Array.Resize(ref _right, newCap);
    }

    // -------------------------------------------------------------------
    //  Bulk build — O(n) Cartesian tree construction
    // -------------------------------------------------------------------

    /// <summary>
    /// Builds a balanced treap from a value span in O(n) using a stack-based
    /// Cartesian tree algorithm.  Nodes are allocated sequentially.
    /// </summary>
    private int BulkBuild(ReadOnlySpan<int> values) {
        EnsureCapacity(values.Length);
        Span<int> stack = values.Length <= 1024
            ? stackalloc int[values.Length]
            : new int[values.Length];
        var top = -1;

        var root = Nil;
        for (var i = 0; i < values.Length; i++) {
            var id = _allocated++;
            _val[id] = values[i];
            _sum[id] = values[i];
            _max[id] = values[i];
            _sz[id] = 1;
            _pri[id] = Random.Shared.Next();
            _left[id] = Nil;
            _right[id] = Nil;
            _count++;

            var lastPopped = Nil;
            while (top >= 0 && _pri[stack[top]] < _pri[id]) {
                lastPopped = stack[top--];
                Push(lastPopped);
            }
            _left[id] = lastPopped;

            if (top >= 0) {
                _right[stack[top]] = id;
            } else {
                root = id;
            }
            stack[++top] = id;
        }
        // Finalize remaining nodes on the stack.
        while (top >= 0) {
            Push(stack[top--]);
        }
        return root;
    }

    /// <summary>
    /// Builds a balanced subtree from a value span for InsertRange.
    /// Uses the same Cartesian tree algorithm as BulkBuild.
    /// </summary>
    private int BuildBalanced(ReadOnlySpan<int> values, int lo, int hi) {
        if (lo > hi) return Nil;
        var span = values[lo..(hi + 1)];
        Span<int> stack = span.Length <= 256
            ? stackalloc int[span.Length]
            : new int[span.Length];
        var top = -1;
        var root = Nil;

        for (var i = 0; i < span.Length; i++) {
            var id = AllocNode(span[i]);
            var lastPopped = Nil;
            while (top >= 0 && _pri[stack[top]] < _pri[id]) {
                lastPopped = stack[top--];
                Push(lastPopped);
            }
            _left[id] = lastPopped;
            if (top >= 0) {
                _right[stack[top]] = id;
            } else {
                root = id;
            }
            stack[++top] = id;
        }
        while (top >= 0) {
            Push(stack[top--]);
        }
        return root;
    }

    // -------------------------------------------------------------------
    //  In-order traversal
    // -------------------------------------------------------------------

    private void InOrder(int node, int[] result, ref int idx) {
        if (node == Nil) return;
        InOrder(_left[node], result, ref idx);
        result[idx++] = _val[node];
        InOrder(_right[node], result, ref idx);
    }
}
