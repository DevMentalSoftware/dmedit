using DevMentalMd.Core.Blocks;

namespace DevMentalMd.Core.Tests;

public class BlockTests {
    // -----------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------

    [Fact]
    public void NewBlock_HasTypeAndText() {
        var block = new Block(BlockType.Paragraph, "Hello");
        Assert.Equal(BlockType.Paragraph, block.Type);
        Assert.Equal("Hello", block.Text);
        Assert.Equal(5, block.Length);
        Assert.Empty(block.Spans);
    }

    [Fact]
    public void NewBlock_DefaultEmptyText() {
        var block = new Block(BlockType.Heading1);
        Assert.Equal("", block.Text);
        Assert.Equal(0, block.Length);
    }

    // -----------------------------------------------------------------
    // Text editing
    // -----------------------------------------------------------------

    [Fact]
    public void InsertText_AtStart() {
        var block = new Block(BlockType.Paragraph, "world");
        block.InsertText(0, "hello ");
        Assert.Equal("hello world", block.Text);
    }

    [Fact]
    public void InsertText_AtEnd() {
        var block = new Block(BlockType.Paragraph, "hello");
        block.InsertText(5, " world");
        Assert.Equal("hello world", block.Text);
    }

    [Fact]
    public void InsertText_InMiddle() {
        var block = new Block(BlockType.Paragraph, "helo");
        block.InsertText(2, "l");
        Assert.Equal("hello", block.Text);
    }

    [Fact]
    public void InsertText_FiresChanged() {
        var block = new Block(BlockType.Paragraph, "test");
        var fired = false;
        block.Changed += (_, _) => fired = true;
        block.InsertText(0, "x");
        Assert.True(fired);
    }

    [Fact]
    public void DeleteText_FromStart() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.DeleteText(0, 6);
        Assert.Equal("world", block.Text);
    }

    [Fact]
    public void DeleteText_FromEnd() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.DeleteText(5, 6);
        Assert.Equal("hello", block.Text);
    }

    [Fact]
    public void DeleteText_InMiddle() {
        var block = new Block(BlockType.Paragraph, "hello");
        block.DeleteText(2, 1);
        Assert.Equal("helo", block.Text);
    }

    [Fact]
    public void SetText_ReplacesEverything() {
        var block = new Block(BlockType.Paragraph, "old");
        block.ApplySpan(InlineSpanType.Bold, 0, 3);
        block.SetText("new content");
        Assert.Equal("new content", block.Text);
        Assert.Empty(block.Spans);
    }

    // -----------------------------------------------------------------
    // Span management
    // -----------------------------------------------------------------

    [Fact]
    public void ApplySpan_AddsSpan() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 5);
        Assert.Single(block.Spans);
        Assert.Equal(InlineSpanType.Bold, block.Spans[0].Type);
        Assert.Equal(0, block.Spans[0].Start);
        Assert.Equal(5, block.Spans[0].Length);
    }

    [Fact]
    public void ApplySpan_OverlappingSpansMerge() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 5);
        block.ApplySpan(InlineSpanType.Bold, 3, 5);
        // Should merge into [0, 8)
        Assert.Single(block.Spans);
        Assert.Equal(0, block.Spans[0].Start);
        Assert.Equal(8, block.Spans[0].Length);
    }

    [Fact]
    public void ApplySpan_AdjacentSpansMerge() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 5);
        block.ApplySpan(InlineSpanType.Bold, 5, 6);
        // Should merge into [0, 11)
        Assert.Single(block.Spans);
        Assert.Equal(0, block.Spans[0].Start);
        Assert.Equal(11, block.Spans[0].Length);
    }

    [Fact]
    public void ApplySpan_DifferentTypesDoNotMerge() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 5);
        block.ApplySpan(InlineSpanType.Italic, 3, 5);
        Assert.Equal(2, block.Spans.Count);
    }

    [Fact]
    public void RemoveSpan_FullyContained() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 11);
        block.RemoveSpan(InlineSpanType.Bold, 0, 11);
        Assert.Empty(block.Spans);
    }

    [Fact]
    public void RemoveSpan_TrimFromLeft() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 11);
        block.RemoveSpan(InlineSpanType.Bold, 0, 5);
        // Remaining: [5, 11)
        Assert.Single(block.Spans);
        Assert.Equal(5, block.Spans[0].Start);
        Assert.Equal(6, block.Spans[0].Length);
    }

    [Fact]
    public void RemoveSpan_TrimFromRight() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 11);
        block.RemoveSpan(InlineSpanType.Bold, 6, 5);
        // Remaining: [0, 6)
        Assert.Single(block.Spans);
        Assert.Equal(0, block.Spans[0].Start);
        Assert.Equal(6, block.Spans[0].Length);
    }

    [Fact]
    public void RemoveSpan_SplitsSpan() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 11);
        block.RemoveSpan(InlineSpanType.Bold, 3, 4);
        // Remaining: [0, 3) and [7, 11)
        Assert.Equal(2, block.Spans.Count);
        var sorted = block.Spans.OrderBy(s => s.Start).ToList();
        Assert.Equal(0, sorted[0].Start);
        Assert.Equal(3, sorted[0].Length);
        Assert.Equal(7, sorted[1].Start);
        Assert.Equal(4, sorted[1].Length);
    }

    [Fact]
    public void HasSpanAt_ReturnsCorrectly() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 2, 5);
        Assert.False(block.HasSpanAt(InlineSpanType.Bold, 0));
        Assert.False(block.HasSpanAt(InlineSpanType.Bold, 1));
        Assert.True(block.HasSpanAt(InlineSpanType.Bold, 2));
        Assert.True(block.HasSpanAt(InlineSpanType.Bold, 6));
        Assert.False(block.HasSpanAt(InlineSpanType.Bold, 7));
    }

    // -----------------------------------------------------------------
    // Span adjustment on text edits
    // -----------------------------------------------------------------

    [Fact]
    public void InsertText_BeforeSpan_ShiftsSpan() {
        var block = new Block(BlockType.Paragraph, "hello");
        block.ApplySpan(InlineSpanType.Bold, 2, 3); // "llo"
        block.InsertText(0, "xx"); // "xxhello"
        Assert.Single(block.Spans);
        Assert.Equal(4, block.Spans[0].Start); // shifted by 2
        Assert.Equal(3, block.Spans[0].Length); // same length
    }

    [Fact]
    public void InsertText_InsideSpan_ExpandsSpan() {
        var block = new Block(BlockType.Paragraph, "hello");
        block.ApplySpan(InlineSpanType.Bold, 1, 3); // "ell"
        block.InsertText(2, "xx"); // "hexxllo"
        Assert.Single(block.Spans);
        Assert.Equal(1, block.Spans[0].Start);
        Assert.Equal(5, block.Spans[0].Length); // expanded by 2
    }

    [Fact]
    public void DeleteText_BeforeSpan_ShiftsSpan() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 6, 5); // "world"
        block.DeleteText(0, 3); // "lo world"
        Assert.Single(block.Spans);
        Assert.Equal(3, block.Spans[0].Start); // shifted back by 3
        Assert.Equal(5, block.Spans[0].Length);
    }

    [Fact]
    public void DeleteText_InsideSpan_ShrinksSpan() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 11);
        block.DeleteText(3, 4); // "helorld"
        Assert.Single(block.Spans);
        Assert.Equal(0, block.Spans[0].Start);
        Assert.Equal(7, block.Spans[0].Length); // shrunk by 4
    }

    [Fact]
    public void DeleteText_FullyCoversSpan_RemovesSpan() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 2, 3);
        block.DeleteText(0, 11);
        Assert.Empty(block.Spans);
    }

    // -----------------------------------------------------------------
    // Split / Merge
    // -----------------------------------------------------------------

    [Fact]
    public void SplitAt_SplitsTextAndSpans() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 0, 5); // "hello"
        block.ApplySpan(InlineSpanType.Italic, 6, 5); // "world"

        var right = block.SplitAt(6);

        Assert.Equal("hello ", block.Text);
        Assert.Equal("world", right.Text);
        Assert.Equal(BlockType.Paragraph, right.Type);

        // Bold span stays in left
        Assert.Single(block.Spans);
        Assert.Equal(InlineSpanType.Bold, block.Spans[0].Type);
        Assert.Equal(0, block.Spans[0].Start);
        Assert.Equal(5, block.Spans[0].Length);

        // Italic span moves to right, offset adjusted
        Assert.Single(right.Spans);
        Assert.Equal(InlineSpanType.Italic, right.Spans[0].Type);
        Assert.Equal(0, right.Spans[0].Start);
        Assert.Equal(5, right.Spans[0].Length);
    }

    [Fact]
    public void SplitAt_SpanStraddlingSplitPoint_IsSplit() {
        var block = new Block(BlockType.Paragraph, "hello world");
        block.ApplySpan(InlineSpanType.Bold, 3, 5); // "lo wo"

        var right = block.SplitAt(6);

        Assert.Equal("hello ", block.Text);
        Assert.Equal("world", right.Text);

        // Left gets [3, 6) = length 3
        Assert.Single(block.Spans);
        Assert.Equal(3, block.Spans[0].Start);
        Assert.Equal(3, block.Spans[0].Length);

        // Right gets [0, 2) = length 2
        Assert.Single(right.Spans);
        Assert.Equal(0, right.Spans[0].Start);
        Assert.Equal(2, right.Spans[0].Length);
    }

    [Fact]
    public void SplitAt_Beginning_LeftIsEmpty() {
        var block = new Block(BlockType.Heading1, "Title");
        var right = block.SplitAt(0);
        Assert.Equal("", block.Text);
        Assert.Equal("Title", right.Text);
        Assert.Equal(BlockType.Heading1, right.Type);
    }

    [Fact]
    public void SplitAt_End_RightIsEmpty() {
        var block = new Block(BlockType.Paragraph, "Hello");
        var right = block.SplitAt(5);
        Assert.Equal("Hello", block.Text);
        Assert.Equal("", right.Text);
    }

    [Fact]
    public void MergeFrom_CombinesTextAndSpans() {
        var block1 = new Block(BlockType.Paragraph, "hello ");
        block1.ApplySpan(InlineSpanType.Bold, 0, 5);

        var block2 = new Block(BlockType.Paragraph, "world");
        block2.ApplySpan(InlineSpanType.Italic, 0, 5);

        block1.MergeFrom(block2);

        Assert.Equal("hello world", block1.Text);
        Assert.Equal(2, block1.Spans.Count);

        var bold = block1.Spans.First(s => s.Type == InlineSpanType.Bold);
        Assert.Equal(0, bold.Start);
        Assert.Equal(5, bold.Length);

        var italic = block1.Spans.First(s => s.Type == InlineSpanType.Italic);
        Assert.Equal(6, italic.Start); // shifted by "hello ".Length
        Assert.Equal(5, italic.Length);
    }

    // -----------------------------------------------------------------
    // Link spans preserve URL
    // -----------------------------------------------------------------

    [Fact]
    public void ApplySpan_Link_PreservesUrl() {
        var block = new Block(BlockType.Paragraph, "click here");
        block.ApplySpan(InlineSpanType.Link, 0, 10, "https://example.com");
        Assert.Single(block.Spans);
        Assert.Equal("https://example.com", block.Spans[0].Url);
    }

    // -----------------------------------------------------------------
    // Metadata
    // -----------------------------------------------------------------

    [Fact]
    public void CodeBlock_HasLanguageMetadata() {
        var block = new Block(BlockType.CodeBlock, "var x = 1;") {
            Metadata = "csharp"
        };
        Assert.Equal("csharp", block.Metadata);
    }

    // -----------------------------------------------------------------
    // IndentLevel
    // -----------------------------------------------------------------

    [Fact]
    public void IndentLevel_DefaultsToZero() {
        var block = new Block(BlockType.Paragraph, "text");
        Assert.Equal(0, block.IndentLevel);
    }

    [Fact]
    public void IndentLevel_CanBeSet() {
        var block = new Block(BlockType.UnorderedListItem, "item") {
            IndentLevel = 2
        };
        Assert.Equal(2, block.IndentLevel);
    }

    [Fact]
    public void SplitAt_PreservesIndentLevel() {
        var block = new Block(BlockType.UnorderedListItem, "hello world") {
            IndentLevel = 3
        };
        var right = block.SplitAt(6);
        Assert.Equal(3, block.IndentLevel);
        Assert.Equal(3, right.IndentLevel);
    }

    // -----------------------------------------------------------------
    // Newline content rules
    // -----------------------------------------------------------------

    [Fact]
    public void AllowsNewlines_TrueForCodeBlock() {
        var block = new Block(BlockType.CodeBlock);
        Assert.True(block.AllowsNewlines);
    }

    [Fact]
    public void AllowsNewlines_FalseForParagraph() {
        var block = new Block(BlockType.Paragraph);
        Assert.False(block.AllowsNewlines);
    }

    [Fact]
    public void AllowsNewlines_FalseForHeadings() {
        for (var type = BlockType.Heading1; type <= BlockType.Heading6; type++) {
            var block = new Block(type);
            Assert.False(block.AllowsNewlines);
        }
    }

    [Fact]
    public void InsertText_Paragraph_RejectsNewline() {
        var block = new Block(BlockType.Paragraph, "hello");
        Assert.Throws<ArgumentException>(() => block.InsertText(5, "\n"));
    }

    [Fact]
    public void InsertText_Paragraph_RejectsEmbeddedNewline() {
        var block = new Block(BlockType.Paragraph, "hello");
        Assert.Throws<ArgumentException>(() => block.InsertText(5, " world\nmore"));
    }

    [Fact]
    public void InsertText_CodeBlock_AllowsNewline() {
        var block = new Block(BlockType.CodeBlock, "line1");
        block.InsertText(5, "\nline2");
        Assert.Equal("line1\nline2", block.Text);
    }

    [Fact]
    public void InsertText_CodeBlock_AllowsMultipleNewlines() {
        var block = new Block(BlockType.CodeBlock);
        block.InsertText(0, "a\nb\nc");
        Assert.Equal("a\nb\nc", block.Text);
    }

    [Fact]
    public void SetText_Paragraph_StripsNewlines() {
        var block = new Block(BlockType.Paragraph);
        block.SetText("hello\nworld\r\nmore");
        Assert.Equal("helloworldmore", block.Text);
    }

    [Fact]
    public void SetText_CodeBlock_PreservesNewlines() {
        var block = new Block(BlockType.CodeBlock);
        block.SetText("line1\nline2\nline3");
        Assert.Equal("line1\nline2\nline3", block.Text);
    }

    [Fact]
    public void InsertText_Heading_RejectsNewline() {
        var block = new Block(BlockType.Heading1, "Title");
        Assert.Throws<ArgumentException>(() => block.InsertText(5, "\n"));
    }

    // -----------------------------------------------------------------
    // Pristine / dirty state
    // -----------------------------------------------------------------

    [Fact]
    public void StringConstructor_IsNotPristine() {
        var block = new Block(BlockType.Paragraph, "hello");
        Assert.False(block.IsPristine);
    }

    [Fact]
    public void MemoryConstructor_IsPristine() {
        var source = "hello world this is a test";
        ReadOnlyMemory<char> slice = source.AsMemory(0, 5); // "hello"
        var block = new Block(BlockType.Paragraph, slice);
        Assert.True(block.IsPristine);
        Assert.Equal(5, block.Length);
    }

    [Fact]
    public void PristineBlock_TextMaterializes() {
        var source = "hello world";
        var block = new Block(BlockType.Paragraph, source.AsMemory(6, 5)); // "world"
        Assert.Equal("world", block.Text);
    }

    [Fact]
    public void PristineBlock_TextMemory_IsZeroCopy() {
        var source = "hello world";
        var memory = source.AsMemory(6, 5); // "world"
        var block = new Block(BlockType.Paragraph, memory);

        // TextMemory should reference the exact same memory region
        Assert.Equal(5, block.TextMemory.Length);
        Assert.True(block.IsPristine); // Text not accessed yet
        Assert.Equal("world", new string(block.TextMemory.Span));
        Assert.True(block.IsPristine); // TextMemory access doesn't materialize
    }

    [Fact]
    public void PristineBlock_LengthDoesNotMaterialize() {
        var source = "hello world";
        var block = new Block(BlockType.Paragraph, source.AsMemory(0, 5));
        Assert.Equal(5, block.Length);
        Assert.True(block.IsPristine); // Length access doesn't materialize
    }

    [Fact]
    public void PristineBlock_InsertText_PromotesToDirty() {
        var source = "hello world";
        var block = new Block(BlockType.Paragraph, source.AsMemory(0, 5));
        Assert.True(block.IsPristine);
        block.InsertText(5, "!");
        Assert.False(block.IsPristine);
        Assert.Equal("hello!", block.Text);
    }

    [Fact]
    public void PristineBlock_DeleteText_PromotesToDirty() {
        var source = "hello world";
        var block = new Block(BlockType.Paragraph, source.AsMemory(0, 5));
        Assert.True(block.IsPristine);
        block.DeleteText(0, 2);
        Assert.False(block.IsPristine);
        Assert.Equal("llo", block.Text);
    }

    [Fact]
    public void PristineBlock_SetText_PromotesToDirty() {
        var source = "hello world";
        var block = new Block(BlockType.Paragraph, source.AsMemory(0, 5));
        Assert.True(block.IsPristine);
        block.SetText("new");
        Assert.False(block.IsPristine);
        Assert.Equal("new", block.Text);
    }

    [Fact]
    public void PristineBlock_SplitAt_BothHalvesStayPristine() {
        var source = "hello world";
        var block = new Block(BlockType.Paragraph, source.AsMemory(0, 11));
        Assert.True(block.IsPristine);

        var right = block.SplitAt(6);

        // Both halves should be pristine — they're slices of the original
        Assert.True(block.IsPristine);
        Assert.True(right.IsPristine);
        Assert.Equal("hello ", block.Text);
        Assert.Equal("world", right.Text);
    }

    [Fact]
    public void PristineBlock_MergeFrom_PromotesToDirty() {
        var source = "hello world";
        var block1 = new Block(BlockType.Paragraph, source.AsMemory(0, 6));  // "hello "
        var block2 = new Block(BlockType.Paragraph, source.AsMemory(6, 5));  // "world"
        Assert.True(block1.IsPristine);

        block1.MergeFrom(block2);

        Assert.False(block1.IsPristine); // concatenation requires new string
        Assert.Equal("hello world", block1.Text);
    }

    [Fact]
    public void PristineBlock_WriteTo_DoesNotMaterialize() {
        var source = "hello world";
        var block = new Block(BlockType.Paragraph, source.AsMemory(6, 5)); // "world"

        using var writer = new StringWriter();
        block.WriteTo(writer);

        Assert.Equal("world", writer.ToString());
        Assert.True(block.IsPristine); // WriteTo should not materialize
    }

    [Fact]
    public void PristineBlock_MultipleSlicesShareSource() {
        // Simulate what a file loader would do: one string, many slices
        var fileContent = "Line one\nLine two\nLine three";
        var block1 = new Block(BlockType.Paragraph, fileContent.AsMemory(0, 8));    // "Line one"
        var block2 = new Block(BlockType.Paragraph, fileContent.AsMemory(9, 8));    // "Line two"
        var block3 = new Block(BlockType.Paragraph, fileContent.AsMemory(18, 10));  // "Line three"

        Assert.True(block1.IsPristine);
        Assert.True(block2.IsPristine);
        Assert.True(block3.IsPristine);

        Assert.Equal("Line one", block1.Text);
        Assert.Equal("Line two", block2.Text);
        Assert.Equal("Line three", block3.Text);

        // Only block2 is edited — block1 and block3 stay pristine
        block2.InsertText(4, " and a half");
        Assert.True(block1.IsPristine);
        Assert.False(block2.IsPristine);
        Assert.True(block3.IsPristine);
        Assert.Equal("Line and a half two", block2.Text);
    }
}
