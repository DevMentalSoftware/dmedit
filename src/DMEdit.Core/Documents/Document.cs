using System.Text;
using DMEdit.Core.Documents.History;
using DMEdit.Core.Printing;

namespace DMEdit.Core.Documents;

/// <summary>
/// High-level document model: wraps a <see cref="PieceTable"/> with undo/redo history
/// and selection state. This is the primary type the editor UI interacts with.
/// </summary>
public sealed partial class Document {
    private readonly PieceTable _table;
    private readonly EditHistory _history = new();

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <summary>Creates an empty document (for untitled/new documents).</summary>
    public Document() {
        _table = new PieceTable();
    }

    /// <summary>
    /// Creates a document from a string.  Used by tests; production code
    /// uses the parameterless constructor or <see cref="Document(PieceTable)"/>.
    /// </summary>
    internal Document(string initialContent) {
        _table = new PieceTable(initialContent);
        if (initialContent.Length > 0) {
            LineEndingInfo = LineEndingInfo.Detect(initialContent);
        }
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
    public EditHistory History => _history;
    public Selection Selection { get; set; } = Selection.Collapsed(0L);

    /// <summary>
    /// When non-null, the editor is in column (block) selection mode.
    /// The rectangular selection governs editing; <see cref="Selection"/>
    /// remains set but is secondary.
    /// </summary>
    public ColumnSelection? ColumnSel { get; set; }

    /// <summary>
    /// The detected (or assigned) line ending style for this document.
    /// Defaults to the platform default for new documents.
    /// </summary>
    public LineEndingInfo LineEndingInfo { get; set; } = LineEndingInfo.PlatformDefault;

    /// <summary>
    /// Detected indentation style. Set during file loading.
    /// Defaults to spaces for new documents.
    /// </summary>
    public IndentInfo IndentInfo { get; set; } = IndentInfo.Default;

    /// <summary>
    /// Page layout settings used for printing and PDF export.
    /// Persisted per-document so the user's last-used paper size,
    /// orientation, and margins are remembered across print invocations.
    /// </summary>
    public PrintSettings PrintSettings { get; set; } = new();

    /// <summary>
    /// Detected (or user-assigned) file encoding. Determines how the document
    /// is written on the next save. Defaults to UTF-8 (no BOM) for new documents.
    /// </summary>
    public EncodingInfo EncodingInfo { get; set; } = EncodingInfo.Default;

    /// <summary>
    /// True while the backing buffer is still streaming from disk.
    /// </summary>
    public bool IsLoading => _table.Buffer is { LengthIsKnown: false };

    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;

    /// <summary>
    /// Maximum selection size (in characters) that the Avalonia clipboard fallback
    /// will materialize. The native clipboard service has no limit.
    /// </summary>
    public const int MaxCopyLength = 1024 * 1024; // 1 M chars ≈ 2 MB

    /// <summary>
    /// Returns the currently selected text, or an empty string if selection is empty.
    /// Returns <c>null</c> when the selection exceeds <see cref="MaxCopyLength"/>.
    /// Uses <see cref="PieceTable.CopyTo"/> via <c>string.Create</c> to allocate
    /// the result string once with zero intermediate copies.
    /// </summary>
    public string? GetSelectedText() {
        if (Selection.IsEmpty) return "";
        long selLen = Selection.Len;
        if (selLen > MaxCopyLength) return null;
        var len = (int)selLen;
        if (len <= PieceTable.MaxGetTextLength) {
            return _table.GetText(Selection.Start, len);
        }
        var start = Selection.Start;
        return string.Create(len, (_table, start),
            static (span, s) => s._table.CopyTo(s.start, span.Length, span));
    }

    /// <summary>
    /// True when the document's undo depth matches the last-saved position.
    /// Used to detect when undo/redo returns the document to its saved state.
    /// </summary>
    public bool IsAtSavePoint => _history.IsAtSavePoint;

    /// <summary>
    /// Records the current undo depth as the "saved" position.
    /// Call after successfully writing the document to disk.
    /// </summary>
    public void MarkSavePoint() => _history.MarkSavePoint();

    // -------------------------------------------------------------------------
    // Events
    // -------------------------------------------------------------------------

    /// <summary>Raised after any mutation to the document content.</summary>
    public event EventHandler? Changed;

    private int _suppressChanged;

    /// <summary>
    /// Suppresses <see cref="Changed"/> events until the returned disposable
    /// is disposed, then fires a single event.  Calls can be nested.
    /// </summary>
    public IDisposable SuppressChangedEvents() {
        _suppressChanged++;
        return new ChangedScope(this);
    }

    public void RaiseChanged() {
        if (_suppressChanged == 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class ChangedScope(Document doc) : IDisposable {
        private bool _disposed;
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            doc._suppressChanged--;
            if (doc._suppressChanged == 0) doc.Changed?.Invoke(doc, EventArgs.Empty);
        }
    }

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
        text = SanitizeSurrogates(text);
        var ofs = Selection.Start;
        var replacing = !Selection.IsEmpty;
        if (replacing) {
            _history.BeginCompound();
            DeleteRange(ofs, Selection.End - ofs);
        }
        PushInsert(ofs, text);
        if (replacing) {
            _history.EndCompound();
        }
        Selection = Selection.Collapsed(ofs + text.Length);
        RaiseChanged();
    }

    /// <summary>Deletes the current selection. No-op if selection is empty.</summary>
    public void DeleteSelection() {
        if (Selection.IsEmpty) {
            return;
        }
        var start = Selection.Start;
        DeleteRange(start, Selection.End - start);
        Selection = Selection.Collapsed(start);
        RaiseChanged();
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
        // Step back one code point so a surrogate pair is treated as a unit.
        // Also collapse \r\n into a single Backspace.
        var delOfs = CodepointBoundary.StepLeft(_table, ofs);
        if (ofs - delOfs == 1 && ofs >= 2 && _table.GetText(ofs - 2, 2) == "\r\n") {
            delOfs = ofs - 2;
        }
        var delLen = (int)(ofs - delOfs);
        var pieces = _table.CapturePieces(delOfs, delLen);
        _history.Push(new DeleteEdit(delOfs, delLen, pieces), _table, Selection);
        Selection = Selection.Collapsed(delOfs);
        RaiseChanged();
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
        // Step forward one code point so a surrogate pair is treated as a unit.
        // Also collapse \r\n into a single Delete.
        var endOfs = CodepointBoundary.StepRight(_table, ofs);
        if (endOfs - ofs == 1 && ofs + 1 < _table.Length && _table.GetText(ofs, 2) == "\r\n") {
            endOfs = ofs + 2;
        }
        var delLen = (int)(endOfs - ofs);
        var pieces = _table.CapturePieces(ofs, delLen);
        _history.Push(new DeleteEdit(ofs, delLen, pieces), _table, Selection);
        RaiseChanged();
    }

    // -------------------------------------------------------------------------
    // Undo / Redo
    // -------------------------------------------------------------------------

    /// <summary>
    /// Undoes the most recent edit. Returns the edit that was reverted,
    /// or <c>null</c> if nothing to undo.
    /// </summary>
    public IDocumentEdit? Undo() {
        var result = _history.Undo(_table);
        if (result is null) {
            return null;
        }
        Selection = result.Value.SelectionBefore;
        RaiseChanged();
        return result.Value.Edit;
    }

    /// <summary>
    /// Re-applies the most recently undone edit. Returns the edit that
    /// was applied, or <c>null</c> if nothing to redo.
    /// </summary>
    public IDocumentEdit? Redo() {
        var result = _history.Redo(_table);
        if (result is null) {
            return null;
        }
        Selection = Selection.Collapsed(CaretAfterRedo(result.Value.Edit));
        RaiseChanged();
        return result.Value.Edit;
    }

    /// <summary>
    /// Computes the caret position after applying <paramref name="edit"/>.
    /// </summary>
    private static long CaretAfterRedo(IDocumentEdit edit) => edit switch {
        // Insert applied → caret at end of inserted text.
        SpanInsertEdit ins => ins.Ofs + ins.Len,
        // Delete applied → caret at deletion point.
        DeleteEdit del => del.Ofs,
        // Compound applies in forward order → last apply is the last edit.
        CompoundEdit comp => CaretAfterRedo(comp.Edits[^1]),
        // Bulk replace → caret at start of document (no single obvious position).
        UniformBulkReplaceEdit => 0L,
        VaryingBulkReplaceEdit => 0L,
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

    /// <summary>
    /// Records an insert (and optional delete) that was already applied on a
    /// background thread. Updates history, selection, and fires Changed.
    /// </summary>
    public void RecordBackgroundPaste(long ofs, Selection selBefore,
        long addBufStart, int insertLen, bool replacing,
        Piece[]? deletePieces,
        (int StartLine, int[] LineLengths)? deleteLineInfo,
        int bufIdx = -1) {

        if (replacing && deletePieces != null) {
            var delEdit = deleteLineInfo is var (sl, ll)
                ? new DeleteEdit(ofs, selBefore.Len, deletePieces, sl, ll)
                : new DeleteEdit(ofs, selBefore.Len, deletePieces);
            var insEdit = new SpanInsertEdit(ofs, addBufStart, insertLen, bufIdx);
            var compound = new CompoundEdit(new List<IDocumentEdit> { delEdit, insEdit });
            _history.PushAlreadyApplied(compound, selBefore);
        } else {
            var insEdit = new SpanInsertEdit(ofs, addBufStart, insertLen, bufIdx);
            _history.PushAlreadyApplied(insEdit, selBefore);
        }
        Selection = Selection.Collapsed(ofs + insertLen);
        RaiseChanged();
    }

    /// <summary>
    /// Appends <paramref name="text"/> to the add buffer and pushes a
    /// <see cref="SpanInsertEdit"/> that references it.
    /// </summary>
    private void PushInsert(long ofs, string text) {
        text = SanitizeSurrogates(text);
        var bufStart = _table.AppendToAddBuffer(text);
        _history.Push(new SpanInsertEdit(ofs, bufStart, text.Length), _table, Selection);
    }

    /// <summary>
    /// Replaces every lone (unpaired) UTF-16 surrogate in <paramref name="text"/>
    /// with U+FFFD REPLACEMENT CHARACTER.  Lone surrogates are illegal in
    /// well-formed Unicode and break anything that round-trips text through
    /// UTF-8 or JSON, so we never let one enter the buffer.  Returns the
    /// original instance if it was already well-formed (no allocation).
    /// </summary>
    internal static string SanitizeSurrogates(string text) {
        // Fast scan: look for any unpaired surrogate.
        var bad = false;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (char.IsHighSurrogate(c)) {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) {
                    bad = true;
                    break;
                }
                i++; // skip the paired low surrogate
            } else if (char.IsLowSurrogate(c)) {
                bad = true;
                break;
            }
        }
        if (!bad) return text;

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (char.IsHighSurrogate(c)) {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                    sb.Append(c);
                    sb.Append(text[i + 1]);
                    i++;
                } else {
                    sb.Append('\uFFFD');
                }
            } else if (char.IsLowSurrogate(c)) {
                sb.Append('\uFFFD');
            } else {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private void DeleteRange(long ofs, long len) {
        var pieces = _table.CapturePieces(ofs, len);
        var lineInfo = _table.CaptureLineInfo(ofs, len);
        var edit = lineInfo is var (sl, ll)
            ? new DeleteEdit(ofs, len, pieces, sl, ll)
            : new DeleteEdit(ofs, len, pieces);
        _history.Push(edit, _table, Selection);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
