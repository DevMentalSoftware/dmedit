using DevMentalMd.Core.Collections;

namespace DevMentalMd.Core.Tests;

public class LineIndexTreeTests {
    // ---------------------------------------------------------------
    //  Group A — API compatibility with IntFenwickTree
    // ---------------------------------------------------------------

    [Fact]
    public void FromValues_PrefixSumsAreCorrect() {
        var tree = LineIndexTree.FromValues([10, 20, 30, 40, 50]);
        Assert.Equal(10, tree.PrefixSum(0));
        Assert.Equal(30, tree.PrefixSum(1));
        Assert.Equal(60, tree.PrefixSum(2));
        Assert.Equal(100, tree.PrefixSum(3));
        Assert.Equal(150, tree.PrefixSum(4));
    }

    [Fact]
    public void TotalSum_ReturnsSum() {
        var tree = LineIndexTree.FromValues([5, 10, 15]);
        Assert.Equal(30, tree.TotalSum());
    }

    [Fact]
    public void TotalSum_EmptyTree() {
        var tree = LineIndexTree.FromValues([]);
        Assert.Equal(0, tree.TotalSum());
    }

    [Fact]
    public void ValueAt_ReturnsIndividualValues() {
        var tree = LineIndexTree.FromValues([10, 20, 30, 40]);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(20, tree.ValueAt(1));
        Assert.Equal(30, tree.ValueAt(2));
        Assert.Equal(40, tree.ValueAt(3));
    }

    [Fact]
    public void Update_AdjustsValueAndPrefixSums() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.Update(1, 5); // 20 → 25
        Assert.Equal(10, tree.PrefixSum(0));
        Assert.Equal(35, tree.PrefixSum(1));
        Assert.Equal(65, tree.PrefixSum(2));
        Assert.Equal(25, tree.ValueAt(1));
    }

    [Fact]
    public void Update_NegativeDelta() {
        var tree = LineIndexTree.FromValues([100, 200, 300]);
        tree.Update(2, -50); // 300 → 250
        Assert.Equal(550, tree.TotalSum());
        Assert.Equal(250, tree.ValueAt(2));
    }

    [Fact]
    public void FindByPrefixSum_FindsCorrectIndex() {
        var tree = LineIndexTree.FromValues([10, 20, 30, 40]);
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
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        Assert.Equal(-1, tree.FindByPrefixSum(61));
        Assert.Equal(-1, tree.FindByPrefixSum(1000));
    }

    [Fact]
    public void FindByPrefixSum_TargetZeroOrNegative_ReturnsZero() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        Assert.Equal(0, tree.FindByPrefixSum(0));
        Assert.Equal(0, tree.FindByPrefixSum(-5));
    }

    [Fact]
    public void FindByPrefixSum_SingleElement() {
        var tree = LineIndexTree.FromValues([42]);
        Assert.Equal(0, tree.FindByPrefixSum(1));
        Assert.Equal(0, tree.FindByPrefixSum(42));
        Assert.Equal(-1, tree.FindByPrefixSum(43));
    }

    [Fact]
    public void FindByPrefixSum_EmptyTree() {
        var tree = LineIndexTree.FromValues([]);
        Assert.Equal(-1, tree.FindByPrefixSum(1));
    }

    [Fact]
    public void Rebuild_ReplacesAllContent() {
        var tree = LineIndexTree.FromValues([1, 2, 3]);
        Assert.Equal(6, tree.TotalSum());
        tree.Rebuild([10, 20, 30, 40]);
        Assert.Equal(4, tree.Count);
        Assert.Equal(100, tree.TotalSum());
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(40, tree.ValueAt(3));
    }

    [Fact]
    public void Rebuild_ToSmallerSize() {
        var tree = LineIndexTree.FromValues([10, 20, 30, 40, 50]);
        tree.Rebuild([100, 200]);
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

    [Fact]
    public void FindByPrefixSum_AfterUpdate_StillCorrect() {
        var tree = LineIndexTree.FromValues([10, 20, 30, 40]);
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
        var tree = LineIndexTree.FromValues([20, 30]);
        tree.InsertAt(0, 10);
        Assert.Equal(3, tree.Count);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(20, tree.ValueAt(1));
        Assert.Equal(30, tree.ValueAt(2));
        Assert.Equal(60, tree.TotalSum());
    }

    [Fact]
    public void InsertAt_Middle() {
        var tree = LineIndexTree.FromValues([10, 30]);
        tree.InsertAt(1, 20);
        Assert.Equal(3, tree.Count);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(20, tree.ValueAt(1));
        Assert.Equal(30, tree.ValueAt(2));
    }

    [Fact]
    public void InsertAt_End() {
        var tree = LineIndexTree.FromValues([10, 20]);
        tree.InsertAt(2, 30);
        Assert.Equal(3, tree.Count);
        Assert.Equal(30, tree.ValueAt(2));
        Assert.Equal(60, tree.TotalSum());
    }

    [Fact]
    public void InsertAt_EmptyTree() {
        var tree = LineIndexTree.FromValues([]);
        tree.InsertAt(0, 42);
        Assert.Equal(1, tree.Count);
        Assert.Equal(42, tree.ValueAt(0));
    }

    [Fact]
    public void RemoveAt_Beginning() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.RemoveAt(0);
        Assert.Equal(2, tree.Count);
        Assert.Equal(20, tree.ValueAt(0));
        Assert.Equal(30, tree.ValueAt(1));
    }

    [Fact]
    public void RemoveAt_Middle() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.RemoveAt(1);
        Assert.Equal(2, tree.Count);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(30, tree.ValueAt(1));
    }

    [Fact]
    public void RemoveAt_End() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.RemoveAt(2);
        Assert.Equal(2, tree.Count);
        Assert.Equal(30, tree.TotalSum());
    }

    [Fact]
    public void InsertRange_InsertsMultipleElements() {
        var tree = LineIndexTree.FromValues([10, 40, 50]);
        tree.InsertRange(1, [20, 30]);
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
        var tree = LineIndexTree.FromValues([10, 20, 30, 40, 50]);
        tree.RemoveRange(1, 3); // remove 20, 30, 40
        Assert.Equal(2, tree.Count);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(50, tree.ValueAt(1));
        Assert.Equal(60, tree.TotalSum());
    }

    [Fact]
    public void InsertAndRemove_MaintainsPrefixSums() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
        tree.InsertAt(1, 15);      // [10, 15, 20, 30]
        tree.RemoveAt(2);          // [10, 15, 30]
        tree.InsertAt(2, 25);      // [10, 15, 25, 30]

        Assert.Equal(4, tree.Count);
        Assert.Equal(10, tree.PrefixSum(0));
        Assert.Equal(25, tree.PrefixSum(1));
        Assert.Equal(50, tree.PrefixSum(2));
        Assert.Equal(80, tree.PrefixSum(3));
    }

    [Fact]
    public void ExtractValues_MatchesInsertedOrder() {
        var tree = LineIndexTree.FromValues([10, 20, 30]);
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
        var tree = LineIndexTree.FromValues([]);
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
                tree.InsertAt(rng.Next(tree.Count + 1), rng.Next(1, 200));
            } else if (tree.Count > 1) {
                tree.RemoveAt(rng.Next(tree.Count));
            }
        }

        // Verify total sum is consistent.
        var extracted = tree.ExtractValues();
        var sum = 0L;
        foreach (var v in extracted) sum += v;
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
