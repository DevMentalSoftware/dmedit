namespace DevMentalMd.Core.Documents.History;

/// <summary>
/// Records an insertion for undo/redo. Stores the offset and length into the
/// piece table's append-only add buffer rather than a managed string.
/// On redo, the add buffer still contains the data so Apply just creates a
/// piece referencing the existing range — zero-copy redo.
/// </summary>
public sealed class SpanInsertEdit(long ofs, long addBufStart, int len) : IDocumentEdit {
    /// <summary>Logical document offset where the text was inserted.</summary>
    public long Ofs => ofs;

    /// <summary>Start offset within the add buffer.</summary>
    public long AddBufStart => addBufStart;

    /// <summary>Number of characters inserted.</summary>
    public int Len => len;

    public void Apply(PieceTable table) =>
        table.InsertFromAddBuffer(ofs, addBufStart, len);

    public void Revert(PieceTable table) =>
        table.Delete(ofs, len);
}
