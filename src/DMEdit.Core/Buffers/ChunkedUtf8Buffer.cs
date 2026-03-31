using System.Buffers;
using System.Text;

namespace DMEdit.Core.Buffers;

/// <summary>
/// Append-only buffer that stores text as UTF-8 bytes in a chain of
/// variable-size chunks. Exposes a char-offset-based API so callers
/// (PieceTable) never deal with byte positions.
///
/// Growth strategy:
///   - First chunk: <see cref="InitialChunkSize"/> bytes.
///   - Subsequent chunks from normal typing: <see cref="GrowthChunkSize"/> bytes.
///   - Large pastes that exceed the remaining space: one chunk sized to fit.
///
/// All offsets in the public API are in characters (UTF-16 code units),
/// matching the rest of the document model.
/// </summary>
public sealed class ChunkedUtf8Buffer : IBuffer {

    private const int InitialChunkSize = 64 * 1024;
    private const int GrowthChunkSize = InitialChunkSize;

    private struct Chunk {
        public byte[] Data;
        public int BytesUsed;
        public int CharCount;
    }

    private readonly List<Chunk> _chunks = new();
    private long _totalChars;
    private long _totalBytes;

    // Sequential-access cursor: avoids repeated binary search when callers
    // scan forward (VisitPieces, BuildLineTree, etc.).
    private int _cursorChunk;
    private long _cursorCharOfs;  // first char offset of _cursorChunk
    private long _cursorByteOfs;  // first byte offset of _cursorChunk

    // -------------------------------------------------------------------------
    // Properties
    // -------------------------------------------------------------------------

    /// <summary>Total characters (UTF-16 code units) stored.</summary>
    public long CharLength => _totalChars;

    /// <summary>Total UTF-8 bytes stored.</summary>
    public long ByteLength => _totalBytes;

    // IBuffer implementation
    long IBuffer.Length => _totalChars;
    char IBuffer.this[long offset] => CharAt(offset);
    void IBuffer.CopyTo(long offset, Span<char> destination, int len) =>
        CopyTo(offset, len, destination);
    public void Dispose() { } // nothing to dispose

    // -------------------------------------------------------------------------
    // Append
    // -------------------------------------------------------------------------

    /// <summary>
    /// Appends UTF-16 text, encoding it to UTF-8. Returns the char start
    /// offset (position of the first appended char in the logical stream).
    /// </summary>
    public long Append(ReadOnlySpan<char> text) {
        if (text.Length == 0) {
            return _totalChars;
        }

        var charStart = _totalChars;
        var remaining = text;

        while (remaining.Length > 0) {
            // Ensure we have a chunk with space.
            if (CurrentChunkRemaining() <= 0) {
                var minSize = _chunks.Count == 0 ? InitialChunkSize : GrowthChunkSize;
                // Size to fit the remaining text (exact byte count) or growth size.
                var needed = Encoding.UTF8.GetByteCount(remaining);
                AllocateChunk(Math.Max(minSize, needed));
            }

            ref var chunk = ref CurrentChunkRef();
            var space = chunk.Data.Length - chunk.BytesUsed;
            var dest = chunk.Data.AsSpan(chunk.BytesUsed, space);

            // Encode as many chars as fit into the available space.
            var encoder = Encoding.UTF8.GetEncoder();
            encoder.Convert(remaining, dest, flush: true,
                out var charsUsed, out var bytesUsed, out _);

            chunk.BytesUsed += bytesUsed;
            chunk.CharCount += charsUsed;
            _totalChars += charsUsed;
            _totalBytes += bytesUsed;
            remaining = remaining[charsUsed..];
        }

        return charStart;
    }

    /// <summary>
    /// Appends raw UTF-8 bytes directly (e.g. from Linux clipboard).
    /// Returns the char start offset.
    /// </summary>
    public long AppendUtf8(ReadOnlySpan<byte> utf8) {
        if (utf8.Length == 0) {
            return _totalChars;
        }

        var charStart = _totalChars;
        var remaining = utf8;

        while (remaining.Length > 0) {
            if (CurrentChunkRemaining() <= 0) {
                var minSize = _chunks.Count == 0 ? InitialChunkSize : GrowthChunkSize;
                AllocateChunk(Math.Max(minSize, remaining.Length));
            }

            ref var chunk = ref CurrentChunkRef();
            var space = chunk.Data.Length - chunk.BytesUsed;
            var take = Math.Min(remaining.Length, space);

            // Don't split a multi-byte UTF-8 sequence across chunks.
            if (take < remaining.Length) {
                while (take > 0 && (remaining[take] & 0xC0) == 0x80) {
                    take--;
                }
                if (take == 0) {
                    // No room for even one complete sequence — need a new chunk.
                    AllocateChunk(Math.Max(GrowthChunkSize, remaining.Length));
                    chunk = ref CurrentChunkRef();
                    space = chunk.Data.Length - chunk.BytesUsed;
                    take = Math.Min(remaining.Length, space);
                }
            }

            var slice = remaining[..take];
            var charCount = Encoding.UTF8.GetCharCount(slice);
            slice.CopyTo(chunk.Data.AsSpan(chunk.BytesUsed));
            chunk.BytesUsed += take;
            chunk.CharCount += charCount;
            _totalChars += charCount;
            _totalBytes += take;
            remaining = remaining[take..];
        }

        return charStart;
    }

    // -------------------------------------------------------------------------
    // Read — char-offset-based API
    // -------------------------------------------------------------------------

    /// <summary>Returns the character at <paramref name="charOffset"/>.</summary>
    public char CharAt(long charOffset) {
        var (ci, byteOfs) = FindByteOffset(charOffset);
        ref var chunk = ref ChunkRef(ci);

        // Determine how many bytes this UTF-8 sequence uses.
        var b = chunk.Data[byteOfs];
        int seqLen;
        if (b < 0x80) return (char)b; // fast path for ASCII
        else if ((b & 0xE0) == 0xC0) seqLen = 2;
        else if ((b & 0xF0) == 0xE0) seqLen = 3;
        else seqLen = 4;

        var span = chunk.Data.AsSpan(byteOfs, Math.Min(seqLen, chunk.BytesUsed - byteOfs));
        Span<char> result = stackalloc char[2];
        Encoding.UTF8.GetChars(span, result);
        return result[0];
    }

    /// <summary>
    /// Copies <paramref name="charLen"/> characters starting at
    /// <paramref name="charStart"/> into <paramref name="dest"/>.
    /// </summary>
    public void CopyTo(long charStart, int charLen, Span<char> dest) {
        if (charLen == 0) return;

        var (ci, byteOfs) = FindByteOffset(charStart);
        var destPos = 0;
        var charsRemaining = charLen;

        while (charsRemaining > 0 && ci < _chunks.Count) {
            ref var chunk = ref ChunkRef(ci);
            var bytesAvail = chunk.BytesUsed - byteOfs;
            var src = chunk.Data.AsSpan(byteOfs, bytesAvail);

            // Decode as many chars as we need from this chunk.
            var decoded = DecodeUpToNChars(src, dest.Slice(destPos), charsRemaining,
                out var bytesConsumed);
            destPos += decoded;
            charsRemaining -= decoded;
            byteOfs += bytesConsumed;

            if (byteOfs >= chunk.BytesUsed) {
                ci++;
                byteOfs = 0;
            }
        }
    }

    /// <summary>
    /// Visits character spans covering [<paramref name="charStart"/>,
    /// <paramref name="charStart"/> + <paramref name="charLen"/>).
    /// Each callback receives a decoded <see cref="ReadOnlySpan{Char}"/>.
    /// </summary>
    public void Visit(long charStart, long charLen, Action<ReadOnlySpan<char>> visitor) {
        if (charLen == 0) return;

        var (ci, byteOfs) = FindByteOffset(charStart);
        var charsRemaining = charLen;
        // Reusable decode buffer — 1 MB of chars.
        var decodeBuf = ArrayPool<char>.Shared.Rent(1024 * 1024);
        try {
            while (charsRemaining > 0 && ci < _chunks.Count) {
                ref var chunk = ref ChunkRef(ci);
                var bytesAvail = chunk.BytesUsed - byteOfs;
                var src = chunk.Data.AsSpan(byteOfs, bytesAvail);

                var want = (int)Math.Min(charsRemaining, decodeBuf.Length);
                var decoded = DecodeUpToNChars(src, decodeBuf, want,
                    out var bytesConsumed);

                if (decoded > 0) {
                    visitor(decodeBuf.AsSpan(0, decoded));
                    charsRemaining -= decoded;
                }

                byteOfs += bytesConsumed;
                if (byteOfs >= chunk.BytesUsed) {
                    ci++;
                    byteOfs = 0;
                }
            }
        } finally {
            ArrayPool<char>.Shared.Return(decodeBuf);
        }
    }

    /// <summary>
    /// Returns a substring from [<paramref name="charStart"/>,
    /// <paramref name="charStart"/> + <paramref name="charLen"/>).
    /// Allocates a string.
    /// </summary>
    public string GetSlice(long charStart, int charLen) {
        if (charLen == 0) return string.Empty;
        var buf = new char[charLen];
        CopyTo(charStart, charLen, buf);
        return new string(buf);
    }

    /// <summary>
    /// Searches for <paramref name="c1"/> or <paramref name="c2"/> in the
    /// char range [<paramref name="charStart"/>, <paramref name="charStart"/>
    /// + <paramref name="charLen"/>). Returns the char-offset relative to
    /// <paramref name="charStart"/>, or -1 if not found.
    /// </summary>
    public int IndexOfAny(long charStart, int charLen, char c1, char c2) {
        // For newline scanning, both targets are ASCII (0x0A, 0x0D)
        // so we can scan bytes directly for a fast path.
        if (c1 < 0x80 && c2 < 0x80) {
            return IndexOfAnyAsciiInBytes(charStart, charLen, (byte)c1, (byte)c2);
        }

        // General path: decode and scan.
        var pos = 0;
        var decodeBuf = ArrayPool<char>.Shared.Rent(Math.Min(charLen, 1024 * 1024));
        try {
            var (ci, byteOfs) = FindByteOffset(charStart);
            var charsRemaining = charLen;
            while (charsRemaining > 0 && ci < _chunks.Count) {
                ref var chunk = ref ChunkRef(ci);
                var src = chunk.Data.AsSpan(byteOfs, chunk.BytesUsed - byteOfs);
                var want = Math.Min(charsRemaining, decodeBuf.Length);
                var decoded = DecodeUpToNChars(src, decodeBuf, want, out var bytesConsumed);
                var span = decodeBuf.AsSpan(0, decoded);
                var idx = span.IndexOfAny(c1, c2);
                if (idx >= 0) return pos + idx;
                pos += decoded;
                charsRemaining -= decoded;
                byteOfs += bytesConsumed;
                if (byteOfs >= chunk.BytesUsed) { ci++; byteOfs = 0; }
            }
        } finally {
            ArrayPool<char>.Shared.Return(decodeBuf);
        }
        return -1;
    }

    // -------------------------------------------------------------------------
    // Trim (for bulk-replace undo)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Truncates the buffer to <paramref name="newCharLen"/> characters.
    /// Discards all data beyond that point.
    /// </summary>
    public void TrimToCharLength(long newCharLen) {
        if (newCharLen >= _totalChars) return;
        if (newCharLen == 0) {
            _chunks.Clear();
            _totalChars = 0;
            _totalBytes = 0;
            ResetCursor();
            return;
        }

        // Walk chunks to find which one contains the cut point.
        long cumChars = 0;
        for (var i = 0; i < _chunks.Count; i++) {
            var chunk = _chunks[i];
            if (cumChars + chunk.CharCount >= newCharLen) {
                // Cut point is in this chunk.
                var charsToKeep = (int)(newCharLen - cumChars);
                var bytePos = FindByteOffsetInChunk(ref chunk, charsToKeep);
                chunk.BytesUsed = bytePos;
                chunk.CharCount = charsToKeep;
                _chunks[i] = chunk;

                // Remove all subsequent chunks.
                if (i + 1 < _chunks.Count) {
                    _chunks.RemoveRange(i + 1, _chunks.Count - i - 1);
                }

                // Recompute totals.
                _totalChars = newCharLen;
                _totalBytes = 0;
                for (var j = 0; j <= i; j++) {
                    _totalBytes += _chunks[j].BytesUsed;
                }
                ResetCursor();
                return;
            }
            cumChars += chunk.CharCount;
        }
    }

    // -------------------------------------------------------------------------
    // Persistence
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes all UTF-8 bytes as raw data to <paramref name="stream"/>.
    /// The resulting file is a plain UTF-8 text file that can be loaded
    /// by <see cref="PagedFileBuffer"/> for paged reading.
    /// </summary>
    public void WriteTo(Stream stream) {
        foreach (var chunk in _chunks) {
            stream.Write(chunk.Data, 0, chunk.BytesUsed);
        }
    }

    // -------------------------------------------------------------------------
    // Internal: chunk management
    // -------------------------------------------------------------------------

    private int CurrentChunkRemaining() {
        if (_chunks.Count == 0) return 0;
        var last = _chunks[^1];
        return last.Data.Length - last.BytesUsed;
    }

    private void AllocateChunk(int size) {
        _chunks.Add(new Chunk {
            Data = new byte[size],
            BytesUsed = 0,
            CharCount = 0,
        });
    }

    private ref Chunk CurrentChunkRef() =>
        ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_chunks)[^1];

    private ref Chunk ChunkRef(int index) =>
        ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_chunks)[index];

    // -------------------------------------------------------------------------
    // Internal: char → byte offset translation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Finds the chunk index and byte offset within that chunk for
    /// the given global char offset. Uses the cursor cache for
    /// sequential access patterns.
    /// </summary>
    private (int chunkIndex, int byteOffset) FindByteOffset(long charOffset) {
        if (_chunks.Count == 0 || charOffset < 0) {
            throw new ArgumentOutOfRangeException(nameof(charOffset));
        }

        // Check if cursor is still valid and can fast-path.
        if (_cursorChunk < _chunks.Count) {
            var cChunk = _chunks[_cursorChunk];
            if (charOffset >= _cursorCharOfs &&
                charOffset < _cursorCharOfs + cChunk.CharCount) {
                // Within the cached chunk.
                var localChars = (int)(charOffset - _cursorCharOfs);
                var byteOfs = FindByteOffsetInChunk(ref cChunk, localChars);
                return (_cursorChunk, byteOfs);
            }
            // Check if we can advance the cursor forward (common sequential case).
            if (charOffset >= _cursorCharOfs + cChunk.CharCount) {
                var cumChars = _cursorCharOfs + cChunk.CharCount;
                var cumBytes = _cursorByteOfs + cChunk.BytesUsed;
                for (var i = _cursorChunk + 1; i < _chunks.Count; i++) {
                    var ch = _chunks[i];
                    if (charOffset < cumChars + ch.CharCount) {
                        _cursorChunk = i;
                        _cursorCharOfs = cumChars;
                        _cursorByteOfs = cumBytes;
                        var localChars = (int)(charOffset - cumChars);
                        var byteOfs = FindByteOffsetInChunk(ref ch, localChars);
                        return (i, byteOfs);
                    }
                    cumChars += ch.CharCount;
                    cumBytes += ch.BytesUsed;
                }
            }
        }

        // Full scan from start (backward seek or cursor invalidated).
        long chars = 0;
        long bytes = 0;
        for (var i = 0; i < _chunks.Count; i++) {
            var ch = _chunks[i];
            if (charOffset < chars + ch.CharCount) {
                _cursorChunk = i;
                _cursorCharOfs = chars;
                _cursorByteOfs = bytes;
                var localChars = (int)(charOffset - chars);
                var byteOfs = FindByteOffsetInChunk(ref ch, localChars);
                return (i, byteOfs);
            }
            chars += ch.CharCount;
            bytes += ch.BytesUsed;
        }

        throw new ArgumentOutOfRangeException(nameof(charOffset),
            $"Char offset {charOffset} beyond buffer length {_totalChars}");
    }

    /// <summary>
    /// Within a single chunk, finds the byte offset of the Nth character
    /// by scanning UTF-8 sequences. O(chunk bytes) worst case but fast
    /// for ASCII-heavy content.
    /// </summary>
    private static int FindByteOffsetInChunk(ref Chunk chunk, int charIndex) {
        if (charIndex == 0) return 0;
        if (charIndex == chunk.CharCount) return chunk.BytesUsed;

        var span = chunk.Data.AsSpan(0, chunk.BytesUsed);
        var bytePos = 0;
        var charCount = 0;

        while (charCount < charIndex && bytePos < span.Length) {
            var b = span[bytePos];
            int seqLen;
            int charsProduced;
            if (b < 0x80) {
                seqLen = 1;
                charsProduced = 1;
            } else if ((b & 0xE0) == 0xC0) {
                seqLen = 2;
                charsProduced = 1;
            } else if ((b & 0xF0) == 0xE0) {
                seqLen = 3;
                charsProduced = 1;
            } else {
                // 4-byte sequence → surrogate pair (2 UTF-16 code units)
                seqLen = 4;
                charsProduced = 2;
            }
            charCount += charsProduced;
            bytePos += seqLen;
        }
        return bytePos;
    }

    /// <summary>
    /// Scans UTF-8 bytes for ASCII targets without decoding. Returns the
    /// char-offset (relative to the start of the search) of the first
    /// match, or -1. Tracks char count while scanning.
    /// </summary>
    private int IndexOfAnyAsciiInBytes(long charStart, int charLen, byte b1, byte b2) {
        var (ci, byteOfs) = FindByteOffset(charStart);
        var charsScanned = 0;

        while (charsScanned < charLen && ci < _chunks.Count) {
            ref var chunk = ref ChunkRef(ci);
            var span = chunk.Data.AsSpan(byteOfs, chunk.BytesUsed - byteOfs);
            var pos = 0;

            while (pos < span.Length && charsScanned < charLen) {
                var b = span[pos];
                if (b == b1 || b == b2) {
                    return charsScanned;
                }
                int seqLen;
                int charsProduced;
                if (b < 0x80) {
                    seqLen = 1;
                    charsProduced = 1;
                } else if ((b & 0xE0) == 0xC0) {
                    seqLen = 2;
                    charsProduced = 1;
                } else if ((b & 0xF0) == 0xE0) {
                    seqLen = 3;
                    charsProduced = 1;
                } else {
                    seqLen = 4;
                    charsProduced = 2;
                }
                pos += seqLen;
                charsScanned += charsProduced;
            }

            ci++;
            byteOfs = 0;
        }
        return -1;
    }

    /// <summary>
    /// Decodes up to <paramref name="maxChars"/> from <paramref name="src"/>
    /// into <paramref name="dest"/>. Returns chars decoded and bytes consumed.
    /// Stops at UTF-8 sequence boundaries so we never split a multi-byte char.
    /// </summary>
    private static int DecodeUpToNChars(ReadOnlySpan<byte> src, Span<char> dest,
        int maxChars, out int bytesConsumed) {
        // Find byte boundary for maxChars.
        var byteLimit = 0;
        var charCount = 0;
        while (byteLimit < src.Length && charCount < maxChars) {
            var b = src[byteLimit];
            int seqLen;
            int charsProduced;
            if (b < 0x80) {
                seqLen = 1;
                charsProduced = 1;
            } else if ((b & 0xE0) == 0xC0) {
                seqLen = 2;
                charsProduced = 1;
            } else if ((b & 0xF0) == 0xE0) {
                seqLen = 3;
                charsProduced = 1;
            } else {
                seqLen = 4;
                charsProduced = 2;
            }

            // Don't overshoot maxChars (e.g. surrogate pair producing 2 chars).
            if (charCount + charsProduced > maxChars) break;
            // Don't read past end of src.
            if (byteLimit + seqLen > src.Length) break;

            byteLimit += seqLen;
            charCount += charsProduced;
        }

        bytesConsumed = byteLimit;
        if (byteLimit == 0) return 0;

        return Encoding.UTF8.GetChars(src[..byteLimit], dest);
    }

    private void ResetCursor() {
        _cursorChunk = 0;
        _cursorCharOfs = 0;
        _cursorByteOfs = 0;
    }
}
