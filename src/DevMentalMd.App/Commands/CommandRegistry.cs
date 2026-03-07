using System.Collections.Generic;
using Avalonia.Input;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Single source of truth for all application commands. Each entry defines
/// a command's identity, display name, category, and default keyboard
/// shortcut. Parallels <see cref="Settings.SettingsRegistry"/> for settings.
/// </summary>
public static class CommandRegistry {
    public static readonly IReadOnlyList<string> Categories = [
        "File",
        "Edit",
        "View",
        "Window",
        "Nav",
    ];

    public static readonly IReadOnlyList<CommandDescriptor> All = [
        // -- File --
        new(CommandIds.FileNew, "New", "File",
            new KeyGesture(Key.N, KeyModifiers.Control)),
        new(CommandIds.FileOpen, "Open", "File",
            new KeyGesture(Key.O, KeyModifiers.Control)),
        new(CommandIds.FileSave, "Save", "File",
            new KeyGesture(Key.S, KeyModifiers.Control)),
        new(CommandIds.FileSaveAs, "Save As", "File",
            new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Alt)),
        new(CommandIds.FileSaveAll, "Save All", "File",
            new KeyGesture(Key.S, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.FileClose, "Close", "File",
            new KeyGesture(Key.F4, KeyModifiers.Control)),
        new(CommandIds.FileCloseAll, "Close All", "File",
            new KeyGesture(Key.F4, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.FileExit, "Exit", "File",
            new KeyGesture(Key.F4, KeyModifiers.Alt)),

        // -- Edit --
        new(CommandIds.EditUndo, "Undo", "Edit",
            new KeyGesture(Key.Z, KeyModifiers.Control)),
        new(CommandIds.EditRedo, "Redo", "Edit",
            new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.EditCut, "Cut", "Edit",
            new KeyGesture(Key.X, KeyModifiers.Control)),
        new(CommandIds.EditCopy, "Copy", "Edit",
            new KeyGesture(Key.C, KeyModifiers.Control)),
        new(CommandIds.EditPaste, "Paste", "Edit",
            new KeyGesture(Key.V, KeyModifiers.Control)),
        new(CommandIds.EditDelete, "Delete", "Edit",
            new KeyGesture(Key.Delete)),
        new(CommandIds.EditBackspace, "Backspace", "Edit",
            new KeyGesture(Key.Back)),
        new(CommandIds.EditSelectAll, "Select All", "Edit",
            new KeyGesture(Key.A, KeyModifiers.Control)),
        new(CommandIds.EditSelectWord, "Select Word", "Edit",
            new KeyGesture(Key.W, KeyModifiers.Control)),
        new(CommandIds.EditDeleteLine, "Delete Line", "Edit",
            new KeyGesture(Key.Y, KeyModifiers.Control)),
        new(CommandIds.EditMoveLineUp, "Move Line Up", "Edit",
            new KeyGesture(Key.Up, KeyModifiers.Alt)),
        new(CommandIds.EditMoveLineDown, "Move Line Down", "Edit",
            new KeyGesture(Key.Down, KeyModifiers.Alt)),
        new(CommandIds.EditUpperCase, "Upper Case", "Edit",
            new KeyGesture(Key.U, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.EditLowerCase, "Lower Case", "Edit",
            new KeyGesture(Key.L, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.EditProperCase, "Proper Case", "Edit",
            new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.EditNewline, "Insert Newline", "Edit",
            new KeyGesture(Key.Return)),
        new(CommandIds.EditTab, "Insert Tab", "Edit",
            new KeyGesture(Key.Tab)),

        // -- View --
        new(CommandIds.ViewLineNumbers, "Line Numbers", "View"),
        new(CommandIds.ViewStatusBar, "Status Bar", "View"),
        new(CommandIds.ViewWrapLines, "Wrap Lines", "View"),

        // -- Window --
        new(CommandIds.WindowNextTab, "Next Tab", "Window",
            new KeyGesture(Key.Tab, KeyModifiers.Control)),
        new(CommandIds.WindowPrevTab, "Previous Tab", "Window",
            new KeyGesture(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.WindowSettings, "Settings", "Window"),

        // -- Nav: movement --
        new(CommandIds.NavMoveLeft, "Move Left", "Nav",
            new KeyGesture(Key.Left)),
        new(CommandIds.NavMoveRight, "Move Right", "Nav",
            new KeyGesture(Key.Right)),
        new(CommandIds.NavMoveUp, "Move Up", "Nav",
            new KeyGesture(Key.Up)),
        new(CommandIds.NavMoveDown, "Move Down", "Nav",
            new KeyGesture(Key.Down)),
        new(CommandIds.NavMoveWordLeft, "Move Word Left", "Nav",
            new KeyGesture(Key.Left, KeyModifiers.Control)),
        new(CommandIds.NavMoveWordRight, "Move Word Right", "Nav",
            new KeyGesture(Key.Right, KeyModifiers.Control)),
        new(CommandIds.NavMoveHome, "Move to Line Start", "Nav",
            new KeyGesture(Key.Home)),
        new(CommandIds.NavMoveEnd, "Move to Line End", "Nav",
            new KeyGesture(Key.End)),
        new(CommandIds.NavMoveDocStart, "Move to Document Start", "Nav",
            new KeyGesture(Key.Home, KeyModifiers.Control)),
        new(CommandIds.NavMoveDocEnd, "Move to Document End", "Nav",
            new KeyGesture(Key.End, KeyModifiers.Control)),
        new(CommandIds.NavPageUp, "Page Up", "Nav",
            new KeyGesture(Key.PageUp)),
        new(CommandIds.NavPageDown, "Page Down", "Nav",
            new KeyGesture(Key.PageDown)),

        // -- Nav: selection extension --
        new(CommandIds.NavSelectLeft, "Select Left", "Nav",
            new KeyGesture(Key.Left, KeyModifiers.Shift)),
        new(CommandIds.NavSelectRight, "Select Right", "Nav",
            new KeyGesture(Key.Right, KeyModifiers.Shift)),
        new(CommandIds.NavSelectUp, "Select Up", "Nav",
            new KeyGesture(Key.Up, KeyModifiers.Shift)),
        new(CommandIds.NavSelectDown, "Select Down", "Nav",
            new KeyGesture(Key.Down, KeyModifiers.Shift)),
        new(CommandIds.NavSelectWordLeft, "Select Word Left", "Nav",
            new KeyGesture(Key.Left, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.NavSelectWordRight, "Select Word Right", "Nav",
            new KeyGesture(Key.Right, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.NavSelectHome, "Select to Line Start", "Nav",
            new KeyGesture(Key.Home, KeyModifiers.Shift)),
        new(CommandIds.NavSelectEnd, "Select to Line End", "Nav",
            new KeyGesture(Key.End, KeyModifiers.Shift)),
        new(CommandIds.NavSelectDocStart, "Select to Document Start", "Nav",
            new KeyGesture(Key.Home, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.NavSelectDocEnd, "Select to Document End", "Nav",
            new KeyGesture(Key.End, KeyModifiers.Control | KeyModifiers.Shift)),
        new(CommandIds.NavSelectPageUp, "Select Page Up", "Nav",
            new KeyGesture(Key.PageUp, KeyModifiers.Shift)),
        new(CommandIds.NavSelectPageDown, "Select Page Down", "Nav",
            new KeyGesture(Key.PageDown, KeyModifiers.Shift)),
    ];
}
