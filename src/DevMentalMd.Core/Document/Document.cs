using DevMentalMd.Core.Documents.History;

namespace DevMentalMd.Core.Documents;

/// <summary>
/// High-level document model: wraps a <see cref="PieceTable"/> with undo/redo history
/// and selection state. This is the primary type the editor UI interacts with.
/// </summary>
public sealed class Document {
    private readonly PieceTable _table;
    private readonly EditHistory _history = new();

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public Document(string initialContent = "") {
        _table = new PieceTable(initialContent);
    }

    /// <summary>
    /// Constructs a document wrapping an existing <see cref="PieceTable"/>.
    /// Used by <c>FileLoader</c> when loading from an <see cref="Buffers.IBuffer"/>.
    /// </summary>
    public Document(PieceTable table) {
        _table = table;
    }

    // -------------------------------------------------------------------------
    // State access
    // -------------------------------------------------------------------------

    public PieceTable Table => _table;
    public Selection Selection { get; set; } = Selection.Collapsed(0L);

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Raised after any mutation to the document content.</summary>
    public event EventHandler? Changed;

    // -------------------------------------------------------------------------
    // Edit operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Inserts <paramref name="text"/> at the current caret position.
    /// If there is a non-empty selection, the selected text is replaced.
    /// </summary>
    public void Insert(string text) {
        if (text.Length == 0) {
            return;
        }
        var ofs = Selection.Start;
        var replacing = !Selection.IsEmpty;
        if (replacing) {
            _history.BeginCompound();
            DeleteRange(ofs, Selection.Len);
        }
        _history.Push(new InsertEdit(ofs, text), _table, Selection);
        if (replacing) {
            _history.EndCompound();
        }
        Selection = Selection.Collapsed(ofs + text.Length);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes the current selection. No-op if selection is empty.</summary>
    public void DeleteSelection() {
        if (Selection.IsEmpty) {
            return;
        }
        DeleteRange(Selection.Start, Selection.Len);
        Selection = Selection.Collapsed(Selection.Start);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes the character before the caret (like the Backspace key).</summary>
    public void DeleteBackward() {
        if (!Selection.IsEmpty) {
            DeleteSelection();
            return;
        }
        var ofs = Selection.Caret;
        if (ofs == 0L) {
            return;
        }
        // Handle \r\n as a single unit
        var delLen = 1;
        if (ofs >= 2 && _table.GetText(ofs - 2, 2) == "\r\n") {
            delLen = 2;
        }
        var delOfs = ofs - delLen;
        var deleted = _table.GetText(delOfs, delLen);
        _history.Push(new DeleteEdit(delOfs, deleted), _table, Selection);
        Selection = Selection.Collapsed(delOfs);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes the character after the caret (like the Delete key).</summary>
    public void DeleteForward() {
        if (!Selection.IsEmpty) {
            DeleteSelection();
            return;
        }
        var ofs = Selection.Caret;
        if (ofs >= _table.Length) {
            return;
        }
        // Handle \r\n as a single unit
        var delLen = 1;
        if (ofs + 1 < _table.Length && _table.GetText(ofs, 2) == "\r\n") {
            delLen = 2;
        }
        var deleted = _table.GetText(ofs, delLen);
        _history.Push(new DeleteEdit(ofs, deleted), _table, Selection);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // -------------------------------------------------------------------------
    // Undo / Redo
    // -------------------------------------------------------------------------

    public void Undo() {
        var result = _history.Undo(_table);
        if (result is null) {
            return;
        }
        Selection = result.Value.SelectionBefore;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo() {
        var result = _history.Redo(_table);
        if (result is null) {
            return;
        }
        Selection = Selection.Collapsed(CaretAfterRedo(result.Value.Edit));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Computes the caret position after applying <paramref name="edit"/>.
    /// </summary>
    private static long CaretAfterRedo(IDocumentEdit edit) => edit switch {
        // Insert applied → caret at end of inserted text.
        InsertEdit ins => ins.Ofs + ins.Text.Length,
        // Delete applied → caret at deletion point.
        DeleteEdit del => del.Ofs,
        // Compound applies in forward order → last apply is the last edit.
        CompoundEdit comp => CaretAfterRedo(comp.Edits[^1]),
        _ => 0L
    };

    // -------------------------------------------------------------------------
    // Compound edit grouping (exposed for the editor to batch keystrokes)
    // -------------------------------------------------------------------------

    public void BeginCompound() => _history.BeginCompound();
    public void EndCompound() => _history.EndCompound();

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private void DeleteRange(long ofs, long len) {
        var deleted = _table.GetText(ofs, (int)len);
        _history.Push(new DeleteEdit(ofs, deleted), _table, Selection);
    }
}
