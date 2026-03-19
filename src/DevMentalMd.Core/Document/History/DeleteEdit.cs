namespace DevMentalMd.Core.Documents.History;

/// <summary>Records a deletion so it can be applied or reverted.</summary>
/// <remarks>
/// Stores lightweight <see cref="Piece"/> descriptors and line tree deltas
/// instead of a string.  Undo re-inserts the pieces directly into the piece
/// list and restores the line tree — no buffer I/O, no string allocation,
/// regardless of the size of the deleted range.
/// </remarks>
public sealed class DeleteEdit : IDocumentEdit {
    private readonly Piece[] _pieces;
    private readonly int _lineInfoStart;
    private readonly int[]? _lineInfoLengths;
    private string? _text; // only set for small deletes or session deserialization

    public long Ofs { get; }
    public long Len { get; }

    /// <summary>Full constructor: pieces + line tree delta for zero-copy undo.</summary>
    public DeleteEdit(long ofs, long len, Piece[] pieces,
                      int lineInfoStart, int[]? lineInfoLengths) {
        Ofs = ofs;
        Len = len;
        _pieces = pieces;
        _lineInfoStart = lineInfoStart;
        _lineInfoLengths = lineInfoLengths;
    }

    /// <summary>Pieces-only constructor (no line tree delta — single-line deletes).</summary>
    public DeleteEdit(long ofs, long len, Piece[] pieces)
        : this(ofs, len, pieces, -1, null) { }

    /// <summary>Convenience constructor for small deletes where the text is already known.</summary>
    public DeleteEdit(long ofs, string deletedText) {
        Ofs = ofs;
        Len = deletedText.Length;
        _pieces = [];
        _text = deletedText;
        _lineInfoStart = -1;
    }

    /// <summary>
    /// The deleted text, or null if not yet materialized.
    /// </summary>
    public string? DeletedText => _text;

    /// <summary>
    /// Materializes the deleted text by reading from the buffers.
    /// Used by session serialization.
    /// </summary>
    public string MaterializeText(PieceTable table) => _text ??= table.ReadPieces(_pieces);

    public void Apply(PieceTable table) => table.Delete(Ofs, Len);

    public void Revert(PieceTable table) {
        if (_pieces.Length > 0) {
            // Re-insert saved pieces directly — no string allocation.
            table.InsertPieces(Ofs, _pieces);
            if (_lineInfoLengths != null) {
                table.RestoreLines(_lineInfoStart, _lineInfoLengths);
            } else {
                // Single-line delete: the line just got shorter; update length.
                table.ReinsertedNonNewlineChars(Ofs, Len);
            }
        } else {
            // Small delete with pre-materialized text — use string Insert.
            table.Insert(Ofs, _text!);
        }
    }
}
