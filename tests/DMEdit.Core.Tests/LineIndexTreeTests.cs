using DMEdit.Core.Collections;

namespace DMEdit.Core.Tests;

public class LineIndexTreeTests {
    // ---------------------------------------------------------------
    //  Group A — basic prefix-sum / value access API
    // ---------------------------------------------------------------

    [Fact]
    public void FromValues_PrefixSumsAreCorrect() {
        int[] v = [10, 20, 30, 40, 50];
        var tree = LineIndexTree.FromValues(v);
        Assert.Equal(10, tree.PrefixSum(0));
        Assert.Equal(30, tree.PrefixSum(1));
        Assert.Equal(60, tree.PrefixSum(2));
        Assert.Equal(100, tree.PrefixSum(3));
        Assert.Equal(150, tree.PrefixSum(4));
    }

    [Fact]
    public void TotalSum_ReturnsSum() {
        int[] v = [5, 10, 15];
        var tree = LineIndexTree.FromValues(v);
        Assert.Equal(30, tree.TotalSum());
    }

    [Fact]
    public void TotalSum_EmptyTree() {
        int[] v = [];
        var tree = LineIndexTree.FromValues(v);
        Assert.Equal(0, tree.TotalSum());
    }

    [Fact]
    public void ValueAt_ReturnsIndividualValues() {
        int[] v = [10, 20, 30, 40];
        var tree = LineIndexTree.FromValues(v);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(20, tree.ValueAt(1));
        Assert.Equal(30, tree.ValueAt(2));
        Assert.Equal(40, tree.ValueAt(3));
    }

    [Fact]
    public void Update_AdjustsValueAndPrefixSums() {
        int[] v = [10, 20, 30];
        var tree = LineIndexTree.FromValues(v);
        tree.Update(1, 5); // 20 → 25
        Assert.Equal(10, tree.PrefixSum(0));
        Assert.Equal(35, tree.PrefixSum(1));
        Assert.Equal(65, tree.PrefixSum(2));
        Assert.Equal(25, tree.ValueAt(1));
    }

    [Fact]
    public void Update_NegativeDelta() {
        int[] v = [100, 200, 300];
        var tree = LineIndexTree.FromValues(v);
        tree.Update(2, -50); // 300 → 250
        Assert.Equal(550, tree.TotalSum());
        Assert.Equal(250, tree.ValueAt(2));
    }

    [Fact]
    public void FindByPrefixSum_FindsCorrectIndex() {
        int[] v = [10, 20, 30, 40];
        var tree = LineIndexTree.FromValues(v);
        Assert.Equal(0, tree.FindByPrefixSum(5));
        Assert.Equal(0, tree.FindByPrefixSum(10));
        Assert.Equal(1, tree.FindByPrefixSum(11));
        Assert.Equal(1, tree.FindByPrefixSum(30));
        Assert.Equal(2, tree.FindByPrefixSum(31));
        Assert.Equal(2, tree.FindByPrefixSum(60));
        Assert.Equal(3, tree.FindByPrefixSum(61));
        Assert.Equal(3, tree.FindByPrefixSum(100));
    }

    [Fact]
    public void FindByPrefixSum_TargetExceedsTotal_ReturnsNegativeOne() {
        int[] v = [10, 20, 30];
        var tree = LineIndexTree.FromValues(v);
        Assert.Equal(-1, tree.FindByPrefixSum(61));
        Assert.Equal(-1, tree.FindByPrefixSum(1000));
    }

    [Fact]
    public void FindByPrefixSum_TargetZeroOrNegative_ReturnsZero() {
        int[] v = [10, 20, 30];
        var tree = LineIndexTree.FromValues(v);
        Assert.Equal(0, tree.FindByPrefixSum(0));
        Assert.Equal(0, tree.FindByPrefixSum(-5));
    }

    [Fact]
    public void FindByPrefixSum_SingleElement() {
        int[] v = [42];
        var tree = LineIndexTree.FromValues(v);
        Assert.Equal(0, tree.FindByPrefixSum(1));
        Assert.Equal(0, tree.FindByPrefixSum(42));
        Assert.Equal(-1, tree.FindByPrefixSum(43));
    }

    [Fact]
    public void FindByPrefixSum_EmptyTree() {
        int[] v = [];
        var tree = LineIndexTree.FromValues(v);
        Assert.Equal(-1, tree.FindByPrefixSum(1));
    }

    [Fact]
    public void Rebuild_ReplacesAllContent() {
        int[] v1 = [1, 2, 3];
        var tree = LineIndexTree.FromValues(v1);
        Assert.Equal(6, tree.TotalSum());
        int[] v2 = [10, 20, 30, 40];
        tree.Rebuild(v2);
        Assert.Equal(4, tree.Count);
        Assert.Equal(100, tree.TotalSum());
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(40, tree.ValueAt(3));
    }

    [Fact]
    public void Rebuild_ToSmallerSize() {
        int[] v1 = [10, 20, 30, 40, 50];
        var tree = LineIndexTree.FromValues(v1);
        int[] v2 = [100, 200];
        tree.Rebuild(v2);
        Assert.Equal(2, tree.Count);
        Assert.Equal(300, tree.TotalSum());
    }

    [Fact]
    public void LargeTree_CorrectBehavior() {
        const int n = 10_000;
        var values = new int[n];
        for (var i = 0; i < n; i++) values[i] = i + 1;
        var tree = LineIndexTree.FromValues(values);
        Assert.Equal(50_005_000, tree.TotalSum());
        Assert.Equal(5050, tree.PrefixSum(99));
        Assert.Equal(99, tree.FindByPrefixSum(5050));
        Assert.Equal(100, tree.FindByPrefixSum(5051));
    }

    // ---------------------------------------------------------------
    //  Group A.MaxValue — direct coverage of the subtree-max path
    //
    //  Load-bearing because PieceTable.MaxLineLength === MaxValue() and
    //  the CharWrap trigger reads it. Drift here is silent (no internal
    //  invariant assertion checks _max), so these tests are the safety
    //  net for the drop-_maxLineLen-cache refactor in PieceTable.
    // ---------------------------------------------------------------

    [Fact]
    public void MaxValue_EmptyTree_ReturnsZero() {
        var tree = LineIndexTree.FromValues([]);
        Assert.Equal(0, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_SingleElement_ReturnsThatValue() {
        var tree = LineIndexTree.FromValues([42]);
        Assert.Equal(42, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_FromValues_ReturnsLargest() {
        var tree = LineIndexTree.FromValues([10, 20, 30, 5, 25]);
        Assert.Equal(30, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_FromValues_MaxAtFirstPosition() {
        // Position-sensitive: ensures _max bubbles up regardless of where
        // the max sits in the random treap structure.
        var tree = LineIndexTree.FromValues([100, 1, 2, 3, 4]);
        Assert.Equal(100, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_FromValues_MaxAtLastPosition() {
        var tree = LineIndexTree.FromValues([1, 2, 3, 4, 100]);
        Assert.Equal(100, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterUpdate_GrowingMax() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.Update(0, 100); // 10 → 110, now the new max
        Assert.Equal(110, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterUpdate_ShrinkingTheCurrentMax() {
        // The element that was the max gets reduced below another element.
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.Update(2, -25); // 30 → 5; new max should be 20
        Assert.Equal(20, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterUpdate_NonMaxElement_DoesNotChangeMax() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.Update(0, 5); // 10 → 15; max still 30
        Assert.Equal(30, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterInsertAt_NewLargest() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.InsertAt(1, 99);
        Assert.Equal(99, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterInsertAt_NotLargest() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.InsertAt(1, 5);
        Assert.Equal(30, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterRemoveAt_RemovingTheUniqueMax() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.RemoveAt(2); // remove 30; new max should be 20
        Assert.Equal(20, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterRemoveAt_RemovingNonMax_KeepsMax() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.RemoveAt(0); // remove 10; max still 30
        Assert.Equal(30, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterRemoveAt_DuplicateMax_KeepsMax() {
        // Two elements share the max value; removing one must not drop it.
        var tree = LineIndexTree.FromValues([10, 30, 20, 30]);
        tree.RemoveAt(1); // remove first 30; the other 30 should remain max
        Assert.Equal(30, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterInsertRange_NewLargest() {
        var tree = LineIndexTree.FromValues([10, 20]);
        tree.InsertRange(1, [5, 999, 7]);
        Assert.Equal(999, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterRemoveRange_TakesNewMaxFromSurvivors() {
        var tree = LineIndexTree.FromValues([10, 999, 998, 20, 30]);
        tree.RemoveRange(1, 2); // remove 999 and 998; survivors are 10/20/30
        Assert.Equal(30, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterRebuild_ReturnsNewMax() {
        var tree = LineIndexTree.FromValues([100, 200, 300]);
        Assert.Equal(300, tree.MaxValue());
        tree.Rebuild([5, 10, 15]);
        Assert.Equal(15, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_AfterRebuildToEmpty_ReturnsZero() {
        var tree = LineIndexTree.FromValues([100, 200, 300]);
        tree.Rebuild([]);
        Assert.Equal(0, tree.MaxValue());
    }

    [Fact]
    public void MaxValue_LargeTree_BulkBuild() {
        // Verify _max bubbles correctly through a treap built via the
        // O(n) Cartesian-tree path (FromValues with > 256 elements
        // exercises the heap-allocated build).
        var values = new int[1024];
        for (var i = 0; i < values.Length; i++) values[i] = i + 1;
        // Insert one outlier near the middle.
        values[500] = 10_000;
        var tree = LineIndexTree.FromValues(values);
        Assert.Equal(10_000, tree.MaxValue());
    }

    [Fact]
    public void FindByPrefixSum_AfterUpdate_StillCorrect() {
        int[] v = [10, 20, 30, 40];
        var tree = LineIndexTree.FromValues(v);
        tree.Update(1, 30); // 20 → 50
        Assert.Equal(130, tree.TotalSum());
        Assert.Equal(0, tree.FindByPrefixSum(10));
        Assert.Equal(1, tree.FindByPrefixSum(11));
        Assert.Equal(1, tree.FindByPrefixSum(60));
        Assert.Equal(2, tree.FindByPrefixSum(61));
    }

    [Fact]
    public void FindByPrefixSum_UniformValues_ScrollPositionMapping() {
        var values = new int[1000];
        Array.Fill(values, 20);
        var tree = LineIndexTree.FromValues(values);
        Assert.Equal(0, tree.FindByPrefixSum(1));
        Assert.Equal(4, tree.FindByPrefixSum(100));
        Assert.Equal(5, tree.FindByPrefixSum(101));
        Assert.Equal(999, tree.FindByPrefixSum(19999));
        Assert.Equal(999, tree.FindByPrefixSum(20000));
        Assert.Equal(-1, tree.FindByPrefixSum(20001));
    }

    // ---------------------------------------------------------------
    //  Group B — InsertAt / RemoveAt / InsertRange / RemoveRange
    // ---------------------------------------------------------------

    [Fact]
    public void InsertAt_Beginning() {
        int[] v = [20, 30];
        var tree = LineIndexTree.FromValues(v);
        tree.InsertAt(0, 10);
        Assert.Equal(3, tree.Count);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(20, tree.ValueAt(1));
        Assert.Equal(30, tree.ValueAt(2));
        Assert.Equal(60, tree.TotalSum());
    }

    [Fact]
    public void InsertAt_Middle() {
        int[] v = [10, 30];
        var tree = LineIndexTree.FromValues(v);
        tree.InsertAt(1, 20);
        Assert.Equal(3, tree.Count);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(20, tree.ValueAt(1));
        Assert.Equal(30, tree.ValueAt(2));
    }

    [Fact]
    public void InsertAt_End() {
        int[] v = [10, 20];
        var tree = LineIndexTree.FromValues(v);
        tree.InsertAt(2, 30);
        Assert.Equal(3, tree.Count);
        Assert.Equal(30, tree.ValueAt(2));
        Assert.Equal(60, tree.TotalSum());
    }

    [Fact]
    public void InsertAt_EmptyTree() {
        int[] v = [];
        var tree = LineIndexTree.FromValues(v);
        tree.InsertAt(0, 42);
        Assert.Equal(1, tree.Count);
        Assert.Equal(42, tree.ValueAt(0));
    }

    [Fact]
    public void RemoveAt_Beginning() {
        int[] v = [10, 20, 30];
        var tree = LineIndexTree.FromValues(v);
        tree.RemoveAt(0);
        Assert.Equal(2, tree.Count);
        Assert.Equal(20, tree.ValueAt(0));
        Assert.Equal(30, tree.ValueAt(1));
    }

    [Fact]
    public void RemoveAt_Middle() {
        int[] v = [10, 20, 30];
        var tree = LineIndexTree.FromValues(v);
        tree.RemoveAt(1);
        Assert.Equal(2, tree.Count);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(30, tree.ValueAt(1));
    }

    [Fact]
    public void RemoveAt_End() {
        int[] v = [10, 20, 30];
        var tree = LineIndexTree.FromValues(v);
        tree.RemoveAt(2);
        Assert.Equal(2, tree.Count);
        Assert.Equal(30, tree.TotalSum());
    }

    [Fact]
    public void InsertRange_InsertsMultipleElements() {
        int[] v = [10, 40, 50];
        var tree = LineIndexTree.FromValues(v);
        int[] r = [20, 30];
        tree.InsertRange(1, r);
        Assert.Equal(5, tree.Count);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(20, tree.ValueAt(1));
        Assert.Equal(30, tree.ValueAt(2));
        Assert.Equal(40, tree.ValueAt(3));
        Assert.Equal(50, tree.ValueAt(4));
        Assert.Equal(150, tree.TotalSum());
    }

    [Fact]
    public void RemoveRange_RemovesMultipleElements() {
        int[] v = [10, 20, 30, 40, 50];
        var tree = LineIndexTree.FromValues(v);
        tree.RemoveRange(1, 3); // remove 20, 30, 40
        Assert.Equal(2, tree.Count);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(50, tree.ValueAt(1));
        Assert.Equal(60, tree.TotalSum());
    }

    [Fact]
    public void InsertAndRemove_MaintainsPrefixSums() {
        int[] v = [10, 20, 30];
        var tree = LineIndexTree.FromValues(v);
        tree.InsertAt(1, 15);      // [10, 15, 20, 30]
        tree.RemoveAt(2);              // [10, 15, 30]
        tree.InsertAt(2, 25);      // [10, 15, 25, 30]

        Assert.Equal(4, tree.Count);
        Assert.Equal(10, tree.PrefixSum(0));
        Assert.Equal(25, tree.PrefixSum(1));
        Assert.Equal(50, tree.PrefixSum(2));
        Assert.Equal(80, tree.PrefixSum(3));
    }

    [Fact]
    public void ExtractValues_MatchesInsertedOrder() {
        int[] v = [10, 20, 30];
        var tree = LineIndexTree.FromValues(v);
        tree.InsertAt(1, 15);
        tree.InsertAt(3, 25);
        var values = tree.ExtractValues();
        Assert.Equal([10, 15, 20, 25, 30], values);
    }

    // ---------------------------------------------------------------
    //  Group C — stress / property tests
    // ---------------------------------------------------------------

    [Fact]
    public void RandomOperations_ConsistentWithListReference() {
        var rng = new Random(42);
        int[] empty = [];
        var tree = LineIndexTree.FromValues(empty);
        var reference = new List<int>();

        for (var op = 0; op < 5000; op++) {
            var action = reference.Count == 0 ? 0 : rng.Next(4);
            switch (action) {
                case 0: { // insert
                    var idx = rng.Next(reference.Count + 1);
                    var val = rng.Next(1, 1000);
                    tree.InsertAt(idx, val);
                    reference.Insert(idx, val);
                    break;
                }
                case 1: { // remove
                    var idx = rng.Next(reference.Count);
                    tree.RemoveAt(idx);
                    reference.RemoveAt(idx);
                    break;
                }
                case 2: { // update
                    var idx = rng.Next(reference.Count);
                    var delta = rng.Next(-100, 100);
                    // LineIndexTree assumes non-negative values (line
                    // lengths are ≥ 0 by domain).  Clamp the delta so the
                    // resulting value never goes below zero — this lets
                    // us assert MaxValue() against the reference without
                    // tripping the absent-child=0 sentinel in _max bookkeeping.
                    if (reference[idx] + delta < 0) delta = -reference[idx];
                    tree.Update(idx, delta);
                    reference[idx] += delta;
                    break;
                }
                case 3: { // verify prefix sum
                    var idx = rng.Next(reference.Count);
                    var expected = 0L;
                    for (var j = 0; j <= idx; j++) expected += reference[j];
                    Assert.Equal(expected, tree.PrefixSum(idx));
                    break;
                }
            }
            Assert.Equal(reference.Count, tree.Count);

            // MaxValue must always agree with the reference's max (or 0 when
            // empty).  Checking on every iteration covers all four operations,
            // not just the one that just ran, because the operation may have
            // disturbed an ancestor's _max bottom-up recompute.
            var expectedMax = reference.Count == 0 ? 0 : reference.Max();
            Assert.Equal(expectedMax, tree.MaxValue());
        }

        // Final full verification.
        var extracted = tree.ExtractValues();
        Assert.Equal(reference.ToArray(), extracted);
    }

    [Fact]
    public void LargeTree_InsertRemovePerformance() {
        // Build 100K elements, do 10K random inserts/removes.
        var values = new int[100_000];
        for (var i = 0; i < values.Length; i++) values[i] = 100;
        var tree = LineIndexTree.FromValues(values);

        var rng = new Random(123);
        for (var i = 0; i < 10_000; i++) {
            if (rng.Next(2) == 0) {
                var val = rng.Next(1, 200);
                tree.InsertAt(rng.Next(tree.Count + 1), val);
            } else if (tree.Count > 1) {
                tree.RemoveAt(rng.Next(tree.Count));
            }
        }

        // Verify total sum is consistent.
        var extracted = tree.ExtractValues();
        var sum = 0L;
        foreach (var vv in extracted) sum += vv;
        Assert.Equal(sum, tree.TotalSum());
    }

    [Fact]
    public void FromValues_LargeDataset_CorrectPrefixSums() {
        const int n = 100_000;
        var values = new int[n];
        for (var i = 0; i < n; i++) values[i] = i + 1;
        var tree = LineIndexTree.FromValues(values);

        // Spot-check prefix sums.
        Assert.Equal(n, tree.Count);
        Assert.Equal(1, tree.PrefixSum(0));
        Assert.Equal(5050L, tree.PrefixSum(99));
        Assert.Equal((long)n * (n + 1) / 2, tree.TotalSum());

        // FindByPrefixSum consistency.
        Assert.Equal(99, tree.FindByPrefixSum(5050));
        Assert.Equal(100, tree.FindByPrefixSum(5051));
    }
}
