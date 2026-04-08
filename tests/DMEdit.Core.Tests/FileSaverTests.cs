using System.Text;
using DMEdit.Core.Documents;
using DMEdit.Core.IO;
using EncodingInfo = DMEdit.Core.Documents.EncodingInfo;

namespace DMEdit.Core.Tests;

/// <summary>
/// Tests for <see cref="FileSaver"/>'s defensive exception wrapping.
/// Round-trip and large-document behavior is covered by
/// <c>LargeDocumentTests.FileSaver_*</c>; this file focuses on the
/// encoding-failure context wrap added for TRIAGE Priority 1 #9.
/// </summary>
public class FileSaverTests {
    // -----------------------------------------------------------------
    // EncodeChunk — encoder fallback context wrapping
    //
    // The encodings FileSaver currently exposes never trigger
    // EncoderFallbackException (UTF-8/16 are total; ASCII / Windows-1252
    // use replacement fallback by default).  We test the wrap by calling
    // EncodeChunk directly with an Encoding explicitly configured for
    // exception fallback — that's the future scenario the wrap defends.
    // -----------------------------------------------------------------

    private static Encoding StrictAscii() =>
        Encoding.GetEncoding("us-ascii",
            new EncoderExceptionFallback(),
            new DecoderExceptionFallback());

    [Fact]
    public void EncodeChunk_FallbackException_WrapsWithFilePath() {
        var enc = StrictAscii();
        var encoder = enc.GetEncoder();
        // 'é' (U+00E9) is not representable in ASCII.
        var chars = new char[] { 'a', 'b', 'é', 'd' };
        var byteBuf = new byte[16];

        var ex = Assert.Throws<IOException>(() => FileSaver.EncodeChunk(
            encoder, chars, chars.Length, byteBuf, flush: true,
            path: @"C:\some\path\notes.txt", enc: enc, chunkStart: 0));

        Assert.Contains(@"C:\some\path\notes.txt", ex.Message);
    }

    [Fact]
    public void EncodeChunk_FallbackException_WrapsWithEncodingName() {
        var enc = StrictAscii();
        var encoder = enc.GetEncoder();
        var chars = new char[] { 'a', 'é' };
        var byteBuf = new byte[16];

        var ex = Assert.Throws<IOException>(() => FileSaver.EncodeChunk(
            encoder, chars, chars.Length, byteBuf, flush: true,
            path: "x.txt", enc: enc, chunkStart: 0));

        Assert.Contains(enc.WebName, ex.Message);
        // Should also nudge the user toward Unicode.
        Assert.Contains("UTF-8", ex.Message);
    }

    [Fact]
    public void EncodeChunk_FallbackException_IncludesOffendingCodepoint() {
        var enc = StrictAscii();
        var encoder = enc.GetEncoder();
        // Use 'Ω' (U+03A9, Greek Capital Letter Omega).
        var chars = new char[] { 'a', 'b', 'Ω' };
        var byteBuf = new byte[16];

        var ex = Assert.Throws<IOException>(() => FileSaver.EncodeChunk(
            encoder, chars, chars.Length, byteBuf, flush: true,
            path: "x.txt", enc: enc, chunkStart: 0));

        Assert.Contains("U+03A9", ex.Message);
    }

    [Fact]
    public void EncodeChunk_FallbackException_PreservesInnerException() {
        var enc = StrictAscii();
        var encoder = enc.GetEncoder();
        var chars = new char[] { 'é' };
        var byteBuf = new byte[16];

        var ex = Assert.Throws<IOException>(() => FileSaver.EncodeChunk(
            encoder, chars, chars.Length, byteBuf, flush: true,
            path: "x.txt", enc: enc, chunkStart: 0));

        // The original EncoderFallbackException is kept as InnerException
        // so debugging tools can still see it.
        Assert.IsType<EncoderFallbackException>(ex.InnerException);
    }

    [Fact]
    public void EncodeChunk_FallbackException_PositionIncludesChunkStart() {
        // Approximate position = chunkStart + ex.Index.  When chunkStart
        // is non-zero (we're in the middle of a multi-chunk save), the
        // reported position should reflect the overall document offset.
        var enc = StrictAscii();
        var encoder = enc.GetEncoder();
        var chars = new char[] { 'é' };
        var byteBuf = new byte[16];

        var ex = Assert.Throws<IOException>(() => FileSaver.EncodeChunk(
            encoder, chars, chars.Length, byteBuf, flush: true,
            path: "x.txt", enc: enc, chunkStart: 1_000_000));

        // Position should be near 1,000,000 (chunkStart + 0).
        Assert.Contains("1000000", ex.Message);
    }

    [Fact]
    public void EncodeChunk_PureAscii_DoesNotThrow() {
        var enc = StrictAscii();
        var encoder = enc.GetEncoder();
        var chars = "hello world".ToCharArray();
        var byteBuf = new byte[16];

        var byteCount = FileSaver.EncodeChunk(
            encoder, chars, chars.Length, byteBuf, flush: true,
            path: "x.txt", enc: enc, chunkStart: 0);

        Assert.Equal(11, byteCount);
        Assert.Equal("hello world", Encoding.ASCII.GetString(byteBuf, 0, byteCount));
    }

    // -----------------------------------------------------------------
    // End-to-end via Save: with current encodings (replacement fallback),
    // unrepresentable characters silently become '?'.  This pins that
    // existing behavior so we know any future change to strict fallback
    // would surface here.
    // -----------------------------------------------------------------

    [Fact]
    public void Save_AsAscii_SilentlyReplacesNonAscii() {
        var path = Path.Combine(Path.GetTempPath(), $"dmedit_fs_{Guid.NewGuid():N}.txt");
        try {
            var doc = new Document("héllo");
            doc.EncodingInfo = new EncodingInfo(FileEncoding.Ascii);
            FileSaver.Save(doc, path);

            // Default ASCII fallback substitutes '?' for non-ASCII.
            // (When/if we switch to strict fallback in the future, this
            // assertion will fail and the EncodeChunk wrap will catch.)
            var written = File.ReadAllText(path, Encoding.ASCII);
            Assert.Equal("h?llo", written);
        } finally {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
