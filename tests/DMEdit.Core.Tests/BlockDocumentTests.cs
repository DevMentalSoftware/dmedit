using DMEdit.Core.Blocks;

namespace DMEdit.Core.Tests;

public class BlockDocumentTests {
    // -----------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------

    [Fact]
    public void EmptyDocument_HasNoBlocks() {
        var doc = new BlockDocument();
        Assert.Equal(0, doc.BlockCount);
        Assert.Equal(0, doc.TotalCharLength);
        Assert.Equal(0, doc.TotalHeight);
    }

    [Fact]
    public void FromBlocks_HasCorrectCount() {
        var doc = new BlockDocument([
            new Block(BlockType.Heading1, "Title"),
            new Block(BlockType.Paragraph, "Body text"),
        ]);
        Assert.Equal(2, doc.BlockCount);
        Assert.Equal("Title", doc[0].Text);
        Assert.Equal("Body text", doc[1].Text);
    }

    [Fact]
    public void FromText_SplitsOnNewlines() {
        var doc = BlockDocument.FromText("Hello\nWorld\nThird line");
        Assert.Equal(3, doc.BlockCount);
        Assert.Equal("Hello", doc[0].Text);
        Assert.Equal("World", doc[1].Text);
        Assert.Equal("Third line", doc[2].Text);
        Assert.All(doc.Blocks, b => Assert.Equal(BlockType.Paragraph, b.Type));
    }

    [Fact]
    public void FromText_HandlesCarriageReturnNewline() {
        var doc = BlockDocument.FromText("Line1\r\nLine2");
        Assert.Equal(2, doc.BlockCount);
        Assert.Equal("Line1", doc[0].Text);
        Assert.Equal("Line2", doc[1].Text);
    }

    [Fact]
    public void FromText_EmptyString_CreatesSingleEmptyBlock() {
        var doc = BlockDocument.FromText("");
        Assert.Equal(1, doc.BlockCount);
        Assert.Equal("", doc[0].Text);
    }

    // -----------------------------------------------------------------
    // Character length tracking
    // -----------------------------------------------------------------

    [Fact]
    public void TotalCharLength_SumsAllBlocks() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello"),       // 5
            new Block(BlockType.Paragraph, "World"),       // 5
            new Block(BlockType.Paragraph, "!"),           // 1
        ]);
        Assert.Equal(11, doc.TotalCharLength);
    }

    [Fact]
    public void BlockCharStart_ReturnsCorrectOffsets() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello"),       // 5 chars → start 0
            new Block(BlockType.Paragraph, "World"),       // 5 chars → start 5
            new Block(BlockType.Paragraph, "!"),           // 1 char  → start 10
        ]);
        Assert.Equal(0, doc.BlockCharStart(0));
        Assert.Equal(5, doc.BlockCharStart(1));
        Assert.Equal(10, doc.BlockCharStart(2));
    }

    // -----------------------------------------------------------------
    // Height tracking
    // -----------------------------------------------------------------

    [Fact]
    public void TotalHeight_SumsEstimatedHeights() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "text"),
            new Block(BlockType.Paragraph, "text"),
        ]);
        // Default paragraph estimate = 24.0 each
        Assert.Equal(48.0, doc.TotalHeight);
    }

    [Fact]
    public void BlockTopY_ReturnsCorrectPositions() {
        var doc = new BlockDocument([
            new Block(BlockType.Heading1, "Title"),   // 48.0
            new Block(BlockType.Paragraph, "Body"),   // 24.0
            new Block(BlockType.Paragraph, "More"),   // 24.0
        ]);
        Assert.Equal(0, doc.BlockTopY(0));
        Assert.Equal(48.0, doc.BlockTopY(1));
        Assert.Equal(72.0, doc.BlockTopY(2));
    }

    [Fact]
    public void HeightEstimator_UsedWhenSet() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "text"),
            new Block(BlockType.Paragraph, "text"),
        ]);
        doc.HeightEstimator = _ => 100.0;
        // Need to trigger rebuild — insert and remove a block to force it
        doc.InsertBlock(2, new Block(BlockType.Paragraph));
        doc.RemoveBlock(2);
        Assert.Equal(200.0, doc.TotalHeight);
    }

    [Fact]
    public void UpdateBlockHeight_PatchesSingleBlock() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Block 0"),
            new Block(BlockType.Paragraph, "Block 1"),
            new Block(BlockType.Paragraph, "Block 2"),
        ]);
        // Default height: 24 each, total = 72
        Assert.Equal(72.0, doc.TotalHeight);

        // Update block 1 to actual rendered height of 50
        doc.UpdateBlockHeight(1, 50.0);

        Assert.Equal(50.0, doc.GetBlockHeight(1));
        Assert.Equal(98.0, doc.TotalHeight); // 24 + 50 + 24
        Assert.Equal(24.0, doc.BlockTopY(1));
        Assert.Equal(74.0, doc.BlockTopY(2)); // 24 + 50
    }

    // -----------------------------------------------------------------
    // FindBlockByCharOffset
    // -----------------------------------------------------------------

    [Fact]
    public void FindBlockByCharOffset_FirstBlock() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello"),
            new Block(BlockType.Paragraph, "World"),
        ]);
        var pos = doc.FindBlockByCharOffset(0);
        Assert.Equal(0, pos.BlockIndex);
        Assert.Equal(0, pos.LocalOffset);

        pos = doc.FindBlockByCharOffset(3);
        Assert.Equal(0, pos.BlockIndex);
        Assert.Equal(3, pos.LocalOffset);
    }

    [Fact]
    public void FindBlockByCharOffset_SecondBlock() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello"),   // chars 0-4
            new Block(BlockType.Paragraph, "World"),   // chars 5-9
        ]);
        var pos = doc.FindBlockByCharOffset(5);
        Assert.Equal(1, pos.BlockIndex);
        Assert.Equal(0, pos.LocalOffset);

        pos = doc.FindBlockByCharOffset(8);
        Assert.Equal(1, pos.BlockIndex);
        Assert.Equal(3, pos.LocalOffset);
    }

    [Fact]
    public void FindBlockByCharOffset_AtEndOfDocument() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello"),   // 5 chars
            new Block(BlockType.Paragraph, "World"),   // 5 chars
        ]);
        var pos = doc.FindBlockByCharOffset(10);
        Assert.Equal(1, pos.BlockIndex);
        Assert.Equal(5, pos.LocalOffset);
    }

    [Fact]
    public void FindBlockByCharOffset_BeyondEnd_ClampsToEnd() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hi"),
        ]);
        var pos = doc.FindBlockByCharOffset(999);
        Assert.Equal(0, pos.BlockIndex);
        Assert.Equal(2, pos.LocalOffset);
    }

    [Fact]
    public void FindBlockByCharOffset_Negative_ClampsToStart() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hi"),
        ]);
        var pos = doc.FindBlockByCharOffset(-5);
        Assert.Equal(0, pos.BlockIndex);
        Assert.Equal(0, pos.LocalOffset);
    }

    [Fact]
    public void FindBlockByCharOffset_EmptyDocument_ReturnsZero() {
        var doc = new BlockDocument();
        var pos = doc.FindBlockByCharOffset(0);
        Assert.Equal(0, pos.BlockIndex);
        Assert.Equal(0, pos.LocalOffset);
    }

    [Fact]
    public void FindBlockByCharOffset_EmptyBlocks() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, ""),          // 0 chars
            new Block(BlockType.Paragraph, "Hello"),     // 5 chars
        ]);
        // Offset 0 falls on the empty block
        var pos = doc.FindBlockByCharOffset(0);
        Assert.Equal(0, pos.BlockIndex);
        Assert.Equal(0, pos.LocalOffset);

        // Offset 1 should be in block 1
        pos = doc.FindBlockByCharOffset(1);
        Assert.Equal(1, pos.BlockIndex);
        Assert.Equal(1, pos.LocalOffset);
    }

    // -----------------------------------------------------------------
    // FindBlockByScrollPosition
    // -----------------------------------------------------------------

    [Fact]
    public void FindBlockByScrollPosition_AtTop() {
        var doc = new BlockDocument([
            new Block(BlockType.Heading1, "Title"),   // 48px
            new Block(BlockType.Paragraph, "Body"),   // 24px
        ]);
        Assert.Equal(0, doc.FindBlockByScrollPosition(0));
        Assert.Equal(0, doc.FindBlockByScrollPosition(24));
    }

    [Fact]
    public void FindBlockByScrollPosition_SecondBlock() {
        var doc = new BlockDocument([
            new Block(BlockType.Heading1, "Title"),   // 48px
            new Block(BlockType.Paragraph, "Body"),   // 24px
        ]);
        Assert.Equal(1, doc.FindBlockByScrollPosition(49));
        Assert.Equal(1, doc.FindBlockByScrollPosition(60));
    }

    [Fact]
    public void FindBlockByScrollPosition_BeyondEnd() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Only"),
        ]);
        Assert.Equal(0, doc.FindBlockByScrollPosition(999));
    }

    // -----------------------------------------------------------------
    // InsertBlock
    // -----------------------------------------------------------------

    [Fact]
    public void InsertBlock_AtEnd() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "First"),
        ]);
        doc.InsertBlock(1, new Block(BlockType.Paragraph, "Second"));
        Assert.Equal(2, doc.BlockCount);
        Assert.Equal("Second", doc[1].Text);
        Assert.Equal(11, doc.TotalCharLength);
    }

    [Fact]
    public void InsertBlock_AtStart() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Existing"),
        ]);
        doc.InsertBlock(0, new Block(BlockType.Heading1, "Header"));
        Assert.Equal(2, doc.BlockCount);
        Assert.Equal("Header", doc[0].Text);
        Assert.Equal("Existing", doc[1].Text);
    }

    [Fact]
    public void InsertBlock_InMiddle() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "A"),
            new Block(BlockType.Paragraph, "C"),
        ]);
        doc.InsertBlock(1, new Block(BlockType.Paragraph, "B"));
        Assert.Equal(3, doc.BlockCount);
        Assert.Equal("A", doc[0].Text);
        Assert.Equal("B", doc[1].Text);
        Assert.Equal("C", doc[2].Text);
    }

    [Fact]
    public void InsertBlock_FiresStructureChanged() {
        var doc = new BlockDocument();
        BlockStructureChangedEventArgs? args = null;
        doc.StructureChanged += (_, e) => args = e;
        doc.InsertBlock(0, new Block(BlockType.Paragraph, "New"));
        Assert.NotNull(args);
        Assert.Equal(BlockStructureChangeKind.Insert, args.Kind);
        Assert.Equal(0, args.BlockIndex);
    }

    [Fact]
    public void InsertBlock_UpdatesFenwickTrees() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "First"),   // 5 chars
        ]);
        doc.InsertBlock(1, new Block(BlockType.Paragraph, "Second")); // 6 chars
        Assert.Equal(11, doc.TotalCharLength);
        Assert.Equal(5, doc.BlockCharStart(1));
    }

    // -----------------------------------------------------------------
    // RemoveBlock
    // -----------------------------------------------------------------

    [Fact]
    public void RemoveBlock_RemovesCorrectBlock() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Keep"),
            new Block(BlockType.Paragraph, "Remove"),
            new Block(BlockType.Paragraph, "Keep too"),
        ]);
        var removed = doc.RemoveBlock(1);
        Assert.Equal("Remove", removed.Text);
        Assert.Equal(2, doc.BlockCount);
        Assert.Equal("Keep", doc[0].Text);
        Assert.Equal("Keep too", doc[1].Text);
    }

    [Fact]
    public void RemoveBlock_FiresStructureChanged() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "A"),
            new Block(BlockType.Paragraph, "B"),
        ]);
        BlockStructureChangedEventArgs? args = null;
        doc.StructureChanged += (_, e) => args = e;
        doc.RemoveBlock(0);
        Assert.NotNull(args);
        Assert.Equal(BlockStructureChangeKind.Remove, args.Kind);
        Assert.Equal(0, args.BlockIndex);
    }

    [Fact]
    public void RemoveBlock_UpdatesCharTree() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "AAA"),     // 3
            new Block(BlockType.Paragraph, "BB"),      // 2
            new Block(BlockType.Paragraph, "C"),       // 1
        ]);
        Assert.Equal(6, doc.TotalCharLength);
        doc.RemoveBlock(1);
        Assert.Equal(4, doc.TotalCharLength); // 3 + 1
    }

    [Fact]
    public void RemoveBlock_UnwiresChangedEvent() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Test"),
        ]);
        var removed = doc.RemoveBlock(0);

        var contentFired = false;
        doc.ContentChanged += (_, _) => contentFired = true;
        // Editing the removed block should NOT fire ContentChanged on the doc
        removed.InsertText(0, "x");
        Assert.False(contentFired);
    }

    // -----------------------------------------------------------------
    // SplitBlock
    // -----------------------------------------------------------------

    [Fact]
    public void SplitBlock_AtMiddle() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "HelloWorld"),
        ]);
        var right = doc.SplitBlock(0, 5);
        Assert.Equal(2, doc.BlockCount);
        Assert.Equal("Hello", doc[0].Text);
        Assert.Equal("World", doc[1].Text);
        Assert.Same(right, doc[1]);
    }

    [Fact]
    public void SplitBlock_AtBeginning() {
        var doc = new BlockDocument([
            new Block(BlockType.Heading1, "Title"),
        ]);
        doc.SplitBlock(0, 0);
        Assert.Equal(2, doc.BlockCount);
        Assert.Equal("", doc[0].Text);
        Assert.Equal("Title", doc[1].Text);
    }

    [Fact]
    public void SplitBlock_AtEnd() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Text"),
        ]);
        doc.SplitBlock(0, 4);
        Assert.Equal(2, doc.BlockCount);
        Assert.Equal("Text", doc[0].Text);
        Assert.Equal("", doc[1].Text);
    }

    [Fact]
    public void SplitBlock_PreservesType() {
        var doc = new BlockDocument([
            new Block(BlockType.Heading2, "Big heading"),
        ]);
        var right = doc.SplitBlock(0, 4);
        Assert.Equal(BlockType.Heading2, right.Type);
    }

    [Fact]
    public void SplitBlock_UpdatesCharTree() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "HelloWorld"),
        ]);
        Assert.Equal(10, doc.TotalCharLength);
        doc.SplitBlock(0, 5);
        Assert.Equal(10, doc.TotalCharLength); // total unchanged
        Assert.Equal(0, doc.BlockCharStart(0));
        Assert.Equal(5, doc.BlockCharStart(1));
    }

    [Fact]
    public void SplitBlock_FiresStructureChanged() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Text"),
        ]);
        BlockStructureChangedEventArgs? args = null;
        doc.StructureChanged += (_, e) => args = e;
        doc.SplitBlock(0, 2);
        Assert.NotNull(args);
        Assert.Equal(BlockStructureChangeKind.Split, args.Kind);
        Assert.Equal(0, args.BlockIndex);
    }

    [Fact]
    public void SplitBlock_NewBlockWiredForChanges() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "HelloWorld"),
        ]);
        var right = doc.SplitBlock(0, 5);

        var contentFired = false;
        doc.ContentChanged += (_, _) => contentFired = true;
        right.InsertText(0, "x");
        Assert.True(contentFired);
    }

    // -----------------------------------------------------------------
    // MergeBlocks
    // -----------------------------------------------------------------

    [Fact]
    public void MergeBlocks_CombinesText() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello"),
            new Block(BlockType.Paragraph, "World"),
        ]);
        doc.MergeBlocks(0);
        Assert.Equal(1, doc.BlockCount);
        Assert.Equal("HelloWorld", doc[0].Text);
    }

    [Fact]
    public void MergeBlocks_UpdatesCharTree() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "AAA"),
            new Block(BlockType.Paragraph, "BB"),
        ]);
        Assert.Equal(5, doc.TotalCharLength);
        doc.MergeBlocks(0);
        Assert.Equal(5, doc.TotalCharLength); // total unchanged
        Assert.Equal(1, doc.BlockCount);
    }

    [Fact]
    public void MergeBlocks_PreservesFirstBlockSpans() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Bold"),
            new Block(BlockType.Paragraph, "Italic"),
        ]);
        doc[0].ApplySpan(InlineSpanType.Bold, 0, 4);
        doc[1].ApplySpan(InlineSpanType.Italic, 0, 6);

        doc.MergeBlocks(0);

        Assert.Equal(2, doc[0].Spans.Count);
        var bold = doc[0].Spans.First(s => s.Type == InlineSpanType.Bold);
        Assert.Equal(0, bold.Start);
        Assert.Equal(4, bold.Length);

        var italic = doc[0].Spans.First(s => s.Type == InlineSpanType.Italic);
        Assert.Equal(4, italic.Start); // shifted by "Bold".Length
        Assert.Equal(6, italic.Length);
    }

    [Fact]
    public void MergeBlocks_FiresStructureChanged() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "A"),
            new Block(BlockType.Paragraph, "B"),
        ]);
        BlockStructureChangedEventArgs? args = null;
        doc.StructureChanged += (_, e) => args = e;
        doc.MergeBlocks(0);
        Assert.NotNull(args);
        Assert.Equal(BlockStructureChangeKind.Merge, args.Kind);
        Assert.Equal(0, args.BlockIndex);
    }

    [Fact]
    public void MergeBlocks_LastBlock_Throws() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Only"),
        ]);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.MergeBlocks(0));
    }

    [Fact]
    public void MergeBlocks_UnwiresRemovedBlock() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "A"),
            new Block(BlockType.Paragraph, "B"),
        ]);
        var secondBlock = doc[1];
        doc.MergeBlocks(0);

        var contentFired = false;
        doc.ContentChanged += (_, _) => contentFired = true;
        secondBlock.InsertText(0, "x");
        Assert.False(contentFired);
    }

    // -----------------------------------------------------------------
    // ChangeBlockType
    // -----------------------------------------------------------------

    [Fact]
    public void ChangeBlockType_ChangesType() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Title"),
        ]);
        doc.ChangeBlockType(0, BlockType.Heading1);
        Assert.Equal(BlockType.Heading1, doc[0].Type);
    }

    [Fact]
    public void ChangeBlockType_UpdatesHeight() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Title"),
        ]);
        var paraHeight = doc.TotalHeight;
        doc.ChangeBlockType(0, BlockType.Heading1);
        var h1Height = doc.TotalHeight;
        Assert.True(h1Height > paraHeight); // H1 (48) > Paragraph (24)
    }

    [Fact]
    public void ChangeBlockType_FiresStructureChanged() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Text"),
        ]);
        BlockStructureChangedEventArgs? args = null;
        doc.StructureChanged += (_, e) => args = e;
        doc.ChangeBlockType(0, BlockType.CodeBlock);
        Assert.NotNull(args);
        Assert.Equal(BlockStructureChangeKind.TypeChange, args.Kind);
        Assert.Equal(0, args.BlockIndex);
    }

    // -----------------------------------------------------------------
    // Content change tracking
    // -----------------------------------------------------------------

    [Fact]
    public void ContentChanged_FiredOnTextEdit() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello"),
        ]);
        var fired = false;
        doc.ContentChanged += (_, _) => fired = true;
        doc[0].InsertText(5, " World");
        Assert.True(fired);
    }

    [Fact]
    public void ContentChanged_UpdatesCharTree() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello"),   // 5 chars
            new Block(BlockType.Paragraph, "World"),   // 5 chars
        ]);
        Assert.Equal(10, doc.TotalCharLength);

        doc[0].InsertText(5, "!!"); // "Hello!!"
        Assert.Equal(12, doc.TotalCharLength);

        // Block 1 start should have shifted
        Assert.Equal(7, doc.BlockCharStart(1));
    }

    [Fact]
    public void ContentChanged_AfterDelete_UpdatesCharTree() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello"),
            new Block(BlockType.Paragraph, "World"),
        ]);
        doc[0].DeleteText(0, 3); // "lo"
        Assert.Equal(7, doc.TotalCharLength); // 2 + 5
        Assert.Equal(2, doc.BlockCharStart(1));
    }

    // -----------------------------------------------------------------
    // Split then Merge round-trip
    // -----------------------------------------------------------------

    [Fact]
    public void SplitThenMerge_RoundTrip() {
        var doc = new BlockDocument([
            new Block(BlockType.Paragraph, "Hello World"),
        ]);
        doc[0].ApplySpan(InlineSpanType.Bold, 0, 5);

        doc.SplitBlock(0, 6); // "Hello " | "World"
        Assert.Equal(2, doc.BlockCount);

        doc.MergeBlocks(0);
        Assert.Equal(1, doc.BlockCount);
        Assert.Equal("Hello World", doc[0].Text);
        // Bold should survive the round-trip
        Assert.Single(doc[0].Spans);
        Assert.Equal(InlineSpanType.Bold, doc[0].Spans[0].Type);
        Assert.Equal(0, doc[0].Spans[0].Start);
        Assert.Equal(5, doc[0].Spans[0].Length);
    }

    // -----------------------------------------------------------------
    // Large document
    // -----------------------------------------------------------------

    [Fact]
    public void LargeDocument_1000Blocks_LookupPerformance() {
        var blocks = new List<Block>();
        for (var i = 0; i < 1000; i++) {
            blocks.Add(new Block(BlockType.Paragraph, $"Block number {i}"));
        }
        var doc = new BlockDocument(blocks);

        // Verify total char length
        var expectedChars = blocks.Sum(b => b.Length);
        Assert.Equal(expectedChars, doc.TotalCharLength);

        // Lookup block in the middle by char offset
        var midCharOffset = doc.BlockCharStart(500);
        var pos = doc.FindBlockByCharOffset(midCharOffset);
        Assert.Equal(500, pos.BlockIndex);
        Assert.Equal(0, pos.LocalOffset);

        // Lookup block by scroll position
        var midScrollY = doc.BlockTopY(500) + 1;
        var blockIdx = doc.FindBlockByScrollPosition(midScrollY);
        Assert.Equal(500, blockIdx);
    }
}
