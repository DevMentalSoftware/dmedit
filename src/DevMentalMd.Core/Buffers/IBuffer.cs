namespace DevMentalMd.Core.Buffers;

/// <summary>
/// Abstraction over a read-only character store of arbitrary size.
/// Implementations include <see cref="StringBuffer"/> (in-memory string),
/// <see cref="ProceduralBuffer"/> (generated content), and
/// <see cref="LazyFileBuffer"/> (memory-mapped file).
/// </summary>
public interface IBuffer : IDisposable {
    /// <summary>Total number of characters, if known.</summary>
    long Length { get; }

    /// <summary>
    /// Returns the character at <paramref name="offset"/>.
    /// </summary>
    char this[long offset] { get; }

    /// <summary>
    /// Copies <paramref name="len"/> characters starting at <paramref name="offset"/>
    /// into <paramref name="destination"/>.
    /// </summary>
    void CopyTo(long offset, Span<char> destination, int len);

    // -------------------------------------------------------------------------
    // Optional line-index hints (return -1 / false when not available)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the character offset at which logical line <paramref name="lineIdx"/> begins,
    /// or <c>-1</c> if the buffer does not maintain a line index.
    /// </summary>
    long GetLineStart(long lineIdx) => -1L;

    /// <summary>
    /// Total number of logical lines, or <c>-1</c> if not known without a full scan.
    /// </summary>
    long LineCount => -1L;

    /// <summary>
    /// When <c>false</c>, <see cref="PieceTable"/> skips the upper-bound guard
    /// on <c>Insert</c>/<c>Delete</c> (because computing <see cref="Length"/> would
    /// require a full scan).
    /// </summary>
    bool LengthIsKnown => true;
}
