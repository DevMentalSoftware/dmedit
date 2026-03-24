using DevMentalMd.Core.Buffers;

namespace DevMentalMd.Core.Tests;

/// <summary>
/// An <see cref="IBuffer"/> that generates content on-demand via a caller-supplied delegate,
/// without storing every character in memory.
/// </summary>
/// <remarks>
/// <para>
/// Designed for integration tests that need to simulate very large documents
/// (millions or billions of lines) without needing real files.
/// </para>
/// <para>
/// Internally maintains a stride-<see cref="Stride"/>-entry skip-list of character offsets
/// at which each sampled line begins. The skip-list is built lazily: only the entries needed
/// to satisfy a given access are computed. For the default stride of 1000 and 1 billion lines,
/// the fully-built skip-list occupies ≈ 8 MB.
/// </para>
/// </remarks>
public sealed class ProceduralBuffer : IBuffer {
    // -------------------------------------------------------------------------
    // Configuration / constants
    // -------------------------------------------------------------------------

    /// <summary>Default number of lines between skip-list samples.</summary>
    public const int DefaultStride = 1000;

    private readonly Func<long, string> _lineGenerator;
    private readonly long _lineCount;

    /// <summary>Number of lines between adjacent skip-list samples.</summary>
    public int Stride { get; }

    // -------------------------------------------------------------------------
    // Skip-list state
    // -------------------------------------------------------------------------

    // _samples[i] = character offset at which line (i * Stride) begins.
    // Allocated upfront, filled lazily up to _samplesBuilt * Stride lines.
    private readonly long[] _samples;
    private long _samplesBuilt; // number of sample slots filled (0 = none beyond index 0)

    private long? _length;  // cached total character count; null until first requested

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <param name="lineCount">Total number of logical lines the buffer will expose.</param>
    /// <param name="lineGenerator">
    /// Delegate that returns the text of line <paramref name="lineCount"/> (0-based index),
    /// <em>without</em> a trailing newline. The buffer appends "\n" between lines
    /// (no trailing newline after the last line).
    /// </param>
    /// <param name="stride">Skip-list sampling interval. Defaults to <see cref="DefaultStride"/>.</param>
    public ProceduralBuffer(long lineCount, Func<long, string> lineGenerator, int stride = DefaultStride) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        _lineCount = lineCount;
        _lineGenerator = lineGenerator;
        Stride = stride;

        var slotCount = (lineCount + stride - 1) / stride; // ceil division
        _samples = new long[slotCount];
        _samples[0] = 0L;
        _samplesBuilt = 1; // slot 0 is always known (offset 0)
    }

    // -------------------------------------------------------------------------
    // IBuffer
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public long LineCount => _lineCount;

    public int LongestLine => 10_000; // Not worth tracking for this dev mode feature

    /// <inheritdoc/>
    public bool LengthIsKnown => _length.HasValue;

    /// <inheritdoc/>
    public long Length {
        get {
            if (_length.HasValue) {
                return _length.Value;
            }
            // Compute total length by walking to the end.
            var ofs = GetLineStart(_lineCount - 1);
            ofs += _lineGenerator(_lineCount - 1).Length;
            _length = ofs;
            return ofs;
        }
    }

    /// <inheritdoc/>
    public long GetLineStart(long lineIdx) {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIdx);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lineIdx, _lineCount);

        // Ensure the skip-list is built up to the sample slot that covers lineIdx.
        var targetSlot = lineIdx / Stride;
        EnsureSamplesThrough(targetSlot);

        // Start from the nearest sample at or before lineIdx.
        var sampleLine = targetSlot * Stride;
        var ofs = _samples[targetSlot];

        // Walk forward line-by-line from sampleLine to lineIdx (at most Stride-1 steps).
        for (var i = sampleLine; i < lineIdx; i++) {
            ofs += _lineGenerator(i).Length + 1L; // +1 for '\n'
        }
        return ofs;
    }

    /// <inheritdoc/>
    public char this[long offset] {
        get {
            // Locate which line contains this offset.
            var (lineIdx, ofsInLine) = FindLineContaining(offset);
            var line = _lineGenerator(lineIdx);
            if (ofsInLine < line.Length) {
                return line[(int)ofsInLine];
            }
            // The character is the '\n' separator (ofsInLine == line.Length)
            return '\n';
        }
    }

    /// <inheritdoc/>
    public void CopyTo(long offset, Span<char> destination, int len) {
        var remaining = len;
        var destPos = 0;

        // Locate the starting line once, then walk forward sequentially — avoids
        // calling FindLineContaining (and the generator) on every line transition.
        var (lineIdx, ofsInLine) = FindLineContaining(offset);

        while (remaining > 0) {
            var line = _lineGenerator(lineIdx);
            var lineWithNewline = lineIdx < _lineCount - 1
                ? line.Length + 1  // +1 for '\n'
                : line.Length;
            var avail = (int)(lineWithNewline - ofsInLine);
            var take = Math.Min(avail, remaining);

            for (var i = 0; i < take; i++) {
                var posInLine = ofsInLine + i;
                destination[destPos + i] = posInLine < line.Length ? line[(int)posInLine] : '\n';
            }
            destPos += take;
            remaining -= take;

            if (remaining > 0) {
                // Advance to next line sequentially (no FindLineContaining needed).
                lineIdx++;
                ofsInLine = 0;
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose() { }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private void EnsureSamplesThrough(long targetSlot) {
        // _samplesBuilt is the number of slots already populated (1-based: slots 0.._samplesBuilt-1).
        // We need slots 0..targetSlot to be populated.
        if (targetSlot < _samplesBuilt) {
            return;
        }
        // Walk forward from the last built sample slot, filling in new ones.
        for (var slot = _samplesBuilt; slot <= targetSlot; slot++) {
            var prevLine = (slot - 1) * Stride;
            var prevOfs = _samples[slot - 1];
            var ofs = prevOfs;
            // Walk Stride lines from prevLine to compute the start of line (slot * Stride).
            var endLine = Math.Min(slot * Stride, _lineCount);
            for (var i = prevLine; i < endLine; i++) {
                ofs += _lineGenerator(i).Length + 1L;
            }
            _samples[slot] = ofs;
        }
        _samplesBuilt = targetSlot + 1;
    }

    /// <summary>
    /// Returns (lineIndex, offsetWithinLine) for the given absolute character offset.
    /// Uses binary search over built samples then linear walk within the stride window.
    /// </summary>
    private (long lineIdx, long ofsInLine) FindLineContaining(long offset) {
        // Binary search over built sample slots to find the largest slot whose offset <= offset.
        var lo = 0L;
        var hi = _samplesBuilt - 1;
        while (lo < hi) {
            var mid = (lo + hi + 1) / 2;
            EnsureSamplesThrough(mid);
            if (_samples[mid] <= offset) {
                lo = mid;
            } else {
                hi = mid - 1;
            }
        }

        // Walk forward from sample line lo*Stride.
        var lineIdx = lo * Stride;
        var curOfs = _samples[lo];

        while (lineIdx < _lineCount) {
            var lineLen = _lineGenerator(lineIdx).Length;
            var lineEnd = curOfs + lineLen; // exclusive; '\n' is at lineEnd
            if (offset <= lineEnd) {
                return (lineIdx, offset - curOfs);
            }
            // Move past the '\n'
            curOfs = lineEnd + 1;
            lineIdx++;
        }

        // Shouldn't reach here for valid offsets.
        throw new ArgumentOutOfRangeException(nameof(offset));
    }
}
