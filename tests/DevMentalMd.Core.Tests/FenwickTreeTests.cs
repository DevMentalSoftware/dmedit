using DevMentalMd.Core.Collections;

namespace DevMentalMd.Core.Tests;

public class FenwickTreeTests {
    [Fact]
    public void FromValues_PrefixSumsAreCorrect() {
        var tree = FenwickTree.FromValues([10, 20, 30, 40, 50]);
        Assert.Equal(10, tree.PrefixSum(0));
        Assert.Equal(30, tree.PrefixSum(1));
        Assert.Equal(60, tree.PrefixSum(2));
        Assert.Equal(100, tree.PrefixSum(3));
        Assert.Equal(150, tree.PrefixSum(4));
    }

    [Fact]
    public void TotalSum_ReturnsSum() {
        var tree = FenwickTree.FromValues([5, 10, 15]);
        Assert.Equal(30, tree.TotalSum());
    }

    [Fact]
    public void TotalSum_EmptyTree() {
        var tree = new FenwickTree(0);
        Assert.Equal(0, tree.TotalSum());
    }

    [Fact]
    public void ValueAt_ReturnsIndividualValues() {
        var tree = FenwickTree.FromValues([10, 20, 30, 40]);
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(20, tree.ValueAt(1));
        Assert.Equal(30, tree.ValueAt(2));
        Assert.Equal(40, tree.ValueAt(3));
    }

    [Fact]
    public void Update_AdjustsValueAndPrefixSums() {
        var tree = FenwickTree.FromValues([10, 20, 30]);
        tree.Update(1, 5); // 20 → 25
        Assert.Equal(10, tree.PrefixSum(0));
        Assert.Equal(35, tree.PrefixSum(1));
        Assert.Equal(65, tree.PrefixSum(2));
        Assert.Equal(25, tree.ValueAt(1));
    }

    [Fact]
    public void Update_NegativeDelta() {
        var tree = FenwickTree.FromValues([100, 200, 300]);
        tree.Update(2, -50); // 300 → 250
        Assert.Equal(550, tree.TotalSum());
        Assert.Equal(250, tree.ValueAt(2));
    }

    [Fact]
    public void FindByPrefixSum_FindsCorrectIndex() {
        // Values: [10, 20, 30, 40]
        // PrefixSums: [10, 30, 60, 100]
        var tree = FenwickTree.FromValues([10, 20, 30, 40]);

        // target <= 10 → index 0
        Assert.Equal(0, tree.FindByPrefixSum(5));
        Assert.Equal(0, tree.FindByPrefixSum(10));

        // target in (10, 30] → index 1
        Assert.Equal(1, tree.FindByPrefixSum(11));
        Assert.Equal(1, tree.FindByPrefixSum(30));

        // target in (30, 60] → index 2
        Assert.Equal(2, tree.FindByPrefixSum(31));
        Assert.Equal(2, tree.FindByPrefixSum(60));

        // target in (60, 100] → index 3
        Assert.Equal(3, tree.FindByPrefixSum(61));
        Assert.Equal(3, tree.FindByPrefixSum(100));
    }

    [Fact]
    public void FindByPrefixSum_TargetExceedsTotal_ReturnsNegativeOne() {
        var tree = FenwickTree.FromValues([10, 20, 30]);
        Assert.Equal(-1, tree.FindByPrefixSum(61));
        Assert.Equal(-1, tree.FindByPrefixSum(1000));
    }

    [Fact]
    public void FindByPrefixSum_TargetZeroOrNegative_ReturnsZero() {
        var tree = FenwickTree.FromValues([10, 20, 30]);
        Assert.Equal(0, tree.FindByPrefixSum(0));
        Assert.Equal(0, tree.FindByPrefixSum(-5));
    }

    [Fact]
    public void FindByPrefixSum_SingleElement() {
        var tree = FenwickTree.FromValues([42]);
        Assert.Equal(0, tree.FindByPrefixSum(1));
        Assert.Equal(0, tree.FindByPrefixSum(42));
        Assert.Equal(-1, tree.FindByPrefixSum(43));
    }

    [Fact]
    public void FindByPrefixSum_EmptyTree_ReturnsNegativeOne() {
        var tree = new FenwickTree(0);
        Assert.Equal(-1, tree.FindByPrefixSum(1));
    }

    [Fact]
    public void Rebuild_ReplacesAllContent() {
        var tree = FenwickTree.FromValues([1, 2, 3]);
        Assert.Equal(6, tree.TotalSum());

        tree.Rebuild([10, 20, 30, 40]);
        Assert.Equal(4, tree.Count);
        Assert.Equal(100, tree.TotalSum());
        Assert.Equal(10, tree.ValueAt(0));
        Assert.Equal(40, tree.ValueAt(3));
    }

    [Fact]
    public void Rebuild_ToSmallerSize() {
        var tree = FenwickTree.FromValues([10, 20, 30, 40, 50]);
        tree.Rebuild([100, 200]);
        Assert.Equal(2, tree.Count);
        Assert.Equal(300, tree.TotalSum());
    }

    [Fact]
    public void LargeTree_CorrectBehavior() {
        const int n = 10_000;
        var values = new double[n];
        for (var i = 0; i < n; i++) {
            values[i] = i + 1; // 1, 2, 3, ..., 10000
        }
        var tree = FenwickTree.FromValues(values);

        // Total = n*(n+1)/2 = 50_005_000
        Assert.Equal(50_005_000, tree.TotalSum());

        // Prefix sum at index 99 = 100*101/2 = 5050
        Assert.Equal(5050, tree.PrefixSum(99));

        // Find: prefix sum >= 5050 should be index 99
        Assert.Equal(99, tree.FindByPrefixSum(5050));

        // Find: prefix sum >= 5051 should be index 100
        Assert.Equal(100, tree.FindByPrefixSum(5051));
    }

    [Fact]
    public void FindByPrefixSum_AfterUpdate_StillCorrect() {
        var tree = FenwickTree.FromValues([10, 20, 30, 40]);
        // Change index 1 from 20 to 50 (+30)
        tree.Update(1, 30);
        // New values: [10, 50, 30, 40], prefix sums: [10, 60, 90, 130]
        Assert.Equal(130, tree.TotalSum());
        Assert.Equal(0, tree.FindByPrefixSum(10));
        Assert.Equal(1, tree.FindByPrefixSum(11));
        Assert.Equal(1, tree.FindByPrefixSum(60));
        Assert.Equal(2, tree.FindByPrefixSum(61));
    }

    [Fact]
    public void FindByPrefixSum_UniformValues_ScrollPositionMapping() {
        // Simulate 1000 blocks each 20px tall
        var values = new double[1000];
        Array.Fill(values, 20.0);
        var tree = FenwickTree.FromValues(values);

        // Scroll position 0 → block 0
        Assert.Equal(0, tree.FindByPrefixSum(1));
        // Scroll position 100 → block 5 (prefix sum at 4 = 100, so target 101 → index 5)
        Assert.Equal(4, tree.FindByPrefixSum(100));
        Assert.Equal(5, tree.FindByPrefixSum(101));
        // Scroll position 19999 → block 999
        Assert.Equal(999, tree.FindByPrefixSum(19999));
        // Scroll position 20000 → block 999 (last block)
        Assert.Equal(999, tree.FindByPrefixSum(20000));
        // Scroll position 20001 → -1 (beyond)
        Assert.Equal(-1, tree.FindByPrefixSum(20001));
    }
}
