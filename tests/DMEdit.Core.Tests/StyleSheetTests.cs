using DMEdit.Core.Blocks;
using DMEdit.Core.Styles;

namespace DMEdit.Core.Tests;

public class StyleSheetTests {
    // -----------------------------------------------------------------
    // Default stylesheet
    // -----------------------------------------------------------------

    [Fact]
    public void CreateDefault_HasAllBlockTypes() {
        var sheet = StyleSheet.CreateDefault();
        foreach (var type in Enum.GetValues<BlockType>()) {
            var style = sheet.GetBlockStyle(type);
            Assert.NotNull(style);
            Assert.True(style.FontSize > 0, $"FontSize for {type} should be > 0");
        }
    }

    [Fact]
    public void CreateDefault_HasAllInlineTypes() {
        var sheet = StyleSheet.CreateDefault();
        foreach (var type in Enum.GetValues<InlineSpanType>()) {
            var style = sheet.GetInlineStyle(type);
            Assert.NotNull(style);
        }
    }

    [Fact]
    public void CreateDefault_HeadingsAreLargerThanParagraph() {
        var sheet = StyleSheet.CreateDefault();
        var paraStyle = sheet.GetBlockStyle(BlockType.Paragraph);
        var h1Style = sheet.GetBlockStyle(BlockType.Heading1);
        var h2Style = sheet.GetBlockStyle(BlockType.Heading2);
        var h3Style = sheet.GetBlockStyle(BlockType.Heading3);

        Assert.True(h1Style.FontSize > h2Style.FontSize);
        Assert.True(h2Style.FontSize > h3Style.FontSize);
        Assert.True(h3Style.FontSize > paraStyle.FontSize);
    }

    [Fact]
    public void CreateDefault_HeadingsAreBold() {
        var sheet = StyleSheet.CreateDefault();
        for (var type = BlockType.Heading1; type <= BlockType.Heading6; type++) {
            var style = sheet.GetBlockStyle(type);
            Assert.Equal(700, style.FontWeight);
        }
    }

    [Fact]
    public void CreateDefault_CodeBlockIsMonospace() {
        var sheet = StyleSheet.CreateDefault();
        var codeStyle = sheet.GetBlockStyle(BlockType.CodeBlock);
        Assert.True(codeStyle.IsMonospace);
        Assert.NotNull(codeStyle.BackgroundColor);
    }

    [Fact]
    public void CreateDefault_BoldInlineHasWeight700() {
        var sheet = StyleSheet.CreateDefault();
        var boldStyle = sheet.GetInlineStyle(InlineSpanType.Bold);
        Assert.Equal(700, boldStyle.FontWeight);
    }

    [Fact]
    public void CreateDefault_LinkHasUnderline() {
        var sheet = StyleSheet.CreateDefault();
        var linkStyle = sheet.GetInlineStyle(InlineSpanType.Link);
        Assert.True(linkStyle.Underline);
        Assert.NotNull(linkStyle.ForegroundColor);
    }

    // -----------------------------------------------------------------
    // BlockStyle.EstimateHeight
    // -----------------------------------------------------------------

    [Fact]
    public void EstimateHeight_EmptyBlock_OneLineHeight() {
        var style = new BlockStyle {
            FontSize = 14,
            LineHeight = 1.5,
            MarginTop = 10,
            MarginBottom = 10,
        };
        var height = style.EstimateHeight(0, 720);
        // 14 * 1.5 + 10 + 10 = 41
        Assert.Equal(41, height);
    }

    [Fact]
    public void EstimateHeight_SingleLine_FitsInWrapWidth() {
        var style = new BlockStyle {
            FontSize = 14,
            LineHeight = 1.5,
            AvgCharWidth = 7.0,
            MarginTop = 0,
            MarginBottom = 0,
        };
        // 10 chars × 7px = 70px < 720px → 1 visual line
        var height = style.EstimateHeight(10, 720);
        Assert.Equal(14 * 1.5, height);
    }

    [Fact]
    public void EstimateHeight_MultipleLines_WrapsCorrectly() {
        var style = new BlockStyle {
            FontSize = 14,
            LineHeight = 1.5,
            AvgCharWidth = 7.0,
            MarginTop = 0,
            MarginBottom = 0,
        };
        // 200 chars × 7px = 1400px / 700px effective = 2 visual lines
        var height = style.EstimateHeight(200, 700);
        Assert.Equal(2 * 14 * 1.5, height);
    }

    [Fact]
    public void EstimateHeight_IncludesMargins() {
        var style = new BlockStyle {
            FontSize = 14,
            LineHeight = 1.0,
            AvgCharWidth = 7.0,
            MarginTop = 10,
            MarginBottom = 10,
        };
        // 10 chars × 7 = 70px < 720px → 1 line
        // 14 * 1.0 + 10 + 10 = 34
        var height = style.EstimateHeight(10, 720);
        Assert.Equal(34, height);
    }

    [Fact]
    public void EstimateHeight_IncludesPadding() {
        var style = new BlockStyle {
            FontSize = 14,
            LineHeight = 1.0,
            AvgCharWidth = 7.0,
            MarginBottom = 0,
            PaddingTop = 8,
            PaddingBottom = 8,
            PaddingLeft = 12,
            PaddingRight = 12,
        };
        // Effective width = 720 - 12 - 12 = 696
        // 10 chars × 7 = 70px < 696px → 1 line
        // 14 * 1.0 + 8 + 8 = 30
        var height = style.EstimateHeight(10, 720);
        Assert.Equal(30, height);
    }

    [Fact]
    public void EstimateHeight_RespectsMaxVisibleLines() {
        var style = new BlockStyle {
            FontSize = 14,
            LineHeight = 1.0,
            AvgCharWidth = 7.0,
            MarginBottom = 0,
            MaxVisibleLines = 5,
        };
        // 1000 chars × 7 = 7000px / 720px = 10 lines, but capped at 5
        var height = style.EstimateHeight(1000, 720);
        Assert.Equal(5 * 14.0, height);
    }

    [Fact]
    public void EstimateHeight_FallbackCharWidth_WhenZero() {
        var style = new BlockStyle {
            FontSize = 14,
            LineHeight = 1.0,
            MarginBottom = 0,
            AvgCharWidth = 0, // not set
        };
        // Fallback = FontSize * 0.5 = 7.0
        // 100 chars × 7 = 700px / 720px = 1 line
        var height = style.EstimateHeight(100, 720);
        Assert.Equal(14.0, height);
    }

    // -----------------------------------------------------------------
    // StyleSheet.EstimateBlockHeight
    // -----------------------------------------------------------------

    [Fact]
    public void StyleSheet_EstimateBlockHeight_UseCorrectStyle() {
        var sheet = StyleSheet.CreateDefault();
        sheet.WrapWidth = 720;

        var paraBlock = new Block(BlockType.Paragraph, "Short text");
        var h1Block = new Block(BlockType.Heading1, "Title");

        var paraHeight = sheet.EstimateBlockHeight(paraBlock);
        var h1Height = sheet.EstimateBlockHeight(h1Block);

        // H1 should be taller due to larger font and margins
        Assert.True(h1Height > paraHeight);
    }

    // -----------------------------------------------------------------
    // Custom style registration
    // -----------------------------------------------------------------

    [Fact]
    public void SetBlockStyle_OverridesDefault() {
        var sheet = StyleSheet.CreateDefault();
        var customStyle = new BlockStyle { FontSize = 99 };
        sheet.SetBlockStyle(BlockType.Paragraph, customStyle);
        Assert.Equal(99, sheet.GetBlockStyle(BlockType.Paragraph).FontSize);
    }

    [Fact]
    public void SetInlineStyle_OverridesDefault() {
        var sheet = StyleSheet.CreateDefault();
        var customStyle = new InlineStyle { FontWeight = 900 };
        sheet.SetInlineStyle(InlineSpanType.Bold, customStyle);
        Assert.Equal(900, sheet.GetInlineStyle(InlineSpanType.Bold).FontWeight);
    }

    // -----------------------------------------------------------------
    // Font metrics update
    // -----------------------------------------------------------------

    [Fact]
    public void UpdateFontMetrics_ChangesAvgCharWidth() {
        var sheet = StyleSheet.CreateDefault();
        sheet.UpdateFontMetrics(BlockType.Paragraph, 8.5);
        Assert.Equal(8.5, sheet.GetBlockStyle(BlockType.Paragraph).AvgCharWidth);
    }

    [Fact]
    public void UpdateFontMetrics_AffectsHeightEstimate() {
        var sheet = StyleSheet.CreateDefault();
        var block = new Block(BlockType.Paragraph, new string('x', 200));

        sheet.UpdateFontMetrics(BlockType.Paragraph, 5.0);
        var heightNarrowChars = sheet.EstimateBlockHeight(block);

        sheet.UpdateFontMetrics(BlockType.Paragraph, 10.0);
        var heightWideChars = sheet.EstimateBlockHeight(block);

        // Wider characters → more visual lines → taller
        Assert.True(heightWideChars > heightNarrowChars);
    }

    // -----------------------------------------------------------------
    // Integration: BlockDocument + StyleSheet height estimation
    // -----------------------------------------------------------------

    [Fact]
    public void BlockDocument_WithStyleEstimator_UsesStyleHeights() {
        var sheet = StyleSheet.CreateDefault();
        sheet.WrapWidth = 720;

        var doc = new BlockDocument([
            new Block(BlockType.Heading1, "Title"),
            new Block(BlockType.Paragraph, "Body text here"),
        ]);
        doc.HeightEstimator = sheet.EstimateBlockHeight;

        // Force tree rebuild by triggering a structural change
        doc.InsertBlock(2, new Block(BlockType.Paragraph, "extra"));
        doc.RemoveBlock(2);

        var h1Height = doc.GetBlockHeight(0);
        var paraHeight = doc.GetBlockHeight(1);

        // H1 should use the heading style (larger font, margins)
        Assert.True(h1Height > paraHeight);
    }
}
