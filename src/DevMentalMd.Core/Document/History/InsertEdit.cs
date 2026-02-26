namespace DevMentalMd.Core.Documents.History;

/// <summary>Records an insertion so it can be applied or reverted.</summary>
/// <param name="Ofs">Logical document offset where the text was inserted.</param>
/// <param name="Text">The inserted text.</param>
public sealed record InsertEdit(long Ofs, string Text) : IDocumentEdit {
    public void Apply(PieceTable table) => table.Insert(Ofs, Text);
    public void Revert(PieceTable table) => table.Delete(Ofs, Text.Length);
}
