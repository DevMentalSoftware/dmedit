namespace DevMentalMd.Core.Documents.History;

/// <summary>
/// Manages undo and redo stacks for a <see cref="PieceTable"/>.
/// Edits pushed here are applied immediately to the table and can be undone/redone.
/// </summary>
public sealed class EditHistory {
    private readonly Stack<IDocumentEdit> _undoStack = new();
    private readonly Stack<IDocumentEdit> _redoStack = new();

    // When non-null, we're collecting edits for a compound group
    private List<IDocumentEdit>? _compound;

    // -------------------------------------------------------------------------
    // Undo / Redo state
    // -------------------------------------------------------------------------

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    // -------------------------------------------------------------------------
    // Pushing edits
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies <paramref name="edit"/> to <paramref name="table"/> and records it for undo.
    /// Clears the redo stack (any redo history is lost after a new edit).
    /// </summary>
    public void Push(IDocumentEdit edit, PieceTable table) {
        edit.Apply(table);
        if (_compound != null) {
            _compound.Add(edit);
        } else {
            _undoStack.Push(edit);
            _redoStack.Clear();
        }
    }

    // -------------------------------------------------------------------------
    // Compound grouping
    // -------------------------------------------------------------------------

    /// <summary>Begins collecting subsequent <see cref="Push"/> calls into a single undo unit.</summary>
    public void BeginCompound() {
        _compound ??= new List<IDocumentEdit>();
    }

    /// <summary>Commits the current compound group as a single <see cref="CompoundEdit"/>.</summary>
    public void EndCompound() {
        if (_compound == null) {
            return;
        }
        var edits = _compound;
        _compound = null;
        if (edits.Count == 0) {
            return;
        }
        var group = new CompoundEdit(edits);
        _undoStack.Push(group);
        _redoStack.Clear();
    }

    // -------------------------------------------------------------------------
    // Undo / Redo operations
    // -------------------------------------------------------------------------

    /// <summary>Reverts the most recent edit. No-op if nothing to undo.</summary>
    public void Undo(PieceTable table) {
        if (!CanUndo) {
            return;
        }
        var edit = _undoStack.Pop();
        edit.Revert(table);
        _redoStack.Push(edit);
    }

    /// <summary>Re-applies the most recently undone edit. No-op if nothing to redo.</summary>
    public void Redo(PieceTable table) {
        if (!CanRedo) {
            return;
        }
        var edit = _redoStack.Pop();
        edit.Apply(table);
        _undoStack.Push(edit);
    }
}
