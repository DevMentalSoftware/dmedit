namespace DevMentalMd.Core.Documents.History;

/// <summary>An atomic, reversible mutation to a <see cref="PieceTable"/>.</summary>
public interface IDocumentEdit {
    void Apply(PieceTable table);
    void Revert(PieceTable table);
}
