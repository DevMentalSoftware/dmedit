using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace DMEdit.App;

// Keyboard input + chord dispatch + menu access-key partial of
// MainWindow.  OnKeyDown / OnKeyUp, DispatchCommand, chord first/
// second-key state machine, Alt-menu activation on KeyUp, and the
// "is this a plain typing key?" helpers.
public partial class MainWindow {


    protected override void OnKeyDown(KeyEventArgs e) {
        // Bare Alt: prevent Avalonia's built-in menu activation by marking
        // handled BEFORE base runs. Menu activation is deferred to our
        // OnKeyUp handler (standard Windows behavior).
        if (e.Key is Key.LeftAlt or Key.RightAlt) {
            _altPressedClean = true;
            _menuAccessKeyActive = false;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
        // Mark Alt as consumed when used as a modifier for another key.
        if (_altPressedClean && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) {
            _altPressedClean = false;
        }
        // Ignore other bare modifier keys.
        if (e.Key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LWin or Key.RWin) {
            return;
        }

        // Menu is open via access key — intercept letters (plain or
        // Alt+letter) to activate submenu items before command dispatch
        // can steal them.  Also handles Escape to dismiss.
        if (_menuAccessKeyActive || MenuBar.IsKeyboardFocusWithin) {
            // Check if a submenu is actually still open.
            bool menuOpen = MenuBar.Items.OfType<MenuItem>()
                .Any(m => m.IsSubMenuOpen);
            if (!menuOpen && !MenuBar.IsKeyboardFocusWithin) {
                _menuAccessKeyActive = false;
            } else {
                if (e.Key == Key.Escape) {
                    MenuBar.Close();
                    _menuAccessKeyActive = false;
                    if (_activeTab is not { IsSettings: true }) {
                        Editor.Focus();
                    }
                    e.Handled = true;
                    return;
                }
                // Plain letter or Alt+letter → activate the matching
                // access key item in the open submenu.
                if (e.KeyModifiers is KeyModifiers.None or KeyModifiers.Alt
                    && TryActivateSubmenuAccessKey(e.Key)) {
                    e.Handled = true;
                    return;
                }
                // Other keys (arrows, Enter, etc.): let menu handle them.
                return;
            }
        }

        // Incremental search mode: intercept keys before normal dispatch.
        if (Editor.InIncrementalSearch) {
            if (e.Key == Key.Escape) {
                Editor.ExitIncrementalSearch();
                e.Handled = true;
                return;
            }
            if (HasCommandModifier(e) || IsNonTextKey(e.Key)) {
                // Exit isearch, then fall through to process the key normally.
                Editor.ExitIncrementalSearch();
            } else {
                // Plain character key — return without handling so that
                // OnTextInput fires and our interception there picks it up.
                return;
            }
        }

        // Column selection mode: Escape exits back to normal editing.
        if (e.Key == Key.Escape && Editor.Document?.ColumnSel != null) {
            Editor.Document.ClearColumnSelection(Editor.IndentWidth);
            Editor.InvalidateVisual();
            Editor.ResetCaretBlink();
            e.Handled = true;
            return;
        }

        // Chord: waiting for the second key of a two-keystroke chord?
        if (_chordFirst != null) {
            _chordTimer.Stop();
            var chordCmd = _keyBindings.ResolveChord(_chordFirst, e.Key, e.KeyModifiers);
            _chordFirst = null;
            StatusLeft.Text = "";
            if (chordCmd != null) {
                if (DispatchCommand(chordCmd)) {
                    e.Handled = true;
                }
                return;
            }
            // Second key didn't complete a chord — fall through to process
            // it as a normal single-key gesture below.
        }

        // Check if this key is the first key of a chord.
        if (_keyBindings.IsChordPrefix(e.Key, e.KeyModifiers)) {
            _chordFirst = new KeyGesture(e.Key, e.KeyModifiers);
            StatusLeft.Text = $"{_chordFirst} was pressed. Waiting for second key of chord\u2026";
            _chordTimer.Stop();
            _chordTimer.Start();
            e.Handled = true;
            return;
        }

        // Normal single-key resolution.
        var commandId = _keyBindings.Resolve(e.Key, e.KeyModifiers);
        if (commandId == null) {
            // Alt+letter with no bound command → try menu access keys.
            if (e.KeyModifiers == KeyModifiers.Alt) {
                TryOpenMenuAccessKey(e);
            }
            return;
        }

        // Menu.* pseudo-commands represent menu access keys (Alt+F, etc.).
        // Open the corresponding menu instead of dispatching as a command.
        if (commandId.StartsWith("Menu.", StringComparison.Ordinal)) {
            TryOpenMenuAccessKey(e);
            return;
        }

        if (DispatchCommand(commandId)) {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Dispatches a command through the registry, applying editor guards for
    /// commands that require the editor to be active and focused.
    /// </summary>
    private bool DispatchCommand(string commandId) {
        var cmd = Cmd.TryGet(commandId);
        if (cmd == null) return false;
        if (!cmd.IsEnabled) return false;
        if (_activeTab is { IsSettings: true }) {
            // On the settings page, only File, Window, Menu, and
            // Nav.FocusEditor commands are allowed.
            if (cmd.Category is not ("File" or "Window" or "Menu")
                && cmd != Cmd.NavFocusEditor) {
                return false;
            }
        } else if (cmd.RequiresEditor) {
            if (FindBar.IsVisible && FindBar.IsKeyboardFocusWithin) return false;
        }
        return cmd.Run();
    }

    protected override void OnKeyUp(KeyEventArgs e) {
        // Alt release: handle BEFORE base to suppress Avalonia's built-in
        // menu activation. Only activate menu if Alt was pressed and released
        // alone (not used as a modifier for column editing or shortcuts).
        if (e.Key is Key.LeftAlt or Key.RightAlt) {
            var wantMenu = _altPressedClean && Editor.Document?.ColumnSel == null;
            _altPressedClean = false;
            e.Handled = true;
            if (wantMenu) {
                MenuBar.Focus();
            } else if (_menuAccessKeyActive) {
                // A menu was opened via Alt+letter (e.g. Alt+F). The Alt
                // key is now being released — don't close the menu.
            } else {
                // Alt was consumed as a modifier (e.g. Alt+Up).  Clear the
                // access-key underlines that Avalonia's AccessKeyHandler may
                // have turned on when it saw the initial Alt press.
                ((Avalonia.Input.IInputRoot)this).ShowAccessKeys = false;
                // The platform may still activate the menu bar via native
                // Alt handling (WM_SYSKEYUP on Windows). Post a deferred
                // focus restore to undo it.
                Dispatcher.UIThread.Post(() => {
                    if (MenuBar.IsKeyboardFocusWithin
                        && _activeTab is not { IsSettings: true }) {
                        MenuBar.Close();
                        Editor.Focus();
                    }
                }, DispatcherPriority.Input);
            }
            return;
        }

        base.OnKeyUp(e);
        // Releasing Ctrl confirms an active PasteMore clipboard-cycling session.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && Editor.IsClipboardCycling) {
            Editor.ConfirmClipboardCycle();
        }
    }

    /// <summary>
    /// Opens the top-level menu whose access key matches the pressed letter.
    /// Called when Alt+letter is pressed and no command binding matches.
    /// </summary>
    private void TryOpenMenuAccessKey(KeyEventArgs e) {
        // Menu bar is hidden while the settings tab is open.
        if (_activeTab is { IsSettings: true }) return;
        var letter = e.Key.ToString();
        if (letter.Length != 1) return;
        foreach (var item in MenuBar.Items.OfType<MenuItem>()) {
            if (item.Header is not string header) continue;
            var idx = header.IndexOf('_');
            if (idx < 0 || idx + 1 >= header.Length) continue;
            if (char.ToUpperInvariant(header[idx + 1]) == char.ToUpperInvariant(letter[0])) {
                item.Open();
                item.Focus();
                _altPressedClean = false;
                _menuAccessKeyActive = true;
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>
    /// When a submenu is open, find the menu item whose access key matches
    /// the pressed letter and invoke it. Handles Alt+letter within an open
    /// menu so that e.g. Alt+E, Alt+P invokes Paste.
    /// </summary>
    private bool TryActivateSubmenuAccessKey(Key key) {
        var letter = key.ToString();
        if (letter.Length != 1) return false;
        var ch = char.ToUpperInvariant(letter[0]);

        foreach (var topItem in MenuBar.Items.OfType<MenuItem>()) {
            if (!topItem.IsSubMenuOpen) continue;
            return ActivateAccessKeyIn(topItem.Items, ch);
        }
        return false;
    }

    private bool ActivateAccessKeyIn(ItemCollection items, char ch) {
        foreach (var item in items.OfType<MenuItem>()) {
            if (item.Header is not string header) continue;
            var idx = header.IndexOf('_');
            if (idx < 0 || idx + 1 >= header.Length) continue;
            if (char.ToUpperInvariant(header[idx + 1]) != ch) continue;

            if (item.Items.Count > 0) {
                // Has a submenu — open it instead of invoking.
                item.Open();
                item.Focus();
            } else {
                // Leaf item — invoke the click handler.
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                MenuBar.Close();
                _menuAccessKeyActive = false;
                if (_activeTab is not { IsSettings: true }) {
                    Editor.Focus();
                }
            }
            return true;
        }
        return false;
    }

    private void CancelChord() {
        _chordTimer.Stop();
        _chordFirst = null;
        StatusLeft.Text = "";
    }

    private static bool HasCommandModifier(KeyEventArgs e) =>
        (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0;

    private static bool IsNonTextKey(Key key) => key is
        Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown
        or Key.Delete or Key.Back or Key.Insert or Key.Tab or Key.Enter or Key.Return
        or Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6
        or Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12
        or Key.CapsLock or Key.NumLock or Key.Scroll or Key.PrintScreen
        or Key.Pause or Key.Apps or Key.Sleep;

    private void UpdateIncrementalSearchStatus() {
        if (Editor.InIncrementalSearch) {
            StatusLeft.Text = Editor.IncrementalSearchFailed
                ? $"Incremental Search: {Editor.IncrementalSearchText} (not found)"
                : $"Incremental Search: {Editor.IncrementalSearchText}";
        } else {
            StatusLeft.Text = "";
        }
    }
}
