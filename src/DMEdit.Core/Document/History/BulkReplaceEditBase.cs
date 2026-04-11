namespace DMEdit.Core.Documents.History;

/// <summary>
/// Base class for bulk replace edits. Stores the pre-edit piece list, line
/// tree, and add-buffer length so <see cref="Revert"/> can restore the
/// document state in O(1).
/// </summary>
public abstract class BulkReplaceEditBase : IDocumentEdit {
    private readonly Piece[] _savedPieces;
    private readonly int[] _savedLineLengths;
    private readonly long _savedAddBufLen;

    protected BulkReplaceEditBase(
        Piece[] savedPieces, int[] savedLineLengths, long savedAddBufLen) {
        _savedPieces = savedPieces;
        _savedLineLengths = savedLineLengths;
        _savedAddBufLen = savedAddBufLen;
    }

    public abstract void Apply(PieceTable table);

    public void Revert(PieceTable table) {
        table.TrimAddBuffer(_savedAddBufLen);
        table.RestorePieces(_savedPieces);
        table.InstallLineTree(_savedLineLengths);
    }
}
