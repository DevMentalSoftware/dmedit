namespace DevMentalMd.Core.Documents.History;

/// <summary>
/// Manages undo and redo stacks for a <see cref="PieceTable"/>.
/// Edits pushed here are applied immediately to the table and can be undone/redone.
/// Each entry pairs an edit with the <see cref="Selection"/> that existed before the
/// edit was applied, so undo can restore the exact caret/selection state.
/// </summary>
public sealed class EditHistory {
    private readonly record struct Entry(IDocumentEdit Edit, Selection SelectionBefore);

    private readonly Stack<Entry> _undoStack = new();
    private readonly Stack<Entry> _redoStack = new();

    // When non-null, we're collecting edits for a compound group
    private List<IDocumentEdit>? _compound;
    private Selection? _compoundSelBefore;

    // -------------------------------------------------------------------------
    // Undo / Redo state
    // -------------------------------------------------------------------------

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    // Save-point: the undo-stack depth at the last save.
    // When the current depth equals this value, the document is clean.
    private int _savePointDepth;

    /// <summary>
    /// Marks the current history position as "saved". After this call,
    /// <see cref="IsAtSavePoint"/> returns true until the document is
    /// further edited (or undone past the save point).
    /// </summary>
    public void MarkSavePoint() => _savePointDepth = _undoStack.Count;

    /// <summary>
    /// True when the current undo-stack depth matches the last saved position.
    /// </summary>
    public bool IsAtSavePoint => _undoStack.Count == _savePointDepth;

    // -------------------------------------------------------------------------
    // Pushing edits
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies <paramref name="edit"/> to <paramref name="table"/> and records it for undo.
    /// <paramref name="selBefore"/> is the selection state before this edit, used to
    /// restore the caret on undo. Clears the redo stack.
    /// </summary>
    public void Push(IDocumentEdit edit, PieceTable table, Selection selBefore) {
        edit.Apply(table);
        if (_compound != null) {
            _compound.Add(edit);
            _compoundSelBefore ??= selBefore; // capture the first push's selection
        } else {
            _undoStack.Push(new Entry(edit, selBefore));
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
        var sel = _compoundSelBefore ?? Selection.Collapsed(0);
        _compound = null;
        _compoundSelBefore = null;
        if (edits.Count == 0) {
            return;
        }
        var group = new CompoundEdit(edits);
        _undoStack.Push(new Entry(group, sel));
        _redoStack.Clear();
    }

    // -------------------------------------------------------------------------
    // Undo / Redo operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Result of an undo or redo operation: the edit that was applied/reverted,
    /// and the selection that existed before the original edit was applied.
    /// </summary>
    public readonly record struct UndoRedoResult(IDocumentEdit Edit, Selection SelectionBefore);

    /// <summary>
    /// Reverts the most recent edit. Returns the edit and pre-edit selection,
    /// or <c>null</c> if nothing to undo.
    /// </summary>
    public UndoRedoResult? Undo(PieceTable table) {
        if (!CanUndo) {
            return null;
        }
        var entry = _undoStack.Pop();
        entry.Edit.Revert(table);
        _redoStack.Push(entry);
        return new UndoRedoResult(entry.Edit, entry.SelectionBefore);
    }

    /// <summary>
    /// Re-applies the most recently undone edit. Returns the edit and pre-edit selection,
    /// or <c>null</c> if nothing to redo.
    /// </summary>
    public UndoRedoResult? Redo(PieceTable table) {
        if (!CanRedo) {
            return null;
        }
        var entry = _redoStack.Pop();
        entry.Edit.Apply(table);
        _undoStack.Push(entry);
        return new UndoRedoResult(entry.Edit, entry.SelectionBefore);
    }
}
