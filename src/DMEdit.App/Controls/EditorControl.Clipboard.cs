using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using DMEdit.App.Services;
using DMEdit.Core.Buffers;
using DMEdit.Core.Clipboard;
using DMEdit.Core.Documents;
using DMEdit.Core.Documents.History;

namespace DMEdit.App.Controls;

// Clipboard / PasteMore partial of EditorControl.  Handles copy, cut,
// paste (small + large), and the inline PasteMore cycling UI.  Shared
// fields (_clipboardRing, _isClipboardCycling, _clipboardCycleIndex,
// _cycleInsertedLength, _preferredCaretX, _editSw) live in the main
// EditorControl.cs.
public sealed partial class EditorControl {

    public async Task CopyAsync() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();

        if (doc.ColumnSel != null) {
            // Column selection: always materializes (selections are small).
            var text = doc.GetColumnSelectedText(_indentWidth);
            if (string.IsNullOrEmpty(text)) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(text);
            _clipboardRing.Push(text);
            return;
        }

        if (doc.Selection.IsEmpty) return;
        await CopySelectionToClipboard(doc);
    }

    public async Task CutAsync() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();

        if (doc.ColumnSel != null) {
            var text = doc.GetColumnSelectedText(_indentWidth);
            if (string.IsNullOrEmpty(text)) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(text);
            _clipboardRing.Push(text);
            _editSw.Restart();
            doc.DeleteColumnSelectionContent(_indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout();
            ResetCaretBlink();
            return;
        }

        if (doc.Selection.IsEmpty) return;
        if (!await CopySelectionToClipboard(doc)) return;
        _editSw.Restart();
        doc.DeleteSelection();
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>
    /// Copies the current stream selection to the system clipboard.
    /// Uses the native clipboard service when available (zero managed allocation);
    /// falls back to Avalonia's <c>SetTextAsync</c> with <c>string.Create</c>.
    /// </summary>
    private async Task<bool> CopySelectionToClipboard(Document doc) {
        var sel = doc.Selection;
        var nativeClip = NativeClipboardDiscovery.Service;
        if (nativeClip != null) {
            if (!nativeClip.Copy(doc.Table, sel.Start, sel.Len)) return false;
            // Push to ring only for small selections.
            if (sel.Len <= _clipboardRing.MaxEntryChars) {
                var ringText = doc.GetSelectedText();
                if (ringText != null) _clipboardRing.Push(ringText);
            }
        } else {
            // Fallback: materialize via string.Create + Avalonia clipboard.
            var text = doc.GetSelectedText();
            if (text == null) {
                CopyTooLarge?.Invoke(sel.Len);
                return false;
            }
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return false;
            await clipboard.SetTextAsync(text);
            _clipboardRing.Push(text);
        }
        return true;
    }

    public async Task PasteAsync() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        _preferredCaretX = -1;
        _editSw.Restart();

        // Column mode paste always needs the full string (for line splitting).
        // Native streaming paste is only used for normal (non-column) mode.
        var nativeClip = NativeClipboardDiscovery.Service;
        if (nativeClip != null && doc.ColumnSel == null) {
            const long LargePasteThreshold = 1024 * 1024; // 1M chars
            var clipSize = nativeClip.GetClipboardCharCount();

            if (clipSize > LargePasteThreshold) {
                await PasteLargeAsync(doc, nativeClip, clipSize);
                // Post to dispatcher so the layout cycle resolves
                // scrollbar visibility before we compute scroll position.
                Dispatcher.UIThread.Post(ScrollCaretIntoView);
            } else {
                PasteSmall(doc, nativeClip);
                ScrollCaretIntoView();
            }
        } else {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) { _editSw.Stop(); return; }
#pragma warning disable CS0618 // GetTextAsync is deprecated but TryGetTextAsync requires IAsyncDataTransfer
            var text = await clipboard.GetTextAsync();
#pragma warning restore CS0618
            if (string.IsNullOrEmpty(text)) { _editSw.Stop(); return; }
            // Normalize Windows line endings to LF
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            _clipboardRing.Push(text);
            if (doc.ColumnSel is { } colSel) {
                var lines = text.Split('\n');
                if (lines.Length == colSel.LineCount) {
                    doc.PasteAtCursors(lines, _indentWidth);
                } else {
                    doc.InsertAtCursors(text, _indentWidth);
                }
            } else {
                doc.Insert(text);
            }
            ScrollCaretIntoView();
        }

        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Small paste: clipboard → in-memory add buffer → insert.</summary>
    private void PasteSmall(Document doc, INativeClipboardService nativeClip) {
        var ofs = doc.Selection.Start;
        var replacing = !doc.Selection.IsEmpty;
        if (replacing) {
            doc.History.BeginCompound();
            var pieces = doc.Table.CapturePieces(ofs, doc.Selection.Len);
            var lineInfo = doc.Table.CaptureLineInfo(ofs, doc.Selection.Len);
            var delEdit = lineInfo is var (sl, ll)
                ? new DeleteEdit(ofs, doc.Selection.Len, pieces, sl, ll)
                : new DeleteEdit(ofs, doc.Selection.Len, pieces);
            doc.History.Push(delEdit, doc.Table, doc.Selection);
        }
        var addBufStart = doc.Table.AddBufferLength;
        nativeClip.Paste(doc.Table, null, default);
        var totalLen = (int)(doc.Table.AddBufferLength - addBufStart);
        if (totalLen > 0) {
            doc.History.Push(
                new SpanInsertEdit(ofs, addBufStart, totalLen), doc.Table, doc.Selection);
        }
        if (replacing) {
            doc.History.EndCompound();
        }
        doc.Selection = Selection.Collapsed(ofs + totalLen);
        doc.RaiseChanged();
    }

    /// <summary>
    /// Large paste: clipboard → file on disk → PagedFileBuffer → piece insert.
    /// The file stays on disk and is paged into memory on demand (~16 MB).
    /// </summary>
    private async Task PasteLargeAsync(Document doc, INativeClipboardService nativeClip,
        long clipCharCount) {

        var ofs = doc.Selection.Start;
        var selBefore = doc.Selection;
        var replacing = !selBefore.IsEmpty;

        // Capture delete pieces before going to background.
        Piece[]? deletePieces = null;
        (int StartLine, int[] LineLengths)? deleteLineInfo = null;
        if (replacing) {
            deletePieces = doc.Table.CapturePieces(ofs, selBefore.Len);
            deleteLineInfo = doc.Table.CaptureLineInfo(ofs, selBefore.Len);
        }

        // Find the active tab ID for the file name.
        var mw = TopLevel.GetTopLevel(this) as MainWindow;
        var tabId = mw?._activeTab?.Id ?? Guid.NewGuid().ToString("N")[..12];
        var bufFileIdx = doc.Table.Buffers.Count; // next available index
        var filePath = SessionStore.AllocateAddBufPath(tabId, bufFileIdx);

        // Phase 1 (UI thread): Stream clipboard → UTF-8 file on disk.
        using (var fs = File.Create(filePath)) {
            nativeClip.PasteToStream(fs, null, default);
        }

        // Phase 2 (background): Scan the file to build page table + line index.
        var savedIsEditBlocked = IsEditBlocked;
        IsEditBlocked = true;
        BackgroundPasteInProgress = true;
        BackgroundPasteChanged?.Invoke(true);
        InvalidateVisual();

        var byteLen = new FileInfo(filePath).Length;
        var paged = new PagedFileBuffer(filePath, byteLen);
        var tcs = new TaskCompletionSource();
        paged.LoadComplete += () => tcs.TrySetResult();
        paged.StartLoading(default);
        await tcs.Task;

        var pastedBufIdx = doc.Table.RegisterBuffer(paged);
        var totalLen = (int)paged.Length;

        // Phase 3 (background): Delete + insert piece + rebuild line tree.
        await Task.Run(() => {
            if (replacing) {
                doc.Table.Delete(ofs, selBefore.Len);
            }
            doc.Table.InsertFromBuffer(ofs, pastedBufIdx, 0, totalLen);
        });

        // Phase 4 (UI thread): Record history, update selection, unblock.
        doc.RecordBackgroundPaste(ofs, selBefore, 0, totalLen,
            replacing, deletePieces, deleteLineInfo, pastedBufIdx);

        BackgroundPasteInProgress = false;
        BackgroundPasteChanged?.Invoke(false);
        IsEditBlocked = savedIsEditBlocked;
    }

    /// <summary>
    /// Pastes from the clipboard ring with inline cycling. First press pastes
    /// the most recent entry; subsequent presses (while Ctrl is held) replace
    /// the pasted text with the next older entry.
    /// </summary>
    public void PasteMore() {
        var doc = Document;
        if (doc == null || _clipboardRing.Count == 0) return;
        FlushCompound();
        if (!_isClipboardCycling) {
            // Start a new cycling session.
            _isClipboardCycling = true;
            _clipboardCycleIndex = 0;
        } else {
            // Cycle to the next entry — undo the previous paste first.
            _clipboardCycleIndex = (_clipboardCycleIndex + 1) % _clipboardRing.Count;
            if (_cycleInsertedLength > 0) {
                var caret = doc.Selection.Caret;
                doc.Selection = new Selection(caret - _cycleInsertedLength, caret);
                doc.DeleteSelection();
            }
        }
        var text = _clipboardRing.Get(_clipboardCycleIndex);
        if (text == null) return;
        _preferredCaretX = -1;
        _editSw.Restart();
        doc.Insert(text);
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        _cycleInsertedLength = text.Length;
        InvalidateLayout();
        ResetCaretBlink();
        ClipboardCycleStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Confirms the current clipboard cycling selection. Called when the
    /// user releases Ctrl (or performs any other action).
    /// </summary>
    public void ConfirmClipboardCycle() {
        if (!_isClipboardCycling) return;
        _isClipboardCycling = false;
        _clipboardCycleIndex = 0;
        _cycleInsertedLength = 0;
        ClipboardCycleStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pastes a specific entry from the clipboard ring by index. Used by the
    /// ClipboardRing popup dialog.
    /// </summary>
    public void PasteFromRing(int index) {
        var doc = Document;
        if (doc == null) return;
        var text = _clipboardRing.Get(index);
        if (text == null) return;
        FlushCompound();
        _preferredCaretX = -1;
        _editSw.Restart();
        doc.Insert(text);
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>Whether the editor is currently in a PasteMore cycling session.</summary>
    public bool IsClipboardCycling => _isClipboardCycling;

    /// <summary>Current cycling index (0-based) into the clipboard ring.</summary>
    public int ClipboardCycleIndex => _clipboardCycleIndex;

    /// <summary>Raised when clipboard cycling starts, advances, or ends.</summary>
    public event EventHandler? ClipboardCycleStatusChanged;

    /// <summary>
    /// Raised when a Copy or Cut falls back to the Avalonia clipboard and
    /// the selection exceeds <see cref="Document.MaxCopyLength"/>.
    /// The argument is the selection length in characters.
    /// </summary>
    public event Action<long>? CopyTooLarge;
}
