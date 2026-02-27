using System;

namespace DevMentalMd.Core.Blocks;

/// <summary>
/// An inline formatting span within a block's text. Represents a contiguous
/// range of characters that have a specific formatting applied.
///
/// Spans are non-overlapping for a given <see cref="InlineSpanType"/> but
/// different types may overlap (e.g., bold and italic on the same range).
/// </summary>
/// <param name="Type">The formatting type.</param>
/// <param name="Start">0-based character offset within the block's text.</param>
/// <param name="Length">Number of characters covered by this span.</param>
/// <param name="Url">For <see cref="InlineSpanType.Link"/> spans, the target URL. Null otherwise.</param>
public record InlineSpan(InlineSpanType Type, int Start, int Length, string? Url = null) {
    /// <summary>Exclusive end offset (Start + Length).</summary>
    public int End => Start + Length;

    /// <summary>
    /// Returns a new span shifted by <paramref name="delta"/> characters.
    /// Used when text is inserted or deleted before this span.
    /// </summary>
    public InlineSpan Shift(int delta) => this with { Start = Start + delta };

    /// <summary>
    /// Returns a new span with its length adjusted by <paramref name="delta"/>.
    /// Used when text is inserted or deleted within this span.
    /// </summary>
    public InlineSpan Resize(int delta) => this with { Length = Math.Max(0, Length + delta) };

    /// <summary>
    /// Returns true if this span overlaps the character range [start, start+length).
    /// </summary>
    public bool Overlaps(int start, int length) {
        return Start < start + length && start < End;
    }

    /// <summary>
    /// Returns true if this span fully contains the character range [start, start+length).
    /// </summary>
    public bool Contains(int start, int length) {
        return Start <= start && start + length <= End;
    }
}
