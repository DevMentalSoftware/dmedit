namespace DevMentalMd.Core.Documents;

/// <summary>
/// A single span within a piece-table buffer.
/// Immutable value type; all mutations produce new pieces.
/// </summary>
/// <param name="Which">Which buffer this piece references.</param>
/// <param name="Start">Zero-based character offset into that buffer.</param>
/// <param name="Len">Number of characters in this piece.</param>
public readonly record struct Piece(BufferKind Which, int Start, int Len) {
    /// <summary>True when this piece contains no characters.</summary>
    public bool IsEmpty => Len == 0;

    /// <summary>Returns a piece that covers only the first <paramref name="n"/> characters.</summary>
    public Piece TakeFirst(int n) => this with { Len = n };

    /// <summary>Returns a piece that skips the first <paramref name="n"/> characters.</summary>
    public Piece SkipFirst(int n) => this with { Start = Start + n, Len = Len - n };
}
