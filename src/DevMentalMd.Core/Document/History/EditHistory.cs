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

    // When non-null, we're collecting edits for a compound group.
    // _compoundDepth supports nesting: inner Begin/End pairs are no-ops.
    private List<IDocumentEdit>? _compound;
    private Selection? _compoundSelBefore;
    private int _compoundDepth;

    // -------------------------------------------------------------------------
    // Undo / Redo state
    // -------------------------------------------------------------------------

    public bool CanUndo => _undoStack.Count > 0 || _compound?.Count > 0;
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
    /// True when the current undo-stack depth matches the last saved position
    /// and there are no uncommitted edits in a pending compound group.
    /// </summary>
    public bool IsAtSavePoint =>
        _undoStack.Count == _savePointDepth
        && (_compound is null || _compound.Count == 0);

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

    /// <summary>
    /// Begins collecting subsequent <see cref="Push"/> calls into a single undo unit.
    /// Calls may be nested; only the outermost <see cref="EndCompound"/> commits.
    /// </summary>
    public void BeginCompound() {
        _compoundDepth++;
        _compound ??= new List<IDocumentEdit>();
    }

    /// <summary>
    /// Ends the current compound level. Only the outermost call commits the
    /// collected edits as a single <see cref="CompoundEdit"/>.
    /// </summary>
    public void EndCompound() {
        if (_compound == null) {
            return;
        }
        _compoundDepth--;
        if (_compoundDepth > 0) {
            return; // still inside an outer compound
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

    /// <summary>A single entry in the undo or redo stack, exposed for serialization.</summary>
    public readonly record struct HistoryEntry(IDocumentEdit Edit, Selection SelectionBefore);

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

    // -----------------------------------------------------------------
    // Serialization support
    // -----------------------------------------------------------------

    /// <summary>The undo-stack depth at the last save point.</summary>
    public int SavePointDepth => _savePointDepth;

    /// <summary>
    /// Returns the undo stack entries bottom-to-top (oldest first).
    /// </summary>
    public IReadOnlyList<HistoryEntry> GetUndoEntries() {
        var arr = _undoStack.ToArray();  // top-to-bottom
        Array.Reverse(arr);
        return Array.ConvertAll(arr, e => new HistoryEntry(e.Edit, e.SelectionBefore));
    }

    /// <summary>
    /// Returns the redo stack entries bottom-to-top (oldest first).
    /// </summary>
    public IReadOnlyList<HistoryEntry> GetRedoEntries() {
        var arr = _redoStack.ToArray();  // top-to-bottom
        Array.Reverse(arr);
        return Array.ConvertAll(arr, e => new HistoryEntry(e.Edit, e.SelectionBefore));
    }

    /// <summary>
    /// Replays serialized entries into the undo and redo stacks, then applies
    /// the undo entries to the <paramref name="table"/> so the document reaches
    /// its edited state. Entries are bottom-to-top (oldest first).
    /// </summary>
    public void RestoreEntries(
        PieceTable table,
        IReadOnlyList<HistoryEntry> undoEntries,
        IReadOnlyList<HistoryEntry> redoEntries,
        int savePointDepth) {

        _undoStack.Clear();
        _redoStack.Clear();

        // Apply undo entries in order (oldest first) to build up to the edited state.
        // DeleteEdits that were serialized without text (oversized deletes)
        // recapture their piece descriptors inside Apply, at the exact moment
        // the table is in the correct pre-delete state.
        foreach (var entry in undoEntries) {
            entry.Edit.Apply(table);
            _undoStack.Push(new Entry(entry.Edit, entry.SelectionBefore));
        }

        // Push redo entries without applying (they are future edits).
        // Redo stack is LIFO, so push oldest first → newest ends up on top.
        foreach (var entry in redoEntries) {
            _redoStack.Push(new Entry(entry.Edit, entry.SelectionBefore));
        }

        _savePointDepth = savePointDepth;
    }
}
