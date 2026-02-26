namespace DevMentalMd.Core.Documents.History;

/// <summary>Records a deletion so it can be applied or reverted.</summary>
/// <param name="Ofs">Logical document offset where the deletion started.</param>
/// <param name="DeletedText">The text that was removed (needed to reconstruct on revert).</param>
public sealed record DeleteEdit(long Ofs, string DeletedText) : IDocumentEdit {
    public void Apply(PieceTable table) => table.Delete(Ofs, DeletedText.Length);
    public void Revert(PieceTable table) => table.Insert(Ofs, DeletedText);
}
