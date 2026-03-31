using DMEdit.Core.Documents;

namespace DMEdit.Core.Tests;

public class LineEndingInfoTests {
    [Fact]
    public void FromCounts_PreservesCountValues() {
        var info = LineEndingInfo.FromCounts(10, 5, 2);
        Assert.Equal(10, info.LfCount);
        Assert.Equal(5, info.CrlfCount);
        Assert.Equal(2, info.CrCount);
        Assert.Equal(LineEnding.LF, info.Dominant);
        Assert.True(info.IsMixed);
    }

    [Fact]
    public void FromCounts_SingleStyle_NotMixed() {
        var info = LineEndingInfo.FromCounts(0, 10, 0);
        Assert.Equal(LineEnding.CRLF, info.Dominant);
        Assert.False(info.IsMixed);
        Assert.Equal(0, info.LfCount);
        Assert.Equal(10, info.CrlfCount);
        Assert.Equal(0, info.CrCount);
    }

    [Fact]
    public void FromCounts_AllZero_ReturnsPlatformDefault() {
        var info = LineEndingInfo.FromCounts(0, 0, 0);
        Assert.False(info.IsMixed);
    }
}
