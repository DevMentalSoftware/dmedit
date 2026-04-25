using System;
using System.Buffers;
using System.Text;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.App.Services;
using DMEdit.Core.Documents;
using DMEdit.Core.Documents.History;

namespace DMEdit.App.Controls;

// Command dispatch partial of EditorControl.  Holds every Public
// command Perform* method, the column-mode command helpers, the
// caret movement helpers (MoveCaretHorizontal/Vertical/ToLineEdge
// plus SnapOutOfDeadZone and FindWordBoundary*), the indent/deindent
// helpers, and the big RegisterCommands() wire-up.  Shared fields
// live in the main EditorControl.cs.
public sealed partial class EditorControl {

    public void EditDelete() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        Coalesce("delete");
        _editSw.Restart();
        if (!doc.Selection.IsEmpty) {
            doc.DeleteSelection();
        } else {
            doc.DeleteForward();
        }
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformUndo() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        _editSw.Restart();
        var edit = doc.Undo();
        if (!IsBulkReplace(edit)) {
            ScrollCaretIntoView();
        }
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformRedo() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        _editSw.Restart();
        var edit = doc.Redo();
        if (!IsBulkReplace(edit)) {
            ScrollCaretIntoView();
        }
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    private static bool IsBulkReplace(IDocumentEdit? edit) =>
        edit is UniformBulkReplaceEdit or VaryingBulkReplaceEdit;

    public void PerformSelectAll() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        doc.Selection = new Selection(0L, doc.Table.Length);
        InvalidateVisual();
    }

    public void PerformSelectWord() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        doc.SelectWord();
        InvalidateVisual();
        ResetCaretBlink();
    }

    public void PerformExpandSelection() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        // Passive setting: read directly so UI toggle takes effect immediately
        // without going through the SettingChanged switch.  Default matches
        // AppSettings.ExpandSelectionMode's default if Settings isn't injected.
        var mode = Settings?.ExpandSelectionMode ?? ExpandSelectionMode.SubwordFirst;
        doc.ExpandSelection(mode);
        InvalidateVisual();
        ResetCaretBlink();
    }

    public void PerformDeleteLine() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        Coalesce("delete-line");
        _editSw.Restart();
        doc.DeleteLine();
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformMoveLineUp() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        Coalesce("move-line-up");
        _editSw.Restart();
        doc.MoveLineUp();
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformMoveLineDown() {
        var doc = Document;
        if (doc == null) {
            return;
        }
        Coalesce("move-line-down");
        _editSw.Restart();
        doc.MoveLineDown();
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    public void PerformTransformCase(CaseTransform transform) {
        var doc = Document;
        if (doc == null) {
            return;
        }
        FlushCompound();
        _editSw.Restart();
        doc.TransformCase(transform);
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    // -------------------------------------------------------------------------
    // Keyboard input
    // -------------------------------------------------------------------------

    protected override void OnTextInput(TextInputEventArgs e) {
        base.OnTextInput(e);
        if (IsEditBlocked) { e.Handled = true; return; }
        if (_inIncrementalSearch) {
            HandleIncrementalSearchChar(e.Text ?? "");
            e.Handled = true;
            return;
        }
        var doc = Document;
        if (doc == null || string.IsNullOrEmpty(e.Text)) {
            return;
        }
        _preferredCaretX = -1;
        _preferredCaretCol = -1;

        if (doc.ColumnSel != null) {
            Coalesce("col-char");
            _editSw.Restart();
            doc.InsertAtCursors(e.Text, _indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            e.Handled = true;
            InvalidateLayout();
            ResetCaretBlink();
            return;
        }

        Coalesce("char");

        // In overwrite mode, select the next code point(s) so Insert replaces them.
        // Don't overwrite past line endings (standard overwrite behavior).
        // Walk both the typed text and the buffer by whole code points so a
        // surrogate pair under the caret is consumed as one unit.
        if (_overwriteMode && doc.Selection.IsEmpty && e.Text != null) {
            var caret = doc.Selection.Caret;
            var table = doc.Table;
            var len = table.Length;
            var charsToOverwrite = 0;
            var typedIdx = 0;
            while (typedIdx < e.Text.Length && caret + charsToOverwrite < len) {
                var bufW = CodepointBoundary.WidthAt(table, caret + charsToOverwrite);
                var ch = table.GetText(caret + charsToOverwrite, 1);
                if (ch[0] is '\r' or '\n') break;
                charsToOverwrite += bufW;
                typedIdx += char.IsHighSurrogate(e.Text[typedIdx])
                    && typedIdx + 1 < e.Text.Length
                    && char.IsLowSurrogate(e.Text[typedIdx + 1])
                    ? 2 : 1;
            }
            if (charsToOverwrite > 0) {
                doc.Selection = new Selection(caret, caret + charsToOverwrite);
            }
        }

        _editSw.Restart();
        doc.Insert(e.Text!);
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        e.Handled = true;
        InvalidateLayout();
        ResetCaretBlink();
    }

    // -------------------------------------------------------------------------
    // Command dispatch (called by MainWindow after key → command resolution)
    // -------------------------------------------------------------------------


    private const string ColumnBlockedByWrap =
        "Column editing disabled in character-wrap mode.";

    private void PerformColumnSelectVertical(TextDocument doc, int delta) {
        if (_charWrapMode) {
            StatusMessage?.Invoke(ColumnBlockedByWrap);
            return;
        }
        FlushCompound();
        var table = doc.Table;
        if (doc.ColumnSel is { } colSel) {
            // Already in column mode — extend by one line.
            var newLine = Math.Clamp(colSel.ActiveLine + delta, 0, (int)table.LineCount - 1);
            doc.ColumnSel = colSel.ExtendTo(newLine, colSel.ActiveCol);
        } else {
            // Enter column mode from current caret.
            var caret = doc.Selection.Caret;
            var line = (int)table.LineFromOfs(caret);
            var col = ColumnSelection.OfsToCol(table, caret, _indentWidth);
            var targetLine = Math.Clamp(line + delta, 0, (int)table.LineCount - 1);
            doc.ColumnSel = new ColumnSelection(line, col, targetLine, col);
        }
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    private void PerformColumnSelectHorizontal(TextDocument doc, int delta) {
        if (_charWrapMode) {
            StatusMessage?.Invoke(ColumnBlockedByWrap);
            return;
        }
        FlushCompound();
        if (doc.ColumnSel is { } colSel) {
            var newCol = ColumnSelection.NextCharCol(
                doc.Table, colSel.ActiveLine, colSel.ActiveCol, delta, _indentWidth);
            doc.ColumnSel = colSel.ExtendTo(colSel.ActiveLine, newCol);
        } else {
            // Enter column mode from current caret.
            var caret = doc.Selection.Caret;
            var line = (int)doc.Table.LineFromOfs(caret);
            var col = ColumnSelection.OfsToCol(doc.Table, caret, _indentWidth);
            var newCol = ColumnSelection.NextCharCol(doc.Table, line, col, delta, _indentWidth);
            doc.ColumnSel = new ColumnSelection(line, col, line, newCol);
        }
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Plain Left/Right in column mode: collapse selection or shift carets.
    /// </summary>
    private void PerformColumnMoveHorizontal(TextDocument doc, int delta) {
        if (doc.ColumnSel is not { } colSel) return;
        FlushCompound();
        if (colSel.LeftCol != colSel.RightCol) {
            // Has selection → collapse to the edge in the movement direction.
            doc.ColumnSel = delta < 0 ? colSel.CollapseToLeft() : colSel.CollapseToRight();
        } else {
            var newCol = ColumnSelection.NextCharCol(
                doc.Table, colSel.ActiveLine, colSel.ActiveCol, delta, _indentWidth);
            doc.ColumnSel = colSel.MoveColumnsTo(newCol);
        }
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Plain Up/Down in column mode: collapse any selection, then shift the
    /// entire caret group by one line.
    /// </summary>
    private void PerformColumnMoveVertical(TextDocument doc, int delta) {
        if (doc.ColumnSel is not { } colSel) return;
        FlushCompound();
        if (colSel.LeftCol != colSel.RightCol) {
            colSel = colSel.CollapseToLeft();
        }
        var maxLine = (int)doc.Table.LineCount - 1;
        doc.ColumnSel = colSel.ShiftLines(delta, maxLine);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Ctrl+Left/Right in column mode: move all carets to the next word boundary.
    /// Uses the first caret line as the reference for computing the word delta.
    /// </summary>
    private void PerformColumnMoveWord(TextDocument doc, int direction) {
        if (doc.ColumnSel is not { } colSel) return;
        FlushCompound();
        // Collapse selection first if any.
        if (colSel.LeftCol != colSel.RightCol) {
            colSel = direction < 0 ? colSel.CollapseToLeft() : colSel.CollapseToRight();
        }
        var wordCol = ColumnSelection.FindWordBoundaryCol(doc.Table, colSel.TopLine, colSel.LeftCol, direction, _indentWidth);
        doc.ColumnSel = colSel.MoveColumnsTo(wordCol);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Ctrl+Shift+Left/Right in column mode: extend selection to word boundary.
    /// </summary>
    private void PerformColumnSelectWord(TextDocument doc, int direction) {
        if (doc.ColumnSel is not { } colSel) return;
        var wordCol = ColumnSelection.FindWordBoundaryCol(doc.Table, colSel.ActiveLine, colSel.ActiveCol, direction, _indentWidth);
        doc.ColumnSel = colSel.ExtendTo(colSel.ActiveLine, wordCol);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Returns the maximum end-of-line column across all lines in the column selection.
    /// </summary>
    private int MaxEndColumn(TextDocument doc, ColumnSelection colSel) {
        var table = doc.Table;
        var max = 0;
        for (var line = colSel.TopLine; line <= colSel.BottomLine; line++) {
            var endCol = ColumnSelection.EndOfLineCol(table, line, _indentWidth);
            if (endCol > max) max = endCol;
        }
        return max;
    }

    public void RegisterCommands() {
        // Column-mode intercepts: alternative behavior for commands when column
        // selection is active. Returns true if fully handled, false to fall
        // through to normal handling (e.g. Edit.Newline exits column mode first).
        var columnIntercepts = new Dictionary<Command, Func<TextDocument, bool>>();

        void ColIntercept(Command cmd, Func<TextDocument, bool> handler) =>
            columnIntercepts[cmd] = handler;

        // Local helper: wraps each editor command with the standard preamble.
        void Reg(Command cmd, Action<TextDocument> action,
                 bool isVerticalNav = false, bool isColumnAware = false,
                 Func<bool>? canExecute = null) {
            cmd.Wire(() => {
                if (IsEditBlocked && cmd.Category == "Edit"
                    && cmd != Cmd.EditSelectAll && cmd != Cmd.EditSelectWord
                    && cmd != Cmd.EditExpandSelection && cmd != Cmd.EditCopy
                    && cmd != Cmd.EditToggleOverwrite) return;
                var doc = Document;
                if (doc == null) return;
                if (_isClipboardCycling && cmd != Cmd.EditPasteMore) ConfirmClipboardCycle();
                if (!isVerticalNav) {
                    _preferredCaretX = -1;
                    _preferredCaretCol = -1;
                }

                if (cmd == Cmd.NavColumnSelectUp || cmd == Cmd.NavColumnSelectDown) {
                    var delta = cmd == Cmd.NavColumnSelectUp ? -1 : +1;
                    PerformColumnSelectVertical(doc, delta);
                    return;
                }

                if (cmd == Cmd.NavColumnSelectLeft || cmd == Cmd.NavColumnSelectRight) {
                    var delta = cmd == Cmd.NavColumnSelectLeft ? -1 : +1;
                    PerformColumnSelectHorizontal(doc, delta);
                    return;
                }

                if (doc.ColumnSel != null) {
                    if (columnIntercepts.TryGetValue(cmd, out var intercept) && intercept(doc))
                        return;
                    if (!isColumnAware) doc.ClearColumnSelection(_indentWidth);
                }

                action(doc);
            }, canExecute: canExecute);
        }

        // -- Edit commands --

        Reg(Cmd.EditBackspace, doc => {
            Coalesce("backspace");
            _editSw.Restart();
            if (doc.Selection.IsEmpty && TrySmartDeindent(doc)) {
                // Smart deindent handled the deletion.
            } else {
                doc.DeleteBackward();
            }
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout();
            ResetCaretBlink();
        }, isColumnAware: true);

        Reg(Cmd.EditDelete, _ => EditDelete(), isColumnAware: true,
            canExecute: () => HasSelection() || (Document is { } d && d.Selection.Caret < d.Table.Length));
        Reg(Cmd.EditUndo, _ => PerformUndo(), canExecute: () => Document?.CanUndo == true);
        Reg(Cmd.EditRedo, _ => PerformRedo(), canExecute: () => Document?.CanRedo == true);
        Reg(Cmd.EditCut, doc => { _ = CutAsync(); }, isColumnAware: true,
            canExecute: HasSelection);
        Reg(Cmd.EditCopy, doc => { _ = CopyAsync(); }, isColumnAware: true,
            canExecute: HasSelection);
        Reg(Cmd.EditPaste, doc => { _ = PasteAsync(); }, isColumnAware: true);
        Reg(Cmd.EditPasteMore, _ => PasteMore(),
            canExecute: () => _clipboardRing.Count > 1);
        Reg(Cmd.EditSelectAll, _ => PerformSelectAll());
        Reg(Cmd.EditSelectWord, _ => PerformSelectWord());
        Reg(Cmd.EditExpandSelection, _ => PerformExpandSelection());
        Reg(Cmd.EditDeleteLine, _ => PerformDeleteLine());
        Reg(Cmd.EditMoveLineUp, _ => PerformMoveLineUp());
        Reg(Cmd.EditMoveLineDown, _ => PerformMoveLineDown());
        Reg(Cmd.EditUpperCase, _ => PerformTransformCase(CaseTransform.Upper),
            canExecute: HasSelection);
        Reg(Cmd.EditLowerCase, _ => PerformTransformCase(CaseTransform.Lower),
            canExecute: HasSelection);
        Reg(Cmd.EditProperCase, _ => PerformTransformCase(CaseTransform.Proper),
            canExecute: HasSelection);

        Reg(Cmd.EditToggleOverwrite, _ => {
            OverwriteMode = !OverwriteMode;
        });

        Reg(Cmd.EditNewline, doc => {
            FlushCompound();
            _editSw.Restart();
            var table = doc.Table;
            var lineIdx = table.LineFromOfs(doc.Selection.Caret);
            var lineText = table.GetLine(lineIdx);
            var indent = GetLeadingWhitespace(lineText);
            var nl = doc.LineEndingInfo.NewlineString;
            var lineStart = table.LineStartOfs(lineIdx);
            var caretCol = (int)(doc.Selection.Caret - lineStart);

            // Strip trailing whitespace from the current line when pressing Enter.
            // If the caret is at or past the last non-whitespace character,
            // delete from last non-ws to the caret, then insert the newline.
            var trimmedLen = lineText.TrimEnd().Length;
            if (caretCol >= trimmedLen && caretCol > trimmedLen) {
                doc.Selection = new Selection(lineStart + trimmedLen, lineStart + caretCol);
                doc.Insert(nl + indent);
            } else {
                doc.Insert(nl + indent);
            }
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout();
            ResetCaretBlink();
        }, isColumnAware: true);

        Reg(Cmd.EditTab, doc => {
            Coalesce("tab");
            _editSw.Restart();
            doc.Insert("\t");
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout();
            ResetCaretBlink();
        }, isColumnAware: true);

        // -- Navigation: horizontal --

        Reg(Cmd.NavMoveLeft, doc => { FlushCompound(); MoveCaretHorizontal(doc, -1, false, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectLeft, doc => { FlushCompound(); MoveCaretHorizontal(doc, -1, false, true); }, isColumnAware: true);
        Reg(Cmd.NavMoveRight, doc => { FlushCompound(); MoveCaretHorizontal(doc, +1, false, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectRight, doc => { FlushCompound(); MoveCaretHorizontal(doc, +1, false, true); }, isColumnAware: true);
        Reg(Cmd.NavMoveWordLeft, doc => { FlushCompound(); MoveCaretHorizontal(doc, -1, true, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectWordLeft, doc => { FlushCompound(); MoveCaretHorizontal(doc, -1, true, true); }, isColumnAware: true);
        Reg(Cmd.NavMoveWordRight, doc => { FlushCompound(); MoveCaretHorizontal(doc, +1, true, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectWordRight, doc => { FlushCompound(); MoveCaretHorizontal(doc, +1, true, true); }, isColumnAware: true);

        // -- Navigation: vertical --

        Reg(Cmd.NavMoveUp, doc => { FlushCompound(); MoveCaretVertical(doc, -1, false); }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavSelectUp, doc => { FlushCompound(); MoveCaretVertical(doc, -1, true); }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavMoveDown, doc => { FlushCompound(); MoveCaretVertical(doc, +1, false); }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavSelectDown, doc => { FlushCompound(); MoveCaretVertical(doc, +1, true); }, isVerticalNav: true, isColumnAware: true);

        // -- Navigation: home/end --

        Reg(Cmd.NavMoveHome, doc => { FlushCompound(); MoveCaretToLineEdge(doc, toStart: true, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectHome, doc => { FlushCompound(); MoveCaretToLineEdge(doc, toStart: true, true); });
        Reg(Cmd.NavMoveEnd, doc => { FlushCompound(); MoveCaretToLineEdge(doc, toStart: false, false); }, isColumnAware: true);
        Reg(Cmd.NavSelectEnd, doc => { FlushCompound(); MoveCaretToLineEdge(doc, toStart: false, true); });

        // -- Navigation: document start/end --

        Reg(Cmd.NavMoveDocStart, doc => {
            FlushCompound();
            doc.Selection = Selection.Collapsed(0);
            ScrollCaretIntoView(ScrollPolicy.Top);
            InvalidateVisual();
            ResetCaretBlink();
        });

        Reg(Cmd.NavSelectDocStart, doc => {
            FlushCompound();
            doc.Selection = doc.Selection.ExtendTo(0);
            ScrollCaretIntoView(ScrollPolicy.Top);
            InvalidateVisual();
            ResetCaretBlink();
        });

        Reg(Cmd.NavMoveDocEnd, doc => {
            FlushCompound();
            doc.Selection = Selection.Collapsed(doc.Table.Length);
            ScrollCaretIntoView(ScrollPolicy.Bottom);
            InvalidateVisual();
            ResetCaretBlink();
        });

        Reg(Cmd.NavSelectDocEnd, doc => {
            FlushCompound();
            doc.Selection = doc.Selection.ExtendTo(doc.Table.Length);
            ScrollCaretIntoView(ScrollPolicy.Bottom);
            InvalidateVisual();
            ResetCaretBlink();
        });

        // -- Navigation: page up/down --

        Reg(Cmd.NavPageUp, doc => { FlushCompound(); MoveCaretByPage(doc, -1, false); }, isVerticalNav: true);
        Reg(Cmd.NavSelectPageUp, doc => { FlushCompound(); MoveCaretByPage(doc, -1, true); }, isVerticalNav: true);
        Reg(Cmd.NavPageDown, doc => { FlushCompound(); MoveCaretByPage(doc, +1, false); }, isVerticalNav: true);
        Reg(Cmd.NavSelectPageDown, doc => { FlushCompound(); MoveCaretByPage(doc, +1, true); }, isVerticalNav: true);

        // -- Editing: word delete, line ops, indent --

        Reg(Cmd.EditDeleteWordLeft, doc => {
            FlushCompound();
            if (!doc.Selection.IsEmpty) {
                doc.DeleteSelection();
            } else {
                var wordLeft = FindWordBoundaryLeft(doc, doc.Selection.Caret);
                if (wordLeft < doc.Selection.Caret) {
                    doc.Selection = new Selection(wordLeft, doc.Selection.Caret);
                    doc.DeleteSelection();
                }
            }
            ScrollCaretIntoView();
            InvalidateLayout();
            ResetCaretBlink();
        }, isColumnAware: true);

        Reg(Cmd.EditDeleteWordRight, doc => {
            FlushCompound();
            if (!doc.Selection.IsEmpty) {
                doc.DeleteSelection();
            } else {
                var wordRight = FindWordBoundaryRight(doc, doc.Selection.Caret);
                if (wordRight > doc.Selection.Caret) {
                    doc.Selection = new Selection(doc.Selection.Caret, wordRight);
                    doc.DeleteSelection();
                }
            }
            ScrollCaretIntoView();
            InvalidateLayout();
            ResetCaretBlink();
        }, isColumnAware: true);

        Reg(Cmd.EditInsertLineBelow, _ => PerformInsertLineBelow());
        Reg(Cmd.EditInsertLineAbove, _ => PerformInsertLineAbove());
        Reg(Cmd.EditDuplicateLine, _ => PerformDuplicateLine());
        Reg(Cmd.EditSmartIndent, _ => { FlushCompound(); PerformSmartIndent(); });
        Reg(Cmd.EditIndent, _ => { FlushCompound(); PerformSimpleIndent(); });
        Reg(Cmd.EditOutdent, _ => { FlushCompound(); PerformOutdent(); });

        // -- Indent conversion --

        Reg(Cmd.EditIndentToSpaces, doc => {
            if (_charWrapMode) return;
            FlushCompound();
            doc.ConvertIndentation(Core.Documents.IndentStyle.Spaces, _indentWidth);
            InvalidateLayout();
        });
        Reg(Cmd.EditIndentToTabs, doc => {
            if (_charWrapMode) return;
            FlushCompound();
            doc.ConvertIndentation(Core.Documents.IndentStyle.Tabs, _indentWidth);
            InvalidateLayout();
        });

        // -- Scroll without moving caret --

        Reg(Cmd.ViewScrollLineUp, _ => {
            FlushCompound();
            ScrollValue -= GetRowHeight();
            InvalidateVisual();
            ResetCaretBlink(); // re-show caret (ScrollValue setter hides it)
        }, isVerticalNav: true);
        Reg(Cmd.ViewScrollLineDown, _ => {
            FlushCompound();
            ScrollValue += GetRowHeight();
            InvalidateVisual();
            ResetCaretBlink(); // re-show caret (ScrollValue setter hides it)
        }, isVerticalNav: true);

        // -- Column selection commands (handled in preamble, register with empty action) --

        Reg(Cmd.NavColumnSelectUp, _ => { }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavColumnSelectDown, _ => { }, isVerticalNav: true, isColumnAware: true);
        Reg(Cmd.NavColumnSelectLeft, _ => { }, isColumnAware: true);
        Reg(Cmd.NavColumnSelectRight, _ => { }, isColumnAware: true);

        // -- Column-mode intercepts --
        // These replace the normal behavior of existing commands when a column
        // selection is active. Return true = fully handled; false = exit column
        // mode and fall through to normal handling.
        ColIntercept(Cmd.NavColumnSelectUp, doc => { PerformColumnSelectVertical(doc, -1); return true; });
        ColIntercept(Cmd.NavColumnSelectDown, doc => { PerformColumnSelectVertical(doc, +1); return true; });
        ColIntercept(Cmd.NavColumnSelectLeft, doc => { PerformColumnSelectHorizontal(doc, -1); return true; });
        ColIntercept(Cmd.NavColumnSelectRight, doc => { PerformColumnSelectHorizontal(doc, +1); return true; });
        ColIntercept(Cmd.NavMoveLeft, doc => { PerformColumnMoveHorizontal(doc, -1); return true; });
        ColIntercept(Cmd.NavMoveRight, doc => { PerformColumnMoveHorizontal(doc, +1); return true; });
        ColIntercept(Cmd.NavMoveUp, doc => { PerformColumnMoveVertical(doc, -1); return true; });
        ColIntercept(Cmd.NavMoveDown, doc => { PerformColumnMoveVertical(doc, +1); return true; });
        ColIntercept(Cmd.NavSelectLeft, doc => { PerformColumnSelectHorizontal(doc, -1); return true; });
        ColIntercept(Cmd.NavSelectRight, doc => { PerformColumnSelectHorizontal(doc, +1); return true; });
        ColIntercept(Cmd.NavSelectUp, doc => { PerformColumnSelectVertical(doc, -1); return true; });
        ColIntercept(Cmd.NavSelectDown, doc => { PerformColumnSelectVertical(doc, +1); return true; });
        ColIntercept(Cmd.NavMoveHome, doc => {
            if (doc.ColumnSel is { } sel) {
                doc.ColumnSel = sel.MoveColumnsTo(0);
                ScrollCaretIntoView(); InvalidateVisual(); ResetCaretBlink();
            }
            return true;
        });
        ColIntercept(Cmd.NavMoveEnd, doc => {
            if (doc.ColumnSel is { } sel) {
                doc.ColumnSel = sel.MoveColumnsTo(MaxEndColumn(doc, sel));
                ScrollCaretIntoView(); InvalidateVisual(); ResetCaretBlink();
            }
            return true;
        });
        ColIntercept(Cmd.NavMoveWordLeft, doc => { PerformColumnMoveWord(doc, -1); return true; });
        ColIntercept(Cmd.NavMoveWordRight, doc => { PerformColumnMoveWord(doc, +1); return true; });
        ColIntercept(Cmd.NavSelectWordLeft, doc => { PerformColumnSelectWord(doc, -1); return true; });
        ColIntercept(Cmd.NavSelectWordRight, doc => { PerformColumnSelectWord(doc, +1); return true; });
        ColIntercept(Cmd.EditNewline, doc => {
            // Exit column mode, then fall through to normal newline handling.
            doc.ClearColumnSelection(_indentWidth);
            return false;
        });
        ColIntercept(Cmd.EditBackspace, doc => {
            FlushCompound();
            _editSw.Restart();
            doc.DeleteBackwardAtCursors(_indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout(); ResetCaretBlink();
            return true;
        });
        ColIntercept(Cmd.EditDelete, doc => {
            FlushCompound();
            _editSw.Restart();
            doc.DeleteForwardAtCursors(_indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout(); ResetCaretBlink();
            return true;
        });
        ColIntercept(Cmd.EditTab, doc => {
            Coalesce("col-tab");
            _editSw.Restart();
            doc.InsertAtCursors("\t", _indentWidth);
            ScrollCaretIntoView();
            _editSw.Stop();
            PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
            InvalidateLayout(); ResetCaretBlink();
            return true;
        });
    }

    // -------------------------------------------------------------------------
    // Caret movement helpers
    // -------------------------------------------------------------------------

    private void MoveCaretHorizontal(TextDocument doc, int delta, bool byWord, bool extend) {
        var table = doc.Table;
        var len = table.Length;

        // Primary caret state: CaretPosition (the public property).  It
        // resolves against Selection.Caret on read, so a stale cache
        // from an out-of-order legacy write doesn't matter.  Null means
        // no visual data (slow path / off-viewport); callers fall
        // through to plain-offset stepping against Selection.Caret.
        var current = CaretPosition;

        // By-word movement crosses wrap boundaries transparently (a word
        // spans its wrapped rows).  No visual-row stepping; caret lands
        // on a logical word boundary.
        if (byWord) {
            var fromOfs = current?.CharOffset ?? doc.Selection.Caret;
            var wordDst = delta < 0
                ? FindWordBoundaryLeft(doc, fromOfs)
                : FindWordBoundaryRight(doc, fromOfs);
            wordDst = Math.Clamp(wordDst, 0L, len);
            if (!_charWrapMode) {
                wordDst = SnapOutOfDeadZone(table, wordDst, delta > 0);
            }
            CommitPlainCaret(wordDst, extend);
            return;
        }

        // Visual stepping in TextPosition space (wrap on).  Every wrap
        // boundary exposes two distinct visual positions for one offset
        // (end of row r  vs  start of row r+1); arrow keys traverse both.
        if ((_wrapLines || _charWrapMode) && current is { } cur) {
            var next = StepCaretPosHorizontal(cur, delta);
            var nextOfs = next.CharOffset;
            if (!_charWrapMode) {
                var snapped = SnapOutOfDeadZone(table, nextOfs, delta > 0);
                if (snapped != nextOfs) {
                    next = BuildCaretPos(snapped);
                }
            }
            CommitCaretPos(next, extend);
            return;
        }

        // Fallback: wrap off, off-viewport, or slow-path line.
        var fallbackFrom = current?.CharOffset ?? doc.Selection.Caret;
        var newCaret = delta < 0
            ? CodepointBoundary.StepLeft(table, fallbackFrom)
            : CodepointBoundary.StepRight(table, fallbackFrom);
        newCaret = Math.Clamp(newCaret, 0L, len);
        if (!_charWrapMode) {
            newCaret = SnapOutOfDeadZone(table, newCaret, delta > 0);
        }
        CommitPlainCaret(newCaret, extend);
    }

    // ------------------------------------------------------------------
    //  TextPosition caret helpers
    //
    //  Migrated code paths (currently: horizontal arrows) read _caretPosition
    //  directly and write via CommitCaretPos.  The _caretIsAtEnd
    //  property setter in EditorControl.cs keeps _caretPosition fresh when
    //  unmigrated code assigns the legacy affinity flag — no stale-
    //  cache reconciliation needed at read sites.
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="TextPosition"/> for <paramref name="ofs"/>.
    /// At a wrap boundary always returns the downstream interpretation
    /// (start of the next row); upstream parking is captured directly
    /// in <c>_caretPosition</c> by <see cref="CommitCaretPos"/>.
    /// Returns <c>null</c> when the layout cannot produce row data for
    /// this offset (slow path or off-viewport).
    /// </summary>
    private TextPosition? TryBuildCaretPos(long ofs) {
        var doc = Document!;
        var table = doc.Table;
        var lineIdx = table.LineFromOfs(ofs);

        if (_charWrapMode && _charWrapCharsPerRow > 0) {
            // CharWrap convention: TextPosition.LineIdx is the absolute
            // visual row (matches LayoutLine.LineIdx for CharWrap), and
            // RowInLine is always 0 (each LayoutLine is one visual row).
            var cpr = _charWrapCharsPerRow;
            var cwLineIdx = ofs / cpr;
            var cwCol = (int)(ofs - cwLineIdx * cpr);
            return new TextPosition(cwLineIdx, 0, cwCol, ofs);
        }

        var layout = EnsureLayout();
        if (layout.Lines.Count == 0) {
            return null;
        }
        var localOfs = ofs - layout.ViewportBase;
        Rendering.Layout.LayoutLine? ll = null;
        for (var i = layout.Lines.Count - 1; i >= 0; i--) {
            if (layout.Lines[i].CharStart <= localOfs) {
                ll = layout.Lines[i];
                break;
            }
        }
        if (ll == null || ll.Mono is not { } mono) {
            return null;
        }
        var charInLine = (int)(localOfs - ll.CharStart);
        if (charInLine < 0 || charInLine > ll.CharLen) {
            return null;
        }
        var (row, col) = mono.OffsetToPos(charInLine);
        // OffsetToPos returns the upstream view at a boundary; flip to
        // downstream so a fresh build (no captured affinity) lands at
        // the start of the next row by default.
        if (row + 1 < mono.Rows.Length && col == mono.Rows[row].CharLen) {
            row++;
            col = 0;
        }
        return new TextPosition(lineIdx, row, col, ofs);
    }

    /// <summary>
    /// Builds a <see cref="TextPosition"/> for <paramref name="ofs"/>,
    /// returning a degenerate (row 0, col 0) value when layout data isn't
    /// available.  Used when a caller has already committed to an offset
    /// and just needs a struct to hand to <see cref="CommitCaretPos"/>.
    /// </summary>
    private TextPosition BuildCaretPos(long ofs) {
        return TryBuildCaretPos(ofs)
            ?? new TextPosition(Document!.Table.LineFromOfs(ofs), 0, 0, ofs);
    }

    /// <summary>
    /// One horizontal visual step from <paramref name="current"/>.
    /// Pure <see cref="TextPosition"/> in, <see cref="TextPosition"/> out
    /// — the legacy affinity bit never appears in the signature.
    /// </summary>
    private TextPosition StepCaretPosHorizontal(TextPosition current, int delta) {
        var table = Document!.Table;

        if (_charWrapMode && _charWrapCharsPerRow > 0) {
            return StepCaretPosCharWrap(current, delta);
        }

        var layout = EnsureLayout();
        var localOfs = current.CharOffset - layout.ViewportBase;
        Rendering.Layout.LayoutLine? ll = null;
        for (var i = layout.Lines.Count - 1; i >= 0; i--) {
            if (layout.Lines[i].CharStart <= localOfs) {
                ll = layout.Lines[i];
                break;
            }
        }
        if (ll == null || ll.Mono is not { } mono
                || current.RowInLine < 0
                || current.RowInLine >= mono.Rows.Length) {
            // Layout state drift — rebuild from offset after a plain step.
            var fallbackOfs = Math.Clamp(
                delta < 0 ? CodepointBoundary.StepLeft(table, current.CharOffset)
                          : CodepointBoundary.StepRight(table, current.CharOffset),
                0L, table.Length);
            return BuildCaretPos(fallbackOfs);
        }

        var row = current.RowInLine;
        var col = current.Col;
        var rowLen = mono.Rows[row].CharLen;
        var isLastRow = row == mono.Rows.Length - 1;
        var isFirstRow = row == 0;
        var rowStart = ll.CharStart + mono.Rows[row].CharStart;

        if (delta > 0) {
            if (col < rowLen) {
                var newOfs = Math.Clamp(
                    CodepointBoundary.StepRight(table, current.CharOffset),
                    0L, table.Length);
                var newCol = (int)(newOfs - layout.ViewportBase - rowStart);
                if (newCol < 0 || newCol > rowLen) {
                    return BuildCaretPos(newOfs);
                }
                return new TextPosition(current.LineIdx, row, newCol, newOfs);
            }
            if (!isLastRow) {
                // In-line flip: end of row r → start of row r+1, same offset.
                return new TextPosition(
                    current.LineIdx, row + 1, 0, current.CharOffset);
            }
            // End of last row → advance into next logical line.
            var crossOfs = Math.Clamp(
                CodepointBoundary.StepRight(table, current.CharOffset),
                0L, table.Length);
            return BuildCaretPos(crossOfs);
        } else {
            if (col > 0) {
                var newOfs = Math.Clamp(
                    CodepointBoundary.StepLeft(table, current.CharOffset),
                    0L, table.Length);
                var newCol = (int)(newOfs - layout.ViewportBase - rowStart);
                if (newCol < 0) {
                    return BuildCaretPos(newOfs);
                }
                return new TextPosition(current.LineIdx, row, newCol, newOfs);
            }
            if (!isFirstRow) {
                // In-line flip: start of row r → end of row r-1, same offset.
                var prevLen = mono.Rows[row - 1].CharLen;
                return new TextPosition(
                    current.LineIdx, row - 1, prevLen, current.CharOffset);
            }
            // Start of first row → retreat into previous logical line.
            var crossOfs = Math.Clamp(
                CodepointBoundary.StepLeft(table, current.CharOffset),
                0L, table.Length);
            return BuildCaretPos(crossOfs);
        }
    }

    /// <summary>
    /// CharWrap visual step.  In CharWrap, <see cref="TextPosition.LineIdx"/>
    /// is the absolute visual row (cwRow), and <see cref="TextPosition.RowInLine"/>
    /// is always 0 — each <see cref="Rendering.Layout.LayoutLine"/> represents
    /// exactly one visual row.
    /// </summary>
    private TextPosition StepCaretPosCharWrap(TextPosition current, int delta) {
        var table = Document!.Table;
        var cpr = _charWrapCharsPerRow;
        var absRow = current.LineIdx;
        var col = current.Col;

        if (delta > 0) {
            if (col < cpr) {
                var newOfs = Math.Clamp(
                    CodepointBoundary.StepRight(table, current.CharOffset),
                    0L, table.Length);
                var rowStart = absRow * cpr;
                var newCol = (int)(newOfs - rowStart);
                if (newCol < 0 || newCol > cpr) {
                    return BuildCaretPos(newOfs);
                }
                return new TextPosition(absRow, 0, newCol, newOfs);
            }
            // col == cpr: end-of-row; flip to start-of-next-row, same offset.
            return new TextPosition(absRow + 1, 0, 0, current.CharOffset);
        } else {
            if (col > 0) {
                var newOfs = Math.Clamp(
                    CodepointBoundary.StepLeft(table, current.CharOffset),
                    0L, table.Length);
                var rowStart = absRow * cpr;
                var newCol = (int)(newOfs - rowStart);
                if (newCol < 0) {
                    return BuildCaretPos(newOfs);
                }
                return new TextPosition(absRow, 0, newCol, newOfs);
            }
            if (absRow > 0) {
                // col == 0, prev row exists: flip to end of prev row, same offset.
                return new TextPosition(absRow - 1, 0, cpr, current.CharOffset);
            }
            // Start of doc — no movement.
            return current;
        }
    }

    /// <summary>
    /// Commits a new <see cref="TextPosition"/> as the caret.  Writes
    /// <c>_caretPosition</c> (canonical) and <see cref="Selection"/>; the
    /// derived <c>_caretIsAtEnd</c> property automatically reflects the
    /// new position.  No legacy-flag write needed.
    /// </summary>
    private void CommitCaretPos(TextPosition pos, bool extend) {
        var doc = Document!;
        _caretPosition = pos;
        doc.Selection = extend
            ? doc.Selection.ExtendTo(pos.CharOffset)
            : Selection.Collapsed(pos.CharOffset);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Commits a plain document offset as the caret.  Used by paths that
    /// don't reason visually (by-word movement, off-viewport fallback).
    /// Clears the cached visual position; the next CaretPosition read
    /// rebuilds from Selection.Active with downstream affinity.
    /// </summary>
    private void CommitPlainCaret(long ofs, bool extend) {
        var doc = Document!;
        _caretPosition = null;
        doc.Selection = extend
            ? doc.Selection.ExtendTo(ofs)
            : Selection.Collapsed(ofs);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

/// <summary>
    /// Caret pixel rect for offset <paramref name="caretOfs"/> in
    /// <paramref name="layout"/> coordinates.  Prefers the
    /// <see cref="TextPosition"/>-native engine path (no offset-walk,
    /// no affinity flag) when <see cref="CaretPosition"/> represents
    /// the same offset; falls back to the offset overload for column-
    /// mode multi-carets and other off-main-caret arrangements.
    /// </summary>
    private Rect GetCaretRect(Rendering.Layout.LayoutResult layout, long caretOfs) {
        if (CaretPosition is { } pos && pos.CharOffset == caretOfs) {
            return _layoutEngine.GetCaretBounds(pos, layout);
        }
        var localCaret = (int)(caretOfs - layout.ViewportBase);
        return _layoutEngine.GetCaretBounds(localCaret, layout, isAtEnd: false);
    }

    /// <summary>
    /// If <paramref name="ofs"/> falls inside a line terminator dead zone,
    /// snaps it to the nearest valid content position.
    /// </summary>
    private static long SnapOutOfDeadZone(PieceTable table, long ofs, bool forward) {
        if (ofs <= 0 || ofs >= table.Length) return ofs;
        var line = (int)table.LineFromOfs(ofs);
        var lineStart = table.LineStartOfs(line);
        var contentLen = table.LineContentLength(line);
        var contentEnd = lineStart + contentLen;

        if (ofs <= contentEnd) {
            // Within content — valid position.
            return ofs;
        }

        // Past content end: we're in the terminator region (LF, CR, CRLF) — snap out.
        if (forward) {
            return line + 1 < table.LineCount
                ? table.LineStartOfs(line + 1)
                : table.Length;
        } else {
            return contentEnd;
        }
    }

    /// <summary>
    /// Moves the caret up or down by one visual row.
    /// When the caret is already at the top or bottom edge of the viewport,
    /// the document scrolls by one row while the caret stays at the same
    /// screen position — matching the page-up/down pattern but at row scale.
    /// </summary>
    /// <remarks>
    /// Column preservation is the line-absolute visual column —
    /// row's leading indent (in chars) plus the in-row character
    /// position.  Continuation rows under hanging indent shift right,
    /// so preserving "visual col" (not raw <see cref="TextPosition.Col"/>)
    /// keeps the caret at the same X across rows with different indents.
    /// </remarks>
    private void MoveCaretVertical(TextDocument doc, int lineDelta, bool extend) {
        var layout = EnsureLayout();
        if (CaretPosition is null) return;
        var pos = CaretPosition.Value;

        // Single search: find source LayoutLine index by LineIdx.  All
        // subsequent navigation (target, edge re-find) walks ±1 in
        // layout.Lines from this index — no second search.
        var sourceIdx = FindLayoutLineIdx(layout, pos.LineIdx);
        if (sourceIdx < 0) return;
        var sourceLL = layout.Lines[sourceIdx];
        if (sourceLL.Mono is not { } sourceMono
                || pos.RowInLine < 0
                || pos.RowInLine >= sourceMono.Rows.Length) {
            return;
        }
        var sourceRowSpan = sourceMono.Rows[pos.RowInLine];

        var rh = layout.RowHeight;
        var sourceVisualRow = sourceLL.Row + pos.RowInLine;

        // Capture preferred line-absolute visual column on the first
        // vertical move (= row indent in chars + char-in-row).  This is
        // what gets preserved across rows of different indent depths.
        if (_preferredCaretCol < 0) {
            _preferredCaretCol = sourceRowSpan.IndentCols + pos.Col;
        }

        // Pixel-based edge detection (the only pixel math left).
        var sourceScreenY = sourceVisualRow * rh + RenderOffsetY;
        var atTopEdge = lineDelta < 0 && sourceScreenY < rh;
        var atBottomEdge = lineDelta > 0 && sourceScreenY + 2 * rh > _viewport.Height;

        if (atTopEdge || atBottomEdge) {
            // Scroll by one row, then re-find source by LineIdx in the
            // rebuilt layout — same LineIdx, possibly different index
            // in layout.Lines.  Navigation below proceeds identically.
            var scrollBefore = _scrollOffset.Y;
            ScrollValue += lineDelta * rh;
            _layout?.Dispose();
            _layout = null;
            layout = EnsureLayout();
            sourceIdx = FindLayoutLineIdx(layout, pos.LineIdx);
            if (sourceIdx < 0) return;
            sourceLL = layout.Lines[sourceIdx];

            // Apply scroll correction to land the caret at the precise
            // screen Y (avoids row-boundary snap).
            var scrollDelta = atBottomEdge
                ? sourceScreenY + 2 * rh - _viewport.Height
                : sourceScreenY - rh;
            ScrollValue = Math.Max(0, scrollBefore + scrollDelta);
        }

        // Direct ±1 navigation: target is either an in-line neighbor row
        // (same LayoutLine, RowInLine ± 1) or the next/previous
        // LayoutLine's first/last row.  No second search.
        Rendering.Layout.LayoutLine targetLL;
        int targetRowInLine;
        if (lineDelta > 0) {
            if (pos.RowInLine + 1 < sourceLL.HeightInRows) {
                targetLL = sourceLL;
                targetRowInLine = pos.RowInLine + 1;
            } else if (sourceIdx + 1 < layout.Lines.Count) {
                targetLL = layout.Lines[sourceIdx + 1];
                targetRowInLine = 0;
            } else {
                return; // already at last visible row
            }
        } else {
            if (pos.RowInLine > 0) {
                targetLL = sourceLL;
                targetRowInLine = pos.RowInLine - 1;
            } else if (sourceIdx > 0) {
                targetLL = layout.Lines[sourceIdx - 1];
                targetRowInLine = targetLL.HeightInRows - 1;
            } else {
                return; // already at first visible row
            }
        }

        if (targetLL.Mono is not { } targetMono
                || targetRowInLine < 0
                || targetRowInLine >= targetMono.Rows.Length) {
            return;
        }
        var targetRowSpan = targetMono.Rows[targetRowInLine];

        // Decompose preserved visual col into target row's char position:
        // visualCol - targetRowIndent, clamped to the target row's width.
        var targetCol = Math.Max(0, _preferredCaretCol - targetRowSpan.IndentCols);
        targetCol = Math.Min(targetCol, targetRowSpan.CharLen);

        var targetOffset = layout.ViewportBase
            + targetLL.CharStart + targetRowSpan.CharStart + targetCol;
        var targetLineIdx = doc.Table.LineFromOfs(targetOffset);
        var targetPos = new TextPosition(
            targetLineIdx, targetRowInLine, targetCol, targetOffset);

        // Commit.  Do NOT call ScrollCaretIntoView — the edge branch
        // already scrolled to keep the caret at the same screen Y.
        _caretPosition = targetPos;
        doc.Selection = extend
            ? doc.Selection.ExtendTo(targetOffset)
            : Selection.Collapsed(targetOffset);
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// Returns the index in <paramref name="layout"/>.Lines of the
    /// <see cref="Rendering.Layout.LayoutLine"/> whose
    /// <see cref="Rendering.Layout.LayoutLine.LineIdx"/> matches
    /// <paramref name="lineIdx"/>, or -1 if none.
    /// </summary>
    private static int FindLayoutLineIdx(
            Rendering.Layout.LayoutResult layout, long lineIdx) {
        for (var i = 0; i < layout.Lines.Count; i++) {
            if (layout.Lines[i].LineIdx == lineIdx) return i;
        }
        return -1;
    }

    private void MoveCaretToLineEdge(TextDocument doc, bool toStart, bool extend) {
        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var pos = CaretPosition;

        // CharWrap mode: row from pos.LineIdx (absolute visual row), no cascading.
        if (_charWrapMode && _charWrapCharsPerRow > 0 && pos is { } cwPos) {
            var cpr = _charWrapCharsPerRow;
            var docLen = table.Length;
            var cwAbsRow = cwPos.LineIdx;
            var rowStartOfs = cwAbsRow * cpr;
            var rowEndOfs = Math.Min(rowStartOfs + cpr, docLen);
            long targetOfs;
            int targetCol;
            if (toStart) {
                targetOfs = rowStartOfs;
                targetCol = 0;
            } else {
                targetOfs = rowEndOfs;
                targetCol = (int)(rowEndOfs - rowStartOfs);
            }
            var targetPos = new TextPosition(cwAbsRow, 0, targetCol, targetOfs);
            CommitCaretPos(targetPos, extend);
            return;
        }

        var lineIdx = (int)table.LineFromOfs(Math.Min(caret, table.Length));
        var lineStart = table.LineStartOfs(lineIdx);
        if (lineStart < 0) {
            return;
        }
        var lineContentLen = table.LineContentLength(lineIdx);
        var lineEnd = lineStart + lineContentLen;

        // Word-wrap path: row-aware via TextPosition.RowInLine.
        // Cascading:
        //   Home: row start → line start → smart-home (first-non-ws ↔ col 0)
        //   End:  row end   → line end
        if (_wrapLines && pos is { } wwPos) {
            var layout = EnsureLayout();
            var llIdx = FindLayoutLineIdx(layout, wwPos.LineIdx);
            if (llIdx >= 0
                    && layout.Lines[llIdx].Mono is { } mono
                    && wwPos.RowInLine >= 0
                    && wwPos.RowInLine < mono.Rows.Length) {
                var ll = layout.Lines[llIdx];
                var span = mono.Rows[wwPos.RowInLine];
                var rowStartOfs = layout.ViewportBase + ll.CharStart + span.CharStart;
                var rowEndOfs = rowStartOfs + span.CharLen;

                TextPosition newPos;
                if (toStart) {
                    long newCaret;
                    int newRow;
                    int newCol;
                    if (caret != rowStartOfs) {
                        // Cascade 1: row start.
                        newCaret = rowStartOfs;
                        newRow = wwPos.RowInLine;
                        newCol = 0;
                    } else if (caret != lineStart) {
                        // Cascade 2: line start.
                        newCaret = lineStart;
                        newRow = 0;
                        newCol = 0;
                    } else {
                        // Cascade 3: smart-home toggle.
                        var wsLen = LeadingWhitespaceLength(table.GetLine(lineIdx));
                        var firstNonWs = lineStart + wsLen;
                        newCaret = caret == firstNonWs ? lineStart : firstNonWs;
                        newRow = 0;
                        newCol = (int)(newCaret - lineStart);
                    }
                    newPos = new TextPosition(wwPos.LineIdx, newRow, newCol, newCaret);
                } else {
                    long newCaret;
                    int newRow;
                    int newCol;
                    if (caret != rowEndOfs) {
                        // Cascade 1: row end (upstream / end-of-row position).
                        newCaret = rowEndOfs;
                        newRow = wwPos.RowInLine;
                        newCol = span.CharLen;
                    } else {
                        // Cascade 2: line end (end of last row).
                        newCaret = lineEnd;
                        newRow = mono.Rows.Length - 1;
                        var lastSpan = mono.Rows[newRow];
                        newCol = (int)(lineEnd - layout.ViewportBase
                            - ll.CharStart - lastSpan.CharStart);
                    }
                    newPos = new TextPosition(wwPos.LineIdx, newRow, newCol, newCaret);
                }
                CommitCaretPos(newPos, extend);
                return;
            }
        }

        // Fallback: logical-line Home/End (wrap off, slow path, or caret
        // not in current layout window).  No row info available, so
        // commit as a plain offset (clears _caretPosition).
        long fallback;
        if (toStart) {
            var wsLen = LeadingWhitespaceLength(table.GetLine(lineIdx));
            var firstNonWs = lineStart + wsLen;
            fallback = caret == firstNonWs ? lineStart : firstNonWs;
        } else {
            fallback = lineEnd;
        }
        CommitPlainCaret(fallback, extend);
    }

    private static long FindWordBoundaryLeft(TextDocument doc, long caret) {
        if (caret == 0L) {
            return 0L;
        }
        // Use a 1 KB window around the caret — avoids materializing the full document.
        var windowStart = Math.Max(0L, caret - 1024);
        var windowLen = (int)(caret - windowStart);
        var buf = ArrayPool<char>.Shared.Rent(windowLen);
        try {
            CopyFromTable(doc.Table, windowStart, buf, windowLen);
            var text = buf.AsSpan(0, windowLen);
            var pos = windowLen; // position within the window
            // Skip whitespace going left, then skip non-whitespace
            while (pos > 0 && char.IsWhiteSpace(text[pos - 1])) {
                pos--;
            }
            while (pos > 0 && !char.IsWhiteSpace(text[pos - 1])) {
                pos--;
            }
            return windowStart + pos;
        } finally {
            ArrayPool<char>.Shared.Return(buf);
        }
    }

    private static long FindWordBoundaryRight(TextDocument doc, long caret) {
        var len = doc.Table.Length;
        if (caret >= len) {
            return len;
        }
        var windowLen = (int)Math.Min(1024L, len - caret);
        var buf = ArrayPool<char>.Shared.Rent(windowLen);
        try {
            CopyFromTable(doc.Table, caret, buf, windowLen);
            var text = buf.AsSpan(0, windowLen);
            var pos = 0;
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) {
                pos++;
            }
            while (pos < text.Length && !char.IsWhiteSpace(text[pos])) {
                pos++;
            }
            return caret + pos;
        } finally {
            ArrayPool<char>.Shared.Return(buf);
        }
    }

    // -------------------------------------------------------------------------
    // New editing command helpers
    // -------------------------------------------------------------------------

    private void PerformInsertLineBelow() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        _editSw.Restart();
        var nl = doc.LineEndingInfo.NewlineString;
        var lineIdx = doc.Table.LineFromOfs(doc.Selection.Caret);
        var indent = GetLeadingWhitespace(doc.Table.GetLine(lineIdx));
        if (lineIdx + 1 < doc.Table.LineCount) {
            var nextLineStart = doc.Table.LineStartOfs(lineIdx + 1);
            doc.Selection = Selection.Collapsed(nextLineStart);
            doc.Insert(indent + nl);
            doc.Selection = Selection.Collapsed(nextLineStart + indent.Length);
        } else {
            // Last line — append newline at end
            doc.Selection = Selection.Collapsed(doc.Table.Length);
            doc.Insert(nl + indent);
        }
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    private void PerformInsertLineAbove() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        _editSw.Restart();
        var nl = doc.LineEndingInfo.NewlineString;
        var lineIdx = doc.Table.LineFromOfs(doc.Selection.Caret);
        var indent = GetLeadingWhitespace(doc.Table.GetLine(lineIdx));
        var lineStart = doc.Table.LineStartOfs(lineIdx);
        doc.Selection = Selection.Collapsed(lineStart);
        doc.Insert(indent + nl);
        doc.Selection = Selection.Collapsed(lineStart + indent.Length);
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    private void PerformDuplicateLine() {
        var doc = Document;
        if (doc == null) return;
        FlushCompound();
        _editSw.Restart();
        var nl = doc.LineEndingInfo.NewlineString;
        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var lineIdx = table.LineFromOfs(caret);
        var lineStart = table.LineStartOfs(lineIdx);
        var caretCol = caret - lineStart;

        long lineEnd = lineIdx + 1 < table.LineCount
            ? table.LineStartOfs(lineIdx + 1)
            : table.Length;
        var lineLen = (int)(lineEnd - lineStart);
        string lineText;
        if (lineLen <= PieceTable.MaxGetTextLength) {
            lineText = table.GetText(lineStart, lineLen);
        } else {
            var sb = new StringBuilder(lineLen);
            table.ForEachPiece(lineStart, lineLen, span => sb.Append(span));
            lineText = sb.ToString();
        }

        // If the line doesn't end with a newline (last line), prepend one.
        if (lineEnd == table.Length && (lineText.Length == 0 || lineText[^1] != '\n')) {
            doc.BeginCompound();
            doc.Selection = Selection.Collapsed(table.Length);
            doc.Insert(nl + lineText);
            doc.EndCompound();
        } else {
            doc.Selection = Selection.Collapsed(lineEnd);
            doc.Insert(lineText);
        }
        // Place caret on the duplicated line at the same column offset.
        var nlLen = nl.Length;
        var newLineStart = lineEnd + (lineText.Length > 0 && lineText[^1] != '\n' ? nlLen : 0);
        doc.Selection = Selection.Collapsed(Math.Min(newLineStart + caretCol, table.Length));
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>
    /// Measures the indentation depth of a line in canonical units.
    /// Tabs count as <paramref name="tabSize"/> spaces each.
    /// </summary>
    private static int MeasureIndent(string lineText, int tabSize) {
        var depth = 0;
        foreach (var ch in lineText) {
            if (ch == ' ') depth++;
            else if (ch == '\t') depth += tabSize;
            else break;
        }
        return depth;
    }

    /// <summary>
    /// Builds the indentation string for a given depth using the document's
    /// dominant indent style.
    /// </summary>
    private static string BuildIndent(int depth, Core.Documents.IndentStyle style, int tabSize) {
        if (depth <= 0) return string.Empty;
        if (style == Core.Documents.IndentStyle.Tabs) {
            var tabs = depth / tabSize;
            var spaces = depth % tabSize;
            return new string('\t', tabs) + (spaces > 0 ? new string(' ', spaces) : "");
        }
        return new string(' ', depth);
    }

    /// <summary>
    /// When the caret is inside leading whitespace on a spaces-indent document,
    /// deletes back to the previous indent stop. Returns true if handled.
    /// </summary>
    private bool TrySmartDeindent(Core.Documents.TextDocument doc) {
        if (doc.IndentInfo.Dominant != IndentStyle.Spaces) return false;

        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var lineIdx = table.LineFromOfs(caret);
        var lineStart = table.LineStartOfs(lineIdx);
        var col = (int)(caret - lineStart); // character offset within line
        if (col == 0) return false; // at start of line — normal backspace deletes newline

        var lineText = table.GetLine(lineIdx);
        var wsLen = LeadingWhitespaceLength(lineText);
        if (col > wsLen) return false; // caret is past leading whitespace

        // All characters before the caret in this line must be spaces
        // (mixed tabs would be ambiguous — fall through to normal backspace).
        for (var i = 0; i < col; i++) {
            if (lineText[i] != ' ') return false;
        }

        // Snap back to the previous indent stop.
        var prevStop = ((col - 1) / _indentWidth) * _indentWidth;
        var deleteCount = col - prevStop;
        doc.Selection = new Selection(lineStart + prevStop, lineStart + col);
        doc.DeleteSelection();
        return true;
    }

    /// <summary>
    /// Returns the number of leading whitespace characters in the line text.
    /// </summary>
    private static int LeadingWhitespaceLength(string lineText) {
        var i = 0;
        while (i < lineText.Length && (lineText[i] == ' ' || lineText[i] == '\t')) i++;
        return i;
    }

    private static string GetLeadingWhitespace(string lineText) {
        var len = LeadingWhitespaceLength(lineText);
        return len > 0 ? lineText[..len] : string.Empty;
    }

    /// <summary>
    /// Finds the indentation depth (in spaces) of the nearest non-empty line
    /// above <paramref name="lineIdx"/>.
    /// </summary>
    private static int FindPrevIndent(Core.Documents.PieceTable table, long lineIdx, int tabSize) {
        for (var i = lineIdx - 1; i >= 0; i--) {
            var text = table.GetLine(i);
            if (!string.IsNullOrWhiteSpace(text))
                return MeasureIndent(text, tabSize);
        }
        return 0;
    }

    /// <summary>
    /// Replaces the leading whitespace of a single line to achieve
    /// <paramref name="targetDepth"/>. No-op if already at that depth.
    /// </summary>
    private static void SetLineIndent(
        Core.Documents.TextDocument doc, Core.Documents.PieceTable table,
        long lineIdx, string lineText, int targetDepth,
        Core.Documents.IndentStyle style, int tabSize) {
        var currentDepth = MeasureIndent(lineText, tabSize);
        if (targetDepth == currentDepth) return;
        var newIndent = BuildIndent(targetDepth, style, tabSize);
        var wsLen = LeadingWhitespaceLength(lineText);
        var lineStart = table.LineStartOfs(lineIdx);
        if (wsLen > 0 && newIndent.Length == 0) {
            doc.Selection = new Selection(lineStart, lineStart + wsLen);
            doc.DeleteSelection();
        } else if (wsLen > 0) {
            doc.Selection = new Selection(lineStart, lineStart + wsLen);
            doc.Insert(newIndent);
        } else {
            doc.Selection = Selection.Collapsed(lineStart);
            doc.Insert(newIndent);
        }
    }

    private void PerformSmartIndent() {
        var doc = Document;
        if (doc == null) return;
        _editSw.Restart();
        var table = doc.Table;
        var sel = doc.Selection;
        var style = doc.IndentInfo.Dominant;
        var tabSize = _indentWidth;

        var startLine = table.LineFromOfs(sel.Start);
        var endLine = table.LineFromOfs(Math.Max(sel.Start, sel.End - 1));

        if (sel.IsEmpty || startLine == endLine) {
            // Single line: stateless smart indent.
            // Candidates: {prevDepth - tabSize, prevDepth, prevDepth + tabSize},
            // clamped to >= 0, deduplicated, sorted ascending.
            // Current depth picks the next candidate up; wraps to smallest.
            var lineText = table.GetLine(startLine);
            var currentDepth = MeasureIndent(lineText, tabSize);
            var prevDepth = FindPrevIndent(table, startLine, tabSize);

            var candidates = new SortedSet<int> {
                Math.Max(0, prevDepth - tabSize),
                prevDepth,
                prevDepth + tabSize,
            };
            var sorted = candidates.ToList();

            // Pick the next candidate strictly above currentDepth; wrap to first.
            var targetDepth = sorted.FirstOrDefault(d => d > currentDepth, sorted[0]);

            if (targetDepth != currentDepth) {
                var newIndent = BuildIndent(targetDepth, style, tabSize);
                var wsLen = LeadingWhitespaceLength(lineText);
                var lineStart = table.LineStartOfs(startLine);
                if (wsLen > 0 && newIndent.Length == 0) {
                    // Removing all indentation: just delete the whitespace.
                    doc.Selection = new Selection(lineStart, lineStart + wsLen);
                    doc.DeleteSelection();
                } else if (wsLen > 0) {
                    // Replacing existing whitespace with different whitespace.
                    doc.Selection = new Selection(lineStart, lineStart + wsLen);
                    doc.Insert(newIndent);
                } else {
                    // Adding indentation to an unindented line.
                    doc.Selection = Selection.Collapsed(lineStart);
                    doc.Insert(newIndent);
                }
            }
        } else {
            // Multi-line: set each line's indent to one level more than the
            // line above the selection. This is the smart indent interpretation
            // for documents without block structure awareness.
            var refDepth = FindPrevIndent(table, startLine, tabSize);
            var targetDepth = refDepth + tabSize;
            doc.BeginCompound();
            for (var line = startLine; line <= endLine; line++) {
                var lineText = table.GetLine(line);
                SetLineIndent(doc, table, line, lineText, targetDepth, style, tabSize);
            }
            doc.EndCompound();
            var rangeStart = table.LineStartOfs(startLine);
            var rangeEnd = endLine + 1 < table.LineCount
                ? table.LineStartOfs(endLine + 1)
                : table.Length;
            doc.Selection = new Selection(rangeStart, rangeEnd);
        }
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>
    /// Adds one indent level to the current line or all selected lines.
    /// </summary>
    private void PerformSimpleIndent() {
        var doc = Document;
        if (doc == null) return;
        if (_charWrapMode) return; // indent not supported in char-wrap mode
        _editSw.Restart();
        var table = doc.Table;
        var sel = doc.Selection;
        var style = doc.IndentInfo.Dominant;
        var tabSize = _indentWidth;

        var startLine = table.LineFromOfs(sel.Start);
        var endLine = table.LineFromOfs(Math.Max(sel.Start, sel.End - 1));

        doc.BeginCompound();
        for (var line = startLine; line <= endLine; line++) {
            var lineText = table.GetLine(line);
            var currentDepth = MeasureIndent(lineText, tabSize);
            var targetDepth = currentDepth + tabSize;
            SetLineIndent(doc, table, line, lineText, targetDepth, style, tabSize);
        }
        doc.EndCompound();

        if (!sel.IsEmpty && startLine != endLine) {
            var rangeStart = table.LineStartOfs(startLine);
            var rangeEnd = endLine + 1 < table.LineCount
                ? table.LineStartOfs(endLine + 1)
                : table.Length;
            doc.Selection = new Selection(rangeStart, rangeEnd);
        }

        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }

    /// <summary>
    /// Removes one indent level from the current line or all selected lines.
    /// </summary>
    private void PerformOutdent() {
        var doc = Document;
        if (doc == null) return;
        if (_charWrapMode) return; // outdent not supported in char-wrap mode
        _editSw.Restart();
        var table = doc.Table;
        var sel = doc.Selection;
        var style = doc.IndentInfo.Dominant;
        var tabSize = _indentWidth;

        var startLine = table.LineFromOfs(sel.Start);
        var endLine = table.LineFromOfs(Math.Max(sel.Start, sel.End - 1));

        doc.BeginCompound();
        for (var line = startLine; line <= endLine; line++) {
            var lineText = table.GetLine(line);
            var currentDepth = MeasureIndent(lineText, tabSize);
            if (currentDepth <= 0) continue;
            var targetDepth = Math.Max(0, currentDepth - tabSize);
            var newIndent = BuildIndent(targetDepth, style, tabSize);
            var wsLen = LeadingWhitespaceLength(lineText);
            var lineStart = table.LineStartOfs(line);
            if (newIndent.Length == 0) {
                doc.Selection = new Selection(lineStart, lineStart + wsLen);
                doc.DeleteSelection();
            } else {
                doc.Selection = new Selection(lineStart, lineStart + wsLen);
                doc.Insert(newIndent);
            }
        }
        doc.EndCompound();

        if (startLine == endLine) {
            // Single line: place caret at end of new indentation.
            var newText = table.GetLine(startLine);
            var newWs = LeadingWhitespaceLength(newText);
            doc.Selection = Selection.Collapsed(table.LineStartOfs(startLine) + newWs);
        } else {
            // Re-select the full line range.
            var rangeStart = table.LineStartOfs(startLine);
            var rangeEnd = endLine + 1 < table.LineCount
                ? table.LineStartOfs(endLine + 1)
                : table.Length;
            doc.Selection = new Selection(rangeStart, rangeEnd);
        }
        ScrollCaretIntoView();
        _editSw.Stop();
        PerfStats.Edit.Record(_editSw.Elapsed.TotalMilliseconds);
        InvalidateLayout();
        ResetCaretBlink();
    }
}
