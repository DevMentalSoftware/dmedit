using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Single source of truth for all application commands. Each entry defines
/// a command's identity, display name, and default keyboard shortcut.
/// The category is derived from the command ID prefix (e.g. "Edit.Undo" →
/// "Edit"). Parallels <see cref="Settings.SettingsRegistry"/> for settings.
/// </summary>
public static class CommandRegistry {
    public static readonly IReadOnlyList<CommandDescriptor> All = [
        // -- File --
        new(CommandIds.FileNew, "New",
            new KeyGesture(Key.N, KeyModifiers.Control)),
        new(CommandIds.FileOpen, "Open",
            new KeyGesture(Key.O, KeyModifiers.Control)),
        new(CommandIds.FileSave, "Save",
            new KeyGesture(Key.S, KeyModifiers.Control)),
        new(CommandIds.FileSaveAs, "Save As",
            new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Alt)),
        new(CommandIds.FileSaveAll, "Save All",
            new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.FileClose, "Close",
            new KeyGesture(Key.F4, KeyModifiers.Control)),
        new(CommandIds.FileCloseAll, "Close All",
            new KeyGesture(Key.F4, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.FileExit, "Exit",
            new KeyGesture(Key.F4, KeyModifiers.Alt),
            new KeyGesture(Key.Q, KeyModifiers.Control)),

        // -- Edit --
        new(CommandIds.EditUndo, "Undo",
            new KeyGesture(Key.Z, KeyModifiers.Control)),
        new(CommandIds.EditRedo, "Redo",
            new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.EditCut, "Cut",
            new KeyGesture(Key.X, KeyModifiers.Control)),
        new(CommandIds.EditCopy, "Copy",
            new KeyGesture(Key.C, KeyModifiers.Control)),
        new(CommandIds.EditPaste, "Paste",
            new KeyGesture(Key.V, KeyModifiers.Control)),
        new(CommandIds.EditDelete, "Delete",
            new KeyGesture(Key.Delete)),
        new(CommandIds.EditBackspace, "Backspace",
            new KeyGesture(Key.Back)),
        new(CommandIds.EditSelectAll, "Select All",
            new KeyGesture(Key.A, KeyModifiers.Control)),
        new(CommandIds.EditSelectWord, "Select Word",
            new KeyGesture(Key.W, KeyModifiers.Control)),
        new(CommandIds.EditDeleteLine, "Delete Line",
            new KeyGesture(Key.Y, KeyModifiers.Control)),
        new(CommandIds.EditMoveLineUp, "Move Line Up",
            new KeyGesture(Key.Up, KeyModifiers.Alt)),
        new(CommandIds.EditMoveLineDown, "Move Line Down",
            new KeyGesture(Key.Down, KeyModifiers.Alt)),
        new(CommandIds.EditUpperCase, "Upper Case",
            new KeyGesture(Key.U, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.EditLowerCase, "Lower Case",
            new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.EditProperCase, "Proper Case",
            new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.EditNewline, "Insert Newline",
            new KeyGesture(Key.Return)),
        new(CommandIds.EditTab, "Insert Tab",
            new KeyGesture(Key.Tab)),

        // -- View --
        new(CommandIds.ViewLineNumbers, "Line Numbers"),
        new(CommandIds.ViewStatusBar, "Status Bar"),
        new(CommandIds.ViewWrapLines, "Wrap Lines"),

        // -- Window --
        new(CommandIds.WindowNextTab, "Next Tab",
            new KeyGesture(Key.Tab, KeyModifiers.Control)),
        new(CommandIds.WindowPrevTab, "Previous Tab",
            new KeyGesture(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.WindowSettings, "Settings"),

        // -- Nav: movement --
        new(CommandIds.NavMoveLeft, "Move Left",
            new KeyGesture(Key.Left)),
        new(CommandIds.NavMoveRight, "Move Right",
            new KeyGesture(Key.Right)),
        new(CommandIds.NavMoveUp, "Move Up",
            new KeyGesture(Key.Up)),
        new(CommandIds.NavMoveDown, "Move Down",
            new KeyGesture(Key.Down)),
        new(CommandIds.NavMoveWordLeft, "Move Word Left",
            new KeyGesture(Key.Left, KeyModifiers.Control)),
        new(CommandIds.NavMoveWordRight, "Move Word Right",
            new KeyGesture(Key.Right, KeyModifiers.Control)),
        new(CommandIds.NavMoveHome, "Move to Line Start",
            new KeyGesture(Key.Home)),
        new(CommandIds.NavMoveEnd, "Move to Line End",
            new KeyGesture(Key.End)),
        new(CommandIds.NavMoveDocStart, "Move to Document Start",
            new KeyGesture(Key.Home, KeyModifiers.Control)),
        new(CommandIds.NavMoveDocEnd, "Move to Document End",
            new KeyGesture(Key.End, KeyModifiers.Control)),
        new(CommandIds.NavPageUp, "Page Up",
            new KeyGesture(Key.PageUp)),
        new(CommandIds.NavPageDown, "Page Down",
            new KeyGesture(Key.PageDown)),

        // -- Nav: selection extension --
        new(CommandIds.NavSelectLeft, "Select Left",
            new KeyGesture(Key.Left, KeyModifiers.Shift)),
        new(CommandIds.NavSelectRight, "Select Right",
            new KeyGesture(Key.Right, KeyModifiers.Shift)),
        new(CommandIds.NavSelectUp, "Select Up",
            new KeyGesture(Key.Up, KeyModifiers.Shift)),
        new(CommandIds.NavSelectDown, "Select Down",
            new KeyGesture(Key.Down, KeyModifiers.Shift)),
        new(CommandIds.NavSelectWordLeft, "Select Word Left",
            new KeyGesture(Key.Left, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.NavSelectWordRight, "Select Word Right",
            new KeyGesture(Key.Right, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.NavSelectHome, "Select to Line Start",
            new KeyGesture(Key.Home, KeyModifiers.Shift)),
        new(CommandIds.NavSelectEnd, "Select to Line End",
            new KeyGesture(Key.End, KeyModifiers.Shift)),
        new(CommandIds.NavSelectDocStart, "Select to Document Start",
            new KeyGesture(Key.Home, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.NavSelectDocEnd, "Select to Document End",
            new KeyGesture(Key.End, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.NavSelectPageUp, "Select Page Up",
            new KeyGesture(Key.PageUp, KeyModifiers.Shift)),
        new(CommandIds.NavSelectPageDown, "Select Page Down",
            new KeyGesture(Key.PageDown, KeyModifiers.Shift)),
    ];

    /// <summary>
    /// Ordered list of categories, derived once from the registered commands.
    /// </summary>
    public static readonly IReadOnlyList<string> Categories =
        All.Select(c => c.Category).Distinct().ToList();
}
