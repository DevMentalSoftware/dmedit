using System;
using System.Runtime.CompilerServices;

namespace DevMentalMd.Core.Collections;

/// <summary>
/// An implicit treap (randomized BST) storing int values with long prefix sums.
/// Supports O(log n) insert/remove of individual elements — unlike a Fenwick tree,
/// no O(n) rebuild is needed when the element count changes.
///
/// Used by PieceTable for the line-length index.  Each element stores two values:
/// a primary (buf-space) length and a secondary (doc-space) length.  For lines
/// without pseudo-terminators these are equal.  Pseudo-lines have doc = buf + 1
/// (the virtual terminator).  Both have independent prefix sums for O(log n)
/// offset lookups in either space.
///
/// Primary queries (<see cref="PrefixSum"/>, <see cref="FindByPrefixSum"/>,
/// <see cref="ValueAt"/>) operate in buf-space — all existing code continues
/// to work unchanged.  Secondary queries (<see cref="DocPrefixSum"/>,
/// <see cref="DocValueAt"/>) operate in doc-space for navigation.
/// </summary>
public sealed class LineIndexTree {
    private const int Nil = -1;

    private int[] _val;   // primary element value (buf-space line length)
    private long[] _sum;  // primary subtree sum including self
    private int[] _max;   // primary subtree max including self
    private int[] _val2;  // secondary element value (doc-space line length)
    private long[] _sum2; // secondary subtree sum including self
    private int[] _max2;  // secondary subtree max including self
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
        _val2  = new int[capacity];
        _sum2  = new long[capacity];
        _max2  = new int[capacity];
        _sz    = new int[capacity];
        _pri   = new int[capacity];
        _left  = new int[capacity];
        _right = new int[capacity];
    }

    // -------------------------------------------------------------------
    //  Public API — queries (primary / buf-space)
    // -------------------------------------------------------------------

    public int Count => _count;

    public long TotalSum() => _root == Nil ? 0 : _sum[_root];

    /// <summary>Returns the maximum primary (buf-space) value across all elements.  O(1).</summary>
    public int MaxValue() => _root == Nil ? 0 : _max[_root];

    /// <summary>Returns the maximum secondary (doc-space) value across all elements.  O(1).</summary>
    public int MaxDocValue() => _root == Nil ? 0 : _max2[_root];

    /// <summary>Returns sum of primary (buf) elements [0..i] inclusive (0-based).</summary>
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
    /// Finds the smallest 0-based index where the primary (buf) prefix sum reaches or exceeds
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

    /// <summary>Returns the primary (buf) value of the element at 0-based index <paramref name="i"/>.</summary>
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
    //  Public API — queries (secondary / doc-space)
    // -------------------------------------------------------------------

    public long DocTotalSum() => _root == Nil ? 0 : _sum2[_root];

    /// <summary>Returns sum of secondary (doc) elements [0..i] inclusive (0-based).</summary>
    public long DocPrefixSum(int i) {
        if (i < 0) return 0;
        var node = _root;
        var result = 0L;
        var remaining = i + 1;
        while (node != Nil) {
            var leftSize = LeftSize(node);
            if (remaining <= leftSize) {
                node = _left[node];
            } else {
                result += LeftSum2(node) + _val2[node];
                remaining -= leftSize + 1;
                if (remaining == 0) break;
                node = _right[node];
            }
        }
        return result;
    }

    /// <summary>
    /// Finds the smallest 0-based index where the secondary (doc) prefix sum
    /// reaches or exceeds <paramref name="target"/>.
    /// Returns -1 if total doc sum &lt; target.
    /// </summary>
    public int FindByDocPrefixSum(long target) {
        if (_root == Nil || target > _sum2[_root]) return -1;
        if (target <= 0) return 0;
        var node = _root;
        var index = 0;
        while (node != Nil) {
            var ls = LeftSum2(node);
            if (target <= ls) {
                node = _left[node];
            } else if (target <= ls + _val2[node]) {
                return index + LeftSize(node);
            } else {
                target -= ls + _val2[node];
                index += LeftSize(node) + 1;
                node = _right[node];
            }
        }
        return index;
    }

    /// <summary>Returns the secondary (doc) value of the element at 0-based index <paramref name="i"/>.</summary>
    public int DocValueAt(int i) {
        var node = _root;
        var remaining = i;
        while (node != Nil) {
            var leftSize = LeftSize(node);
            if (remaining < leftSize) {
                node = _left[node];
            } else if (remaining == leftSize) {
                return _val2[node];
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

    /// <summary>Adds <paramref name="delta"/> to both primary and secondary values at index <paramref name="i"/>.</summary>
    public void Update(int i, int delta) {
        UpdateNode(_root, i, delta);
    }

    private void UpdateNode(int node, int i, int delta) {
        if (node == Nil) throw new ArgumentOutOfRangeException(nameof(i));
        _sum[node] += delta;
        _sum2[node] += delta;
        var leftSize = LeftSize(node);
        if (i < leftSize) {
            UpdateNode(_left[node], i, delta);
        } else if (i == leftSize) {
            _val[node] += delta;
            _val2[node] += delta;
        } else {
            UpdateNode(_right[node], i - leftSize - 1, delta);
        }
        // Recompute max bottom-up: unlike sum, max is non-linear and can't be
        // updated with a simple += delta — each ancestor must recompute from children.
        _max[node] = Math.Max(_val[node], Math.Max(LeftMax(node), RightMax(node)));
        _max2[node] = Math.Max(_val2[node], Math.Max(LeftMax2(node), RightMax2(node)));
    }

    // -------------------------------------------------------------------
    //  Public API — structural mutations
    // -------------------------------------------------------------------

    /// <summary>Inserts a dual-valued element at 0-based index <paramref name="i"/>.</summary>
    public void InsertAt(int i, int bufValue, int docValue) {
        var newNode = AllocNode(bufValue, docValue);
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

    /// <summary>Inserts multiple dual-valued elements starting at 0-based index <paramref name="i"/>.</summary>
    public void InsertRange(int i, ReadOnlySpan<int> bufValues, ReadOnlySpan<int> docValues) {
        if (bufValues.IsEmpty) return;
        var subtree = BuildBalanced(bufValues, docValues, 0, bufValues.Length - 1);
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

    /// <summary>Creates a new tree from dual value spans.  O(n).</summary>
    public static LineIndexTree FromValues(ReadOnlySpan<int> bufValues, ReadOnlySpan<int> docValues) {
        var tree = new LineIndexTree(bufValues.Length);
        if (bufValues.Length > 0) {
            tree._root = tree.BulkBuild(bufValues, docValues);
        }
        return tree;
    }

    /// <summary>Replaces all content with new dual values.  O(n).</summary>
    public void Rebuild(ReadOnlySpan<int> bufValues, ReadOnlySpan<int> docValues) {
        // Reset all state.
        _root = Nil;
        _count = 0;
        _allocated = 0;
        _freeHead = Nil;
        EnsureCapacity(bufValues.Length);
        if (bufValues.Length > 0) {
            _root = BulkBuild(bufValues, docValues);
        }
    }

    /// <summary>Extracts all primary (buf) values via in-order traversal.  O(n).  For tests/debug.</summary>
    public int[] ExtractValues() {
        var result = new int[_count];
        var idx = 0;
        InOrder(_root, result, ref idx, primary: true);
        return result;
    }

    /// <summary>Extracts all secondary (doc) values via in-order traversal.  O(n).  For tests/debug.</summary>
    public int[] ExtractDocValues() {
        var result = new int[_count];
        var idx = 0;
        InOrder(_root, result, ref idx, primary: false);
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

    /// <summary>Recomputes Size, both Sums, and both Maxes from children.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(int node) {
        _sz[node] = 1 + LeftSize(node) + RightSize(node);
        _sum[node] = _val[node] + LeftSum(node) + RightSum(node);
        _sum2[node] = _val2[node] + LeftSum2(node) + RightSum2(node);
        _max[node] = Math.Max(_val[node], Math.Max(LeftMax(node), RightMax(node)));
        _max2[node] = Math.Max(_val2[node], Math.Max(LeftMax2(node), RightMax2(node)));
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
    private long LeftSum2(int node) => _left[node] == Nil ? 0 : _sum2[_left[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long RightSum2(int node) => _right[node] == Nil ? 0 : _sum2[_right[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LeftMax(int node) => _left[node] == Nil ? 0 : _max[_left[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RightMax(int node) => _right[node] == Nil ? 0 : _max[_right[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LeftMax2(int node) => _left[node] == Nil ? 0 : _max2[_left[node]];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int RightMax2(int node) => _right[node] == Nil ? 0 : _max2[_right[node]];

    // -------------------------------------------------------------------
    //  Node allocation
    // -------------------------------------------------------------------

    private int AllocNode(int bufValue, int docValue) {
        int id;
        if (_freeHead != Nil) {
            id = _freeHead;
            _freeHead = _left[_freeHead]; // free list linked through _left
        } else {
            EnsureCapacity(_allocated + 1);
            id = _allocated++;
        }
        _val[id] = bufValue;
        _sum[id] = bufValue;
        _max[id] = bufValue;
        _val2[id] = docValue;
        _sum2[id] = docValue;
        _max2[id] = docValue;
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
        Array.Resize(ref _val2,  newCap);
        Array.Resize(ref _sum2,  newCap);
        Array.Resize(ref _max2,  newCap);
        Array.Resize(ref _sz,    newCap);
        Array.Resize(ref _pri,   newCap);
        Array.Resize(ref _left,  newCap);
        Array.Resize(ref _right, newCap);
    }

    // -------------------------------------------------------------------
    //  Bulk build — O(n) Cartesian tree construction
    // -------------------------------------------------------------------

    /// <summary>
    /// Builds a balanced treap from dual value spans in O(n) using a stack-based
    /// Cartesian tree algorithm.  Nodes are allocated sequentially.
    /// </summary>
    private int BulkBuild(ReadOnlySpan<int> bufValues, ReadOnlySpan<int> docValues) {
        EnsureCapacity(bufValues.Length);
        Span<int> stack = bufValues.Length <= 1024
            ? stackalloc int[bufValues.Length]
            : new int[bufValues.Length];
        var top = -1;

        var root = Nil;
        for (var i = 0; i < bufValues.Length; i++) {
            var id = _allocated++;
            _val[id] = bufValues[i];
            _sum[id] = bufValues[i];
            _max[id] = bufValues[i];
            _val2[id] = docValues[i];
            _sum2[id] = docValues[i];
            _max2[id] = docValues[i];
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
    /// Builds a balanced subtree from dual value spans for InsertRange.
    /// Uses the same Cartesian tree algorithm as BulkBuild.
    /// </summary>
    private int BuildBalanced(ReadOnlySpan<int> bufValues, ReadOnlySpan<int> docValues,
                              int lo, int hi) {
        if (lo > hi) return Nil;
        var bufSpan = bufValues[lo..(hi + 1)];
        var docSpan = docValues[lo..(hi + 1)];
        Span<int> stack = bufSpan.Length <= 256
            ? stackalloc int[bufSpan.Length]
            : new int[bufSpan.Length];
        var top = -1;
        var root = Nil;

        for (var i = 0; i < bufSpan.Length; i++) {
            var id = AllocNode(bufSpan[i], docSpan[i]);
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

    private void InOrder(int node, int[] result, ref int idx, bool primary) {
        if (node == Nil) return;
        InOrder(_left[node], result, ref idx, primary);
        result[idx++] = primary ? _val[node] : _val2[node];
        InOrder(_right[node], result, ref idx, primary);
    }
}
