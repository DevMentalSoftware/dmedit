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


    private void PerformColumnSelectVertical(Document doc, int delta) {
        if (_wrapLines) return;
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

    private void PerformColumnSelectHorizontal(Document doc, int delta) {
        if (_wrapLines) return;
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
    private void PerformColumnMoveHorizontal(Document doc, int delta) {
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
    private void PerformColumnMoveVertical(Document doc, int delta) {
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
    private void PerformColumnMoveWord(Document doc, int direction) {
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
    private void PerformColumnSelectWord(Document doc, int direction) {
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
    private int MaxEndColumn(Document doc, ColumnSelection colSel) {
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
        var columnIntercepts = new Dictionary<Command, Func<Document, bool>>();

        void ColIntercept(Command cmd, Func<Document, bool> handler) =>
            columnIntercepts[cmd] = handler;

        // Local helper: wraps each editor command with the standard preamble.
        void Reg(Command cmd, Action<Document> action,
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
                if (!isVerticalNav) _preferredCaretX = -1;

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

    private void MoveCaretHorizontal(Document doc, int delta, bool byWord, bool extend) {
        var caret = doc.Selection.Caret;
        var table = doc.Table;
        var len = table.Length;

        // At a soft-break boundary, two visual positions share one offset:
        //   isAtEnd=true  → end of current row (right edge)
        //   isAtEnd=false → start of next row (left edge)
        //
        // Arrow keys visit BOTH positions when crossing a boundary:
        //
        //   Right: ...lastChar → endOfRow(isAtEnd) → startOfNextRow → nextChar...
        //   Left:  ...secondChar → startOfRow → endOfPrevRow(isAtEnd) → prevChar...
        //
        // When the caret is already at one of these two positions, the
        // next arrow press in the same direction just flips isAtEnd
        // without changing the offset.
        if (!byWord && (_wrapLines || _charWrapMode)) {
            // Right from isAtEnd at any boundary: flip to !isAtEnd.
            // This handles End-then-right (word-wrap) and arrow traversal
            // through hard breaks (CharWrap / forced mid-word breaks).
            if (delta > 0 && _caretIsAtEnd && IsAtSoftRowBoundary(caret)) {
                _caretIsAtEnd = false;
                if (!extend) doc.Selection = Selection.Collapsed(caret);
                InvalidateVisual();
                ResetCaretBlink();
                return;
            }
            // Left from !isAtEnd at a hard break: flip to isAtEnd.
            // Only for hard breaks — at space-breaks, left arrow advances
            // through the space character naturally.
            if (delta < 0 && !_caretIsAtEnd && IsAtHardBreakBoundary(caret, table)) {
                _caretIsAtEnd = true;
                if (!extend) doc.Selection = Selection.Collapsed(caret);
                InvalidateVisual();
                ResetCaretBlink();
                return;
            }
        }

        long newCaret;
        if (!byWord) {
            newCaret = delta < 0
                ? CodepointBoundary.StepLeft(table, caret)
                : CodepointBoundary.StepRight(table, caret);
            newCaret = Math.Clamp(newCaret, 0L, len);
        } else {
            newCaret = delta < 0
                ? FindWordBoundaryLeft(doc, caret)
                : FindWordBoundaryRight(doc, caret);
        }

        // Skip over dead zones (line terminators) — but not in char-wrap
        // mode where CR/LF are visible characters.
        if (!_charWrapMode) {
            newCaret = SnapOutOfDeadZone(table, newCaret, delta > 0);
        }

        // Right arrow landing at a hard-break boundary → isAtEnd.
        if (delta > 0 && (_wrapLines || _charWrapMode)
                && IsAtHardBreakBoundary(newCaret, table)) {
            _caretIsAtEnd = true;
        } else {
            _caretIsAtEnd = false;
        }

        doc.Selection = extend
            ? doc.Selection.ExtendTo(newCaret)
            : Selection.Collapsed(newCaret);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    /// <summary>
    /// <summary>
    /// Returns true if <paramref name="ofs"/> is at a hard row boundary —
    /// a break with no consumed space (prevEnd == curStart).  CharWrap
    /// boundaries are always hard.  Word-wrap breaks that consumed a
    /// space are NOT hard boundaries.
    /// </summary>
    private bool IsAtHardRowBoundary(long ofs) {
        if (ofs <= 0) return false;

        // CharWrap: every cpr boundary is a hard break.
        if (_charWrapMode && _charWrapCharsPerRow > 0) {
            return ofs % _charWrapCharsPerRow == 0 && ofs < Document!.Table.Length;
        }

        if (!_wrapLines) return false;
        var layout = EnsureLayout();
        if (layout.Lines.Count == 0) return false;
        var localOfs = (int)(ofs - layout.ViewportBase);
        if (localOfs < 0 || localOfs > layout.Lines[^1].CharEnd) return false;

        for (var i = layout.Lines.Count - 1; i >= 0; i--) {
            if (layout.Lines[i].CharStart <= localOfs) {
                var ll = layout.Lines[i];
                if (ll.Mono is not { } mono || mono.Rows.Length <= 1) return false;
                var posInLine = localOfs - ll.CharStart;
                var r = mono.RowForChar(posInLine);
                if (r == 0) return false;
                var prevEnd = mono.Rows[r - 1].CharStart + mono.Rows[r - 1].CharLen;
                var curStart = mono.Rows[r].CharStart;
                // Hard break: no gap between previous row's drawn content
                // and next row's start.
                return prevEnd == curStart && posInLine == curStart;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="ofs"/> is at a hard row boundary —
    /// a break between two non-space characters (or a CharWrap break).
    /// Space-break boundaries are NOT hard: the space is a navigable
    /// character on the current row, so arrow keys cross the boundary
    /// by advancing through the space naturally.
    /// </summary>
    private bool IsAtHardBreakBoundary(long ofs, PieceTable table) {
        if (ofs <= 0) return false;

        // CharWrap: every cpr boundary is a hard break.
        if (_charWrapMode && _charWrapCharsPerRow > 0) {
            return ofs % _charWrapCharsPerRow == 0 && ofs < table.Length;
        }

        if (!_wrapLines) return false;

        // Check IsAtSoftRowBoundary first (is this even a boundary?).
        if (!IsAtSoftRowBoundary(ofs)) return false;

        // It's a boundary. Check if the character just before it is a
        // space — if so, it's a space-break, not a hard break.
        var charBefore = ofs - 1;
        if (charBefore >= 0 && charBefore < table.Length) {
            var text = table.GetText(charBefore, 1);
            if (text.Length > 0 && text[0] == ' ') return false;
        }
        return true;
    }

    /// Returns true if the caret at <paramref name="ofs"/> is at a soft
    /// row boundary — a position where <c>isAtEnd</c> makes a visual
    /// difference (end of previous row vs start of next row).
    ///
    /// Covers both hard breaks (offset == Rows[r].CharStart) and word-
    /// wrap breaks where the consumed space sits before CharStart
    /// (offset &lt; Rows[r].CharStart but RowForChar still returns r).
    ///
    /// Used by both left-arrow (flip to isAtEnd) and right-arrow (set
    /// isAtEnd on arrival).
    /// </summary>
    private bool IsAtSoftRowBoundary(long ofs) {
        if (ofs <= 0) return false;

        if (_charWrapMode && _charWrapCharsPerRow > 0) {
            return ofs % _charWrapCharsPerRow == 0 && ofs < Document!.Table.Length;
        }

        if (!_wrapLines) return false;
        var layout = EnsureLayout();
        if (layout.Lines.Count == 0) return false;
        var localOfs = (int)(ofs - layout.ViewportBase);
        if (localOfs < 0 || localOfs > layout.Lines[^1].CharEnd) return false;

        for (var i = layout.Lines.Count - 1; i >= 0; i--) {
            if (layout.Lines[i].CharStart <= localOfs) {
                var ll = layout.Lines[i];
                if (ll.Mono is not { } mono || mono.Rows.Length <= 1) return false;
                var posInLine = localOfs - ll.CharStart;
                var r = mono.RowForChar(posInLine);
                if (r == 0) return false;
                // A position is at a soft boundary when RowForChar
                // assigns it to row r but it's at or before that row's
                // first drawn char.  This covers both hard breaks
                // (pos == CharStart) and consumed word-break spaces
                // (pos < CharStart).
                return posInLine <= mono.Rows[r].CharStart;
            }
        }
        return false;
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
    private void MoveCaretVertical(Document doc, int lineDelta, bool extend) {
        var layout = EnsureLayout();
        var localCaret = (int)(doc.Selection.Caret - layout.ViewportBase);
        var totalChars = layout.Lines.Count > 0 ? layout.Lines[^1].CharEnd : 0;

        // If the caret is outside the visible window, skip the movement.
        if (localCaret < 0 || localCaret > totalChars) {
            return;
        }

        var rh = layout.RowHeight;
        var caretRect = _layoutEngine.GetCaretBounds(localCaret, layout, _caretIsAtEnd);

        // On the first vertical move, capture the caret's current VISUAL
        // X as the "preferred" column.  Subsequent vertical moves reuse
        // this so the caret returns to the original column after
        // traversing short lines.  When isAtEnd, the visual X is at the
        // right edge of the row — use that so "End → Down" lands at
        // the end of the next row (or as far right as it goes).
        if (_preferredCaretX < 0) {
            _preferredCaretX = caretRect.X;
        }

        // Pixel-based edge detection: check whether the next row would be
        // fully visible.  This avoids rounding-dependent off-by-ones that
        // the integer screen-row check was susceptible to.
        var caretScreenY = caretRect.Y + RenderOffsetY;
        var atTopEdge = lineDelta < 0 && caretScreenY < rh;
        var atBottomEdge = lineDelta > 0 && caretScreenY + 2 * rh > _viewport.Height;

        if (atTopEdge || atBottomEdge) {
            // Capture the scroll before mutation so we can compute the
            // precise delta for smooth scrollbar tracking.
            var scrollBefore = _scrollOffset.Y;

            // Find the target caret position via the proven hit-test
            // approach: scroll by rh to bring the target row into the
            // layout window, then hit-test at the same screen row.
            var caretScreenRow = GetCaretScreenRow(caretRect, rh);
            ScrollValue += lineDelta * rh;
            _layout?.Dispose();
            _layout = null;
            var tempLayout = EnsureLayout();
            var newCaret = HitTestAtScreenRow(caretScreenRow, rh, tempLayout);
            doc.Selection = extend
                ? doc.Selection.ExtendTo(newCaret)
                : Selection.Collapsed(newCaret);

            // Correct the scroll to the precise minimum delta.  The first
            // ScrollValue += rh was needed for the hit-test; now set the
            // scroll to the exact target via the setter so the incremental
            // cache (_winScrollOffset, _winRenderOffsetY) stays consistent.
            // Writing to _scrollOffset directly left _winRenderOffsetY
            // stale (from the full-rh temporary layout), causing the
            // content to snap to a row boundary instead of showing a
            // partial top row.
            var scrollDelta = atBottomEdge
                ? caretScreenY + 2 * rh - _viewport.Height
                : caretScreenY - rh;
            ScrollValue = Math.Max(0, scrollBefore + scrollDelta);
        } else {
            // Normal movement within the viewport — move the caret one
            // visual row.  Use rh (not caretRect.Height) as the step so
            // wrapped lines advance one visual row at a time.
            var targetY = caretRect.Y + rh / 2 + lineDelta * rh;
            var localNewCaret = _layoutEngine.HitTest(
                new Point(_preferredCaretX >= 0 ? _preferredCaretX : 0, targetY), layout);
            var newCaret = layout.ViewportBase + localNewCaret;
            doc.Selection = extend
                ? doc.Selection.ExtendTo(newCaret)
                : Selection.Collapsed(newCaret);
        }

        // If vertical movement landed at a row boundary, set isAtEnd
        // only if HitTest clamped the position because the target row
        // was too short.  We detect this by checking whether the landed
        // position is at the END of its row (col == CharLen) as opposed
        // to a real character position within the row.
        // CharWrapMode: all rows are the same length, so vertical movement
        // simply preserves the current isAtEnd state.
        //
        // Word-wrap mode: rows have variable lengths.  If the target row
        // is shorter than the preferred X, HitTest clamps to the row
        // boundary and we need isAtEnd=true so the caret renders at the
        // end of that shorter row.  If the target row is long enough,
        // isAtEnd=false.
        var landedCaret = doc.Selection.Caret;
        if (_charWrapMode) {
            // Preserve current isAtEnd — all rows are the same width.
        } else if (_wrapLines && IsAtSoftRowBoundary(landedCaret)) {
            var landedLocal = (int)(landedCaret - layout.ViewportBase);
            var llIdx2 = layout.Lines.Count - 1;
            for (var i = layout.Lines.Count - 1; i >= 0; i--) {
                if (layout.Lines[i].CharStart <= landedLocal) {
                    llIdx2 = i; break;
                }
            }
            var ll2 = layout.Lines[llIdx2];
            if (ll2.Mono is { } mono2) {
                var posInLine = landedLocal - ll2.CharStart;
                var r = mono2.RowForChar(posInLine);
                if (r > 0) {
                    var prevSpan = mono2.Rows[r - 1];
                    var prevRowEndX = prevSpan.XOffset + prevSpan.CharLen * GetCharWidth();
                    _caretIsAtEnd = _preferredCaretX >= prevRowEndX - GetCharWidth() / 2;
                } else {
                    _caretIsAtEnd = false;
                }
            } else {
                _caretIsAtEnd = false;
            }
        } else {
            _caretIsAtEnd = false;
        }

        // Do NOT call ScrollCaretIntoView here — both branches above already
        // handle visibility correctly at row granularity (the top/bottom-edge
        // branch slides by exactly one row, the in-viewport branch doesn't
        // need to scroll at all).  Adding a generic ScrollCaretIntoView call
        // on top of that is what caused "click top row, arrow down, scroll
        // jumps up by one row" — the generic path can't tell that the move
        // is intentionally to the edge and treats the caret as needing to
        // be scrolled *further* into view.
        InvalidateVisual();
        ResetCaretBlink();
    }

    private void MoveCaretToLineEdge(Document doc, bool toStart, bool extend) {
        if (_charWrapMode && _charWrapCharsPerRow > 0) {
            // Compute row start/end from buf-space character offset.
            var caretOfs = doc.Selection.Caret;
            var cpr = _charWrapCharsPerRow;
            var docLen = doc.Table.Length;
            var currentRow = caretOfs / cpr;
            // Left affinity: caret visually sits on the previous row.
            if (_caretIsAtEnd && caretOfs > 0 && caretOfs % cpr == 0) {
                currentRow--;
            }
            var rowStart = currentRow * cpr;
            // End = position after the last char of this row.
            var rowEnd = Math.Min(rowStart + cpr, docLen);
            var target = toStart ? rowStart : rowEnd;
            // End → left affinity (park at right edge of row).
            // Home → right affinity (park at left edge of row).
            _caretIsAtEnd = !toStart;
            doc.Selection = extend
                ? doc.Selection.ExtendTo(target)
                : Selection.Collapsed(target);
            ScrollCaretIntoView();
            InvalidateVisual();
            ResetCaretBlink();
            return;
        }

        var table = doc.Table;
        var caret = doc.Selection.Caret;
        var lineIdx = (int)table.LineFromOfs(Math.Min(caret, table.Length));
        var lineStart = table.LineStartOfs(lineIdx);
        if (lineStart < 0) return;
        var lineContentLen = table.LineContentLength(lineIdx);
        var lineEnd = lineStart + lineContentLen;

        // Row-aware navigation: when wrap is on and the caret's line is
        // in the current layout, use the actual row break positions from
        // the layout engine.  Cascading behaviour:
        //
        // Home: row start → line start → smart-home (first-non-ws ↔ col 0)
        // End:  row end   → line end
        //
        // When wrap is off, or the line uses proportional layout, or the
        // caret isn't in the current layout, fall through to the logical-
        // line path below.
        if (_wrapLines && !_charWrapMode) {
            var layout = EnsureLayout();
            var localCaret = (int)(caret - layout.ViewportBase);
            if (layout.Lines.Count > 0
                    && localCaret >= 0
                    && localCaret <= layout.Lines[^1].CharEnd) {
                // Find the LayoutLine containing the caret.
                var llIdx = layout.Lines.Count - 1;
                for (var i = layout.Lines.Count - 1; i >= 0; i--) {
                    if (layout.Lines[i].CharStart <= localCaret) {
                        llIdx = i;
                        break;
                    }
                }
                var ll = layout.Lines[llIdx];

                if (ll.IsMono && ll.Mono is { } mono) {
                    var posInLine = Math.Max(0, localCaret - ll.CharStart);
                    var rowIdx = mono.RowForChar(posInLine);
                    // Left affinity: the caret visually sits on the
                    // previous row (at its right edge).  Adjust the row
                    // index so Home/End operate on the visual row.
                    if (_caretIsAtEnd && rowIdx > 0
                            && posInLine <= mono.Rows[rowIdx].CharStart) {
                        rowIdx--;
                    }
                    var span = mono.Rows[rowIdx];

                    var absLineStart = layout.ViewportBase + ll.CharStart;
                    var absRowStart = absLineStart + span.CharStart;
                    // The position between this row and the next — both
                    // the end of this row (isAtEnd=true) and the start
                    // of the next row (isAtEnd=false).
                    var rowBoundary = absLineStart + span.CharStart + span.CharLen;

                    long newCaret;
                    if (toStart) {
                        _caretIsAtEnd = false;
                        if (caret != absRowStart) {
                            newCaret = absRowStart;
                        } else if (caret != lineStart) {
                            newCaret = lineStart;
                        } else {
                            // Smart home: toggle first-non-ws ↔ line start.
                            var wsLen = LeadingWhitespaceLength(table.GetLine(lineIdx));
                            var firstNonWs = lineStart + wsLen;
                            newCaret = caret == firstNonWs ? lineStart : firstNonWs;
                        }
                    } else {
                        if (caret != rowBoundary) {
                            _caretIsAtEnd = true;
                            newCaret = rowBoundary;
                        } else {
                            _caretIsAtEnd = false;
                            newCaret = lineEnd;
                        }
                    }

                    doc.Selection = extend
                        ? doc.Selection.ExtendTo(newCaret)
                        : Selection.Collapsed(newCaret);
                    ScrollCaretIntoView();
                    InvalidateVisual();
                    ResetCaretBlink();
                    return;
                }
            }
        }

        // Fallback: logical line (wrap off, proportional font, or caret
        // not in current layout window).
        _caretIsAtEnd = false; // logical-line Home/End: no soft break
        long fallbackCaret;
        if (toStart) {
            // Smart Home: toggle between first non-whitespace and column 0.
            var wsLen = LeadingWhitespaceLength(table.GetLine(lineIdx));
            var firstNonWs = lineStart + wsLen;
            fallbackCaret = caret == firstNonWs ? lineStart : firstNonWs;
        } else {
            fallbackCaret = lineEnd;
        }
        doc.Selection = extend
            ? doc.Selection.ExtendTo(fallbackCaret)
            : Selection.Collapsed(fallbackCaret);
        ScrollCaretIntoView();
        InvalidateVisual();
        ResetCaretBlink();
    }

    private static long FindWordBoundaryLeft(Document doc, long caret) {
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

    private static long FindWordBoundaryRight(Document doc, long caret) {
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
    private bool TrySmartDeindent(Core.Documents.Document doc) {
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
        Core.Documents.Document doc, Core.Documents.PieceTable table,
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
