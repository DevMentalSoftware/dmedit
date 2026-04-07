using DMEdit.Core.Buffers;

namespace DMEdit.Core.Tests;

public class ChunkedUtf8BufferTests {
    [Fact]
    public void Empty_HasZeroLength() {
        var buf = new ChunkedUtf8Buffer();
        Assert.Equal(0, buf.CharLength);
        Assert.Equal(0, buf.ByteLength);
    }

    [Fact]
    public void Append_ReturnsStartOffset() {
        var buf = new ChunkedUtf8Buffer();
        Assert.Equal(0, buf.Append("abc"));
        Assert.Equal(3, buf.Append("de"));
        Assert.Equal(5, buf.CharLength);
    }

    [Fact]
    public void CharAt_Ascii() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("hello");
        Assert.Equal('h', buf.CharAt(0));
        Assert.Equal('o', buf.CharAt(4));
    }

    [Fact]
    public void CharAt_Multibyte() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("a\u00E9b"); // a + e-acute (2 bytes UTF-8) + b
        Assert.Equal('a', buf.CharAt(0));
        Assert.Equal('\u00E9', buf.CharAt(1));
        Assert.Equal('b', buf.CharAt(2));
        Assert.Equal(3, buf.CharLength);
    }

    [Fact]
    public void CharAt_Emoji_SurrogatePair() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("a\U0001F600b"); // a + grinning face (4 bytes UTF-8, 2 UTF-16 code units) + b
        Assert.Equal('a', buf.CharAt(0));
        // Surrogate pair: chars 1 and 2
        Assert.Equal('\uD83D', buf.CharAt(1)); // high surrogate
        Assert.Equal('b', buf.CharAt(3));
        Assert.Equal(4, buf.CharLength); // a + 2 surrogates + b
    }

    [Fact]
    public void CopyTo_BasicAscii() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("hello world");
        var dest = new char[5];
        buf.CopyTo(6, 5, dest);
        Assert.Equal("world", new string(dest));
    }

    [Fact]
    public void CopyTo_Multibyte() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("\u00E9\u00E8\u00EA"); // 3 accented chars
        var dest = new char[2];
        buf.CopyTo(1, 2, dest);
        Assert.Equal("\u00E8\u00EA", new string(dest));
    }

    [Fact]
    public void GetSlice_ReturnsSubstring() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("abcdefgh");
        Assert.Equal("cde", buf.GetSlice(2, 3));
    }

    [Fact]
    public void GetSlice_Empty() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("abc");
        Assert.Equal("", buf.GetSlice(1, 0));
    }

    [Fact]
    public void Visit_CollectsAllChars() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("hello");
        var sb = new System.Text.StringBuilder();
        buf.Visit(0, 5, span => sb.Append(span));
        Assert.Equal("hello", sb.ToString());
    }

    [Fact]
    public void Visit_SubRange() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("hello world");
        var sb = new System.Text.StringBuilder();
        buf.Visit(3, 5, span => sb.Append(span));
        Assert.Equal("lo wo", sb.ToString());
    }

    [Fact]
    public void IndexOfAny_FindsNewlines() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("abc\ndef");
        Assert.Equal(3, buf.IndexOfAny(0, 7, '\n', '\r'));
    }

    [Fact]
    public void IndexOfAny_NotFound() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("abcdef");
        Assert.Equal(-1, buf.IndexOfAny(0, 6, '\n', '\r'));
    }

    [Fact]
    public void IndexOfAny_SubRange() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("ab\ncd\nef");
        // Search from char 3, length 4 → "cd\ne"
        Assert.Equal(2, buf.IndexOfAny(3, 4, '\n', '\r'));
    }

    [Fact]
    public void TrimToCharLength_Basic() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("hello world");
        buf.TrimToCharLength(5);
        Assert.Equal(5, buf.CharLength);
        Assert.Equal("hello", buf.GetSlice(0, 5));
    }

    [Fact]
    public void TrimToCharLength_ToZero() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("abc");
        buf.TrimToCharLength(0);
        Assert.Equal(0, buf.CharLength);
        Assert.Equal(0, buf.ByteLength);
    }

    [Fact]
    public void TrimToCharLength_NoOp() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("abc");
        buf.TrimToCharLength(100);
        Assert.Equal(3, buf.CharLength);
    }

    [Fact]
    public void AppendUtf8_DirectBytes() {
        var buf = new ChunkedUtf8Buffer();
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
        buf.AppendUtf8(bytes);
        Assert.Equal(5, buf.CharLength);
        Assert.Equal('h', buf.CharAt(0));
        Assert.Equal("hello", buf.GetSlice(0, 5));
    }

    [Fact]
    public void AppendUtf8_Multibyte() {
        var buf = new ChunkedUtf8Buffer();
        var bytes = System.Text.Encoding.UTF8.GetBytes("\u00E9"); // 2 bytes
        buf.AppendUtf8(bytes);
        Assert.Equal(1, buf.CharLength);
        Assert.Equal(2, buf.ByteLength);
        Assert.Equal('\u00E9', buf.CharAt(0));
    }

    [Fact]
    public void MultipleAppends_Coalesce() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("ab");
        buf.Append("cd");
        buf.Append("ef");
        Assert.Equal(6, buf.CharLength);
        Assert.Equal("abcdef", buf.GetSlice(0, 6));
        Assert.Equal('d', buf.CharAt(3));
    }

    [Fact]
    public void WriteTo_ProducesRawUtf8() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("hello ");
        buf.Append("world\u00E9");

        using var ms = new MemoryStream();
        buf.WriteTo(ms);

        // Verify the output is raw UTF-8 (no headers).
        var bytes = ms.ToArray();
        var decoded = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Equal("hello world\u00E9", decoded);
        Assert.Equal(buf.ByteLength, bytes.Length);
    }

    [Fact]
    public void WriteTo_EmptyBuffer() {
        var buf = new ChunkedUtf8Buffer();
        using var ms = new MemoryStream();
        buf.WriteTo(ms);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void LargeAppend_CreatesOwnChunk() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("small");
        // Append something larger than the initial chunk (64KB)
        var big = new string('x', 100_000);
        buf.Append(big);
        buf.Append("tail");

        Assert.Equal(5 + 100_000 + 4, buf.CharLength);
        Assert.Equal('s', buf.CharAt(0));
        Assert.Equal('x', buf.CharAt(5));
        Assert.Equal('x', buf.CharAt(100_004));
        Assert.Equal('t', buf.CharAt(100_005));
        Assert.Equal("tail", buf.GetSlice(100_005, 4));
    }

    [Fact]
    public void TrimToCharLength_AcrossChunks() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("ab");
        var big = new string('y', 100_000);
        buf.Append(big);
        buf.Append("cd");

        // Trim to just past the first chunk
        buf.TrimToCharLength(5);
        Assert.Equal(5, buf.CharLength);
        Assert.Equal("ab", buf.GetSlice(0, 2));
    }

    [Fact]
    public void CJK_Characters() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("\u4F60\u597D\u4E16\u754C"); // "Hello World" in Chinese
        Assert.Equal(4, buf.CharLength);
        Assert.Equal('\u4F60', buf.CharAt(0));
        Assert.Equal('\u754C', buf.CharAt(3));
        Assert.Equal("\u597D\u4E16", buf.GetSlice(1, 2));
    }

    // -------------------------------------------------------------------------
    // ASCII fast path coverage (entry 21).
    //
    // ChunkedUtf8Buffer has two char-offset → byte-offset code paths:
    //   - ASCII chunk: byte index == char index (immediate return)
    //   - Non-ASCII chunk: walk UTF-8 bytes from start of chunk
    // The dividing line is per-chunk, not per-offset.  These tests verify
    // both paths produce identical results across reads, copies, slices,
    // and searches, and that the IsAllAscii flag transitions correctly
    // across appends and trims.
    // -------------------------------------------------------------------------

    [Fact]
    public void Ascii_LongChunk_RandomReadsAreCorrect() {
        // A long all-ASCII chunk exercises FindByteOffsetInChunk's
        // fast-return path repeatedly.  Build a recognizable pattern so
        // we can verify any offset.
        var buf = new ChunkedUtf8Buffer();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 10_000; i++) {
            sb.Append((char)('a' + (i % 26)));
        }
        var text = sb.ToString();
        buf.Append(text);

        Assert.Equal(10_000, buf.CharLength);
        Assert.Equal(10_000, buf.ByteLength); // ASCII = 1 byte per char

        // Spot-check several positions, including ones the cursor cache
        // would not naturally help with (jumping forward and backward).
        Assert.Equal(text[0], buf.CharAt(0));
        Assert.Equal(text[9999], buf.CharAt(9999));
        Assert.Equal(text[5000], buf.CharAt(5000));
        Assert.Equal(text[1234], buf.CharAt(1234));
        Assert.Equal(text[8765], buf.CharAt(8765));
        Assert.Equal(text[42], buf.CharAt(42));
        Assert.Equal(text[9998], buf.CharAt(9998));
    }

    [Fact]
    public void Ascii_TabsCRLFAreFastPathed() {
        // Tab (0x09), CR (0x0D), LF (0x0A) are all < 0x80 and must NOT
        // taint a chunk's IsAllAscii flag.  Source code is full of these.
        var buf = new ChunkedUtf8Buffer();
        buf.Append("\tline1\r\n\tline2\r\n");
        Assert.Equal(16, buf.ByteLength); // every char is 1 byte
        Assert.Equal('\t', buf.CharAt(0));
        Assert.Equal('l', buf.CharAt(1));
        Assert.Equal('\r', buf.CharAt(6));
        Assert.Equal('\n', buf.CharAt(7));
        Assert.Equal('\t', buf.CharAt(8));
    }

    [Fact]
    public void NonAscii_LongChunk_RandomReadsAreCorrect() {
        // The non-ASCII slow path walks UTF-8 from chunk start.  Build
        // a long mixed chunk with deterministic content so any offset
        // can be verified.  Each cycle is "abc\u00E9" — three ASCII +
        // one 2-byte char = 4 chars / 5 bytes.
        var buf = new ChunkedUtf8Buffer();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 1_000; i++) {
            sb.Append("abc\u00E9");
        }
        var text = sb.ToString();
        buf.Append(text);

        Assert.Equal(4_000, buf.CharLength);
        Assert.Equal(5_000, buf.ByteLength);

        // Spot-check positions across the chunk, including jumps that
        // defeat the cursor cache.
        Assert.Equal('a', buf.CharAt(0));
        Assert.Equal('\u00E9', buf.CharAt(3));
        Assert.Equal('a', buf.CharAt(4));
        Assert.Equal('\u00E9', buf.CharAt(3999));
        Assert.Equal('a', buf.CharAt(2000));
        Assert.Equal('\u00E9', buf.CharAt(1999));
        Assert.Equal('b', buf.CharAt(2001));
        Assert.Equal('c', buf.CharAt(2002));
        Assert.Equal('\u00E9', buf.CharAt(2003));
    }

    [Fact]
    public void NonAscii_SurrogatePair_AcrossPositions() {
        // 4-byte UTF-8 sequences encode supplementary-plane chars and
        // map to a UTF-16 surrogate pair (2 chars).  Verify both halves
        // can be read at any offset.
        var buf = new ChunkedUtf8Buffer();
        // "x\U0001F600y\U0001F600z" — x + emoji + y + emoji + z
        buf.Append("x\U0001F600y\U0001F600z");
        // Char layout: x=0, emoji-hi=1, emoji-lo=2, y=3, emoji-hi=4, emoji-lo=5, z=6
        Assert.Equal(7, buf.CharLength);
        Assert.Equal('x', buf.CharAt(0));
        Assert.Equal('\uD83D', buf.CharAt(1)); // first emoji high surrogate
        Assert.Equal('y', buf.CharAt(3));
        Assert.Equal('\uD83D', buf.CharAt(4)); // second emoji high surrogate
        Assert.Equal('z', buf.CharAt(6));
    }

    [Fact]
    public void Mixed_AsciiThenNonAscii_BothPathsCorrect() {
        // A chunk that starts all-ASCII and gains non-ASCII mid-append
        // permanently moves to the slow path.  Verify reads at the
        // ASCII prefix still produce correct results after the
        // transition.
        var buf = new ChunkedUtf8Buffer();
        buf.Append("hello"); // chunk is all-ASCII at this point
        Assert.Equal('h', buf.CharAt(0));
        Assert.Equal('o', buf.CharAt(4));

        buf.Append(" \u00E9 world"); // appends a multi-byte char
        // Chunk is now non-ASCII; CharAt must walk UTF-8.
        Assert.Equal('h', buf.CharAt(0)); // ASCII prefix still readable
        Assert.Equal('o', buf.CharAt(4));
        Assert.Equal(' ', buf.CharAt(5));
        Assert.Equal('\u00E9', buf.CharAt(6));
        Assert.Equal(' ', buf.CharAt(7));
        Assert.Equal('w', buf.CharAt(8));
        Assert.Equal('d', buf.CharAt(12));
        Assert.Equal(13, buf.CharLength);
    }

    [Fact]
    public void Mixed_NonAsciiThenAscii_StaysOnSlowPath() {
        // Once a chunk is tainted with non-ASCII, appending more ASCII
        // does NOT re-enable the fast path.  This is by design — the
        // flag is monotonic, and the existing non-ASCII bytes still
        // require the walk.
        var buf = new ChunkedUtf8Buffer();
        buf.Append("\u00E9");
        buf.Append("hello"); // ASCII tail does not re-flag the chunk
        Assert.Equal(6, buf.CharLength);
        Assert.Equal('\u00E9', buf.CharAt(0));
        Assert.Equal('h', buf.CharAt(1));
        Assert.Equal('o', buf.CharAt(5));
    }

    [Fact]
    public void AppendUtf8_TracksAsciiFlagCorrectly() {
        // The raw-bytes path also has to track IsAllAscii.
        var buf = new ChunkedUtf8Buffer();
        var asciiBytes = System.Text.Encoding.UTF8.GetBytes("hello");
        buf.AppendUtf8(asciiBytes);
        // Should be on fast path; verify reads work.
        Assert.Equal('h', buf.CharAt(0));
        Assert.Equal('o', buf.CharAt(4));

        var multiByte = System.Text.Encoding.UTF8.GetBytes("\u00E9");
        buf.AppendUtf8(multiByte);
        Assert.Equal(6, buf.CharLength);
        Assert.Equal('h', buf.CharAt(0));
        Assert.Equal('\u00E9', buf.CharAt(5));
    }

    [Fact]
    public void Trim_NonAsciiChunkBackIntoAsciiPrefix_KeepsSlowPath() {
        // Trimming the non-ASCII tail off a chunk leaves only ASCII
        // bytes in the chunk, but we deliberately do NOT re-set
        // IsAllAscii to true (to do so safely would require a full
        // rescan).  Verify that trim still produces correct results.
        var buf = new ChunkedUtf8Buffer();
        buf.Append("hello\u00E9world"); // chunk now non-ASCII
        Assert.Equal(11, buf.CharLength);
        buf.TrimToCharLength(5);        // trim back to "hello" (all ASCII)
        Assert.Equal(5, buf.CharLength);
        Assert.Equal(5, buf.ByteLength);
        Assert.Equal('h', buf.CharAt(0));
        Assert.Equal('o', buf.CharAt(4));
        Assert.Equal("hello", buf.GetSlice(0, 5));
    }

    [Fact]
    public void CopyTo_NonAsciiChunk_CorrectAtAnyOffset() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("a\u00E9b\u00E9c\u00E9d");
        // Layout: a=0, e1=1, b=2, e2=3, c=4, e3=5, d=6
        var dest = new char[3];
        buf.CopyTo(2, 3, dest);
        Assert.Equal("b\u00E9c", new string(dest));
        buf.CopyTo(4, 3, dest);
        Assert.Equal("c\u00E9d", new string(dest));
    }

    [Fact]
    public void IndexOfAny_NonAsciiChunk_FindsAsciiTarget() {
        // Newline scanning over non-ASCII content should still work —
        // the IndexOfAnyAsciiInBytes path scans bytes directly so
        // multi-byte chars must be skipped over correctly.
        var buf = new ChunkedUtf8Buffer();
        buf.Append("\u00E9\u00E9\u00E9\nabc"); // 3 e-acute + LF + 3 ASCII
        // Char layout: e=0, e=1, e=2, \n=3, a=4, b=5, c=6
        Assert.Equal(3, buf.IndexOfAny(0, 7, '\n', '\r'));
    }

    [Fact]
    public void GetSlice_AcrossManyChunks_NonAscii() {
        // Force multiple chunks by appending more than InitialChunkSize
        // (64 KB) of non-ASCII content, then read across chunk boundaries.
        var buf = new ChunkedUtf8Buffer();
        var unit = "\u00E9abc"; // 4 chars / 5 bytes
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 20_000; i++) sb.Append(unit);
        buf.Append(sb.ToString());

        Assert.Equal(80_000, buf.CharLength);
        Assert.Equal(100_000, buf.ByteLength);
        // The first char of every unit is e-acute.
        Assert.Equal('\u00E9', buf.CharAt(0));
        Assert.Equal('a', buf.CharAt(1));
        Assert.Equal('\u00E9', buf.CharAt(40_000));
        Assert.Equal('a', buf.CharAt(40_001));
        Assert.Equal('\u00E9', buf.CharAt(79_996));
        Assert.Equal('c', buf.CharAt(79_999));
    }

    // -------------------------------------------------------------------------
    // Forward-progress regression tests for the decode loop.
    //
    // These pin the fix for a hang reported when the user deletes one half
    // of a surrogate pair (or otherwise asks for an odd char count that
    // straddles a 4-byte UTF-8 / surrogate-pair sequence).  Before the fix,
    // DecodeUpToNChars returned (decoded=0, bytesConsumed=0), and the outer
    // loop in CopyTo / Visit / IndexOfAny made no progress and spun forever.
    // The fix is to emit a U+FFFD replacement and skip the offending bytes
    // both inside DecodeUpToNChars and as a defensive safety net in each
    // outer loop.
    // -------------------------------------------------------------------------

    [Fact]
    public void CopyTo_RequestingOddCharCountInsideSurrogatePair_DoesNotHang() {
        // "x😀y" in UTF-16 is x + high + low + y = 4 chars / 6 UTF-8 bytes.
        // Asking for chars [0..1] would normally land between x and the
        // emoji's high surrogate; we want to verify that asking for chars
        // [0..2] (which lands between high and low surrogate) doesn't hang.
        var buf = new ChunkedUtf8Buffer();
        buf.Append("x\U0001F600y");
        Assert.Equal(4, buf.CharLength);

        // CharLen=2 lands inside the surrogate pair.  Must terminate.
        var dest = new char[2];
        buf.CopyTo(0, 2, dest);
        // First char is 'x'; second falls inside the pair, so the safety
        // net writes U+FFFD.  The exact replacement isn't the contract;
        // termination is.  Just check that CharAt of the surviving chars
        // is well-defined.
        Assert.Equal('x', dest[0]);
        Assert.True(dest[1] == '\uFFFD' || char.IsHighSurrogate(dest[1]));
    }

    [Fact]
    public void CopyTo_OrphanLowSurrogate_DoesNotHang() {
        // Construct an orphan low surrogate by writing the raw 3-byte
        // tail of a 4-byte UTF-8 sequence directly via AppendUtf8 — but
        // raw byte appends would corrupt the char/byte counts.  Instead,
        // simulate the original failure mode: append a full emoji, then
        // ask CopyTo for char counts that span and don't span the pair.
        var buf = new ChunkedUtf8Buffer();
        buf.Append("a\U0001F600b\U0001F600c");
        // Layout: a=0, hi=1, lo=2, b=3, hi=4, lo=5, c=6 → 7 chars total.
        Assert.Equal(7, buf.CharLength);

        // Sweep all valid (start, len) combinations.  None should hang
        // and all should produce a string of the requested length.
        for (var start = 0; start <= 7; start++) {
            for (var len = 0; len + start <= 7; len++) {
                var dest = new char[len];
                buf.CopyTo(start, len, dest);
                // Just touch every cell so any uninitialized read would
                // also surface.
                for (var i = 0; i < len; i++) { _ = dest[i]; }
            }
        }
    }

    [Fact]
    public void GetSlice_OddLengthAcrossSurrogate_Terminates() {
        // GetSlice goes through CopyTo, so the same safety net applies.
        var buf = new ChunkedUtf8Buffer();
        buf.Append("\U0001F600\U0001F600\U0001F600"); // 3 emojis = 6 chars
        Assert.Equal(6, buf.CharLength);

        var slice = buf.GetSlice(0, 3); // odd length, lands mid-pair
        Assert.Equal(3, slice.Length);
        // First char is the high surrogate of the first emoji; the rest
        // is implementation-defined replacement vs partial decode, but
        // it must not hang and must be exactly 3 chars long.
    }

    [Fact]
    public void Visit_OddLengthAcrossSurrogate_Terminates() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("\U0001F600x");  // emoji + ASCII
        var totalChars = 0;
        buf.Visit(0, buf.CharLength, span => totalChars += span.Length);
        Assert.Equal(buf.CharLength, totalChars);

        // Same test but asking for an odd char count straddling the pair.
        totalChars = 0;
        buf.Visit(0, 1, span => totalChars += span.Length);
        Assert.Equal(1, totalChars);
    }

    [Fact]
    public void IndexOfAny_NonAsciiTargetsAcrossSurrogate_Terminates() {
        // Non-ASCII targets force the slow path inside IndexOfAny.
        // Make sure it doesn't hang on a surrogate-pair-bearing chunk.
        var buf = new ChunkedUtf8Buffer();
        buf.Append("\U0001F600\u00E9\U0001F600");
        // Looking for U+00E9 — should find it at char index 2 (after the
        // first emoji's surrogate pair).
        var idx = buf.IndexOfAny(0, (int)buf.CharLength, '\u00E9', '\u00EA');
        Assert.Equal(2, idx);
    }

    [Fact]
    public void GetSlice_AcrossManyChunks_AsciiOnly() {
        // Same idea but with ASCII content so every chunk is fast-pathed.
        var buf = new ChunkedUtf8Buffer();
        var unit = "abcd";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 25_000; i++) sb.Append(unit);
        buf.Append(sb.ToString());

        Assert.Equal(100_000, buf.CharLength);
        Assert.Equal(100_000, buf.ByteLength);
        Assert.Equal('a', buf.CharAt(0));
        Assert.Equal('d', buf.CharAt(99_999));
        Assert.Equal('a', buf.CharAt(50_000));
        Assert.Equal("abcd", buf.GetSlice(50_000, 4));
        Assert.Equal("abcd", buf.GetSlice(99_996, 4));
    }
}
