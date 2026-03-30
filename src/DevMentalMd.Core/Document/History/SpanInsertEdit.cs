namespace DevMentalMd.Core.Documents.History;

/// <summary>
/// Records an insertion for undo/redo. Stores a buffer index and char offset
/// into one of the piece table's buffers rather than a managed string.
/// On redo, the buffer still contains the data so Apply just creates a
/// piece referencing the existing range — zero-copy redo.
/// </summary>
public sealed class SpanInsertEdit(long ofs, long addBufStart, int len, int bufIdx = -1) : IDocumentEdit {
    /// <summary>Logical document offset where the text was inserted.</summary>
    public long Ofs => ofs;

    /// <summary>Start offset within the buffer.</summary>
    public long AddBufStart => addBufStart;

    /// <summary>Number of characters inserted.</summary>
    public int Len => len;

    /// <summary>
    /// Buffer index. -1 means use the active add buffer (default for
    /// normal edits). Explicit index is used for session-restored edits
    /// that reference a paged buffer.
    /// </summary>
    public int BufIdx => bufIdx;

    public void Apply(PieceTable table) =>
        table.InsertFromBuffer(ofs, bufIdx < 0 ? table.AddBufferIndex : bufIdx,
            addBufStart, len);

    public void Revert(PieceTable table) =>
        table.Delete(ofs, len);
}
