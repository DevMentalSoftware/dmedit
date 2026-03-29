using DevMentalMd.Core.Buffers;

namespace DevMentalMd.Core.Tests;

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
    public void Persistence_RoundTrip() {
        var buf = new ChunkedUtf8Buffer();
        buf.Append("hello ");
        buf.Append("world\u00E9");

        using var ms = new MemoryStream();
        buf.WriteTo(ms);
        ms.Position = 0;

        var restored = ChunkedUtf8Buffer.ReadFrom(ms);
        Assert.Equal(buf.CharLength, restored.CharLength);
        Assert.Equal(buf.ByteLength, restored.ByteLength);
        Assert.Equal(buf.GetSlice(0, (int)buf.CharLength),
                     restored.GetSlice(0, (int)restored.CharLength));
    }

    [Fact]
    public void Persistence_EmptyBuffer() {
        var buf = new ChunkedUtf8Buffer();
        using var ms = new MemoryStream();
        buf.WriteTo(ms);
        ms.Position = 0;

        var restored = ChunkedUtf8Buffer.ReadFrom(ms);
        Assert.Equal(0, restored.CharLength);
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
}
