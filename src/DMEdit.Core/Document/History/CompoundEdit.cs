namespace DMEdit.Core.Documents.History;

/// <summary>
/// A group of edits treated as a single undo/redo unit.
/// Applied in forward order; reverted in reverse order.
/// </summary>
public sealed class CompoundEdit : IDocumentEdit {
    private readonly IReadOnlyList<IDocumentEdit> _edits;

    public CompoundEdit(IReadOnlyList<IDocumentEdit> edits) {
        _edits = edits;
    }

    /// <summary>The constituent edits, in forward (apply) order.</summary>
    public IReadOnlyList<IDocumentEdit> Edits => _edits;

    public void Apply(PieceTable table) {
        foreach (var e in _edits) {
            e.Apply(table);
        }
    }

    public void Revert(PieceTable table) {
        for (var i = _edits.Count - 1; i >= 0; i--) {
            _edits[i].Revert(table);
        }
    }
}
