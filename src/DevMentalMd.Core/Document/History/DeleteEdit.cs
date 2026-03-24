namespace DevMentalMd.Core.Documents.History;

/// <summary>Records a deletion so it can be applied or reverted.</summary>
/// <remarks>
/// Stores lightweight <see cref="Piece"/> descriptors and line tree deltas
/// instead of a string.  Undo re-inserts the pieces directly into the piece
/// list and restores the line tree — no buffer I/O, no string allocation,
/// regardless of the size of the deleted range.
/// </remarks>
public sealed class DeleteEdit : IDocumentEdit {
    private Piece[] _pieces;
    private int _lineInfoStart;
    private int[]? _lineInfoLengths;
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
    /// Session-restore constructor: explicit length with optional text.
    /// When <paramref name="deletedText"/> is shorter than <paramref name="len"/>
    /// (e.g. empty for oversized deletes), Apply still deletes the correct range
    /// but Revert cannot fully restore the content.
    /// </summary>
    public DeleteEdit(long ofs, long len, string deletedText) {
        Ofs = ofs;
        Len = len;
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

    public void Apply(PieceTable table) {
        // If this edit was deserialized without piece descriptors (oversized
        // delete whose text was omitted from the session file), capture them
        // now while the table is in the correct pre-delete state.  This
        // enables Revert (undo) to restore the deleted content.
        if (_pieces.Length == 0 && Len > 0 && (_text is null or { Length: 0 })) {
            _pieces = table.CapturePieces(Ofs, Len);
            var lineInfo = table.CaptureLineInfo(Ofs, Len);
            if (lineInfo is { } info) {
                _lineInfoStart = info.StartLine;
                _lineInfoLengths = info.LineLengths;
            }
        }
        table.Delete(Ofs, Len);
    }

    public void Revert(PieceTable table) {
        if (_pieces.Length > 0) {
            // Re-insert saved pieces and restore line tree atomically so
            // no observer can see an inconsistent state (pieces restored
            // but line tree still reflecting the deleted range).
            table.InsertPiecesAndRestoreLines(
                Ofs, _pieces, _lineInfoStart, _lineInfoLengths, Len);
        } else {
            // Small delete with pre-materialized text — use string Insert.
            table.Insert(Ofs, _text!);
        }
    }
}
