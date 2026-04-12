using System;
using System.Collections.Generic;
using System.Linq;

namespace DMEdit.App.Commands;

/// <summary>
/// Single source of truth for every command in the application. Each command is
/// a <c>public static readonly</c> field. <see cref="DefineMenus"/> builds the
/// master <see cref="All"/> list, assigns menu ordering, and sets the
/// <see cref="Command.Menu"/> property. Runtime delegates are wired separately
/// via <see cref="Command.Wire"/> at startup.
/// </summary>
public static class Commands {
    // =================================================================
    // File
    // =================================================================

    public static readonly Command FileNew = new("File", "New", "_New") {
        DefaultInToolbar = true,
        ToolbarGlyph = IconGlyphs.Add, ToolbarTooltip = "New",
    };
    public static readonly Command FileOpen = new("File", "Open", "_Open\u2026") {
        ToolbarGlyph = IconGlyphs.Open, ToolbarTooltip = "Open",

    };
    public static readonly Command FileSave = new("File", "Save", "_Save") {
        ToolbarGlyph = IconGlyphs.Save, ToolbarTooltip = "Save",
    };
    public static readonly Command FileSaveAs = new("File", "SaveAs", "Save _As\u2026") {
        ToolbarGlyph = IconGlyphs.SaveAs, ToolbarTooltip = "Save As",
    };
    public static readonly Command FileSaveAll = new("File", "SaveAll", "Save A_ll") {
        IsAdvanced = true,
        ToolbarGlyph = IconGlyphs.SaveAll, ToolbarTooltip = "Save All",

    };
    public static readonly Command FileRevertFile = new("File", "RevertFile", "_Revert File") {
        IsAdvanced = true,
        ToolbarGlyph = IconGlyphs.RevertFile, ToolbarTooltip = "Revert File",
    };
    public static readonly Command FilePrint = new("File", "Print", "_Print\u2026") {
        ToolbarGlyph = IconGlyphs.Print, ToolbarTooltip = "Print",
    };
    public static readonly Command FileSaveAsPdf = new("File", "SaveAsPdf", "Save As P_DF\u2026") {
        IsAdvanced = true,
        ToolbarGlyph = IconGlyphs.SaveAsPdf, ToolbarTooltip = "Save As PDF",
    };
    public static readonly Command FileClose = new("File", "Close", "_Close");
    public static readonly Command FileCloseAll = new("File", "CloseAll", "Close A_ll") {
        IsAdvanced = true,
        ToolbarGlyph = IconGlyphs.Close, ToolbarTooltip = "Close All",

    };
    public static readonly Command FileExit = new("File", "Exit", "E_xit");
    public static readonly Command FileToggleReadOnly = new("File", "ToggleReadOnly", "Toggle Read _Only") {
        IsAdvanced = true,
    };
    public static readonly Command FileReloadFile = new("File", "ReloadFile", "Re_load File") {
        IsAdvanced = true,
        ToolbarGlyph = IconGlyphs.ReloadFile, ToolbarTooltip = "Reload File",
    };
    public static readonly Command FileRecent = new("File", "Recent", "_Recent") {
        ToolbarGlyph = IconGlyphs.History, ToolbarTooltip = "Recent",
        IsToolbarDropdown = true,
    };
    public static readonly Command FileClearRecentFiles = new("File", "ClearRecentFiles", "Clear Recent Files") { IsAdvanced = true };

    // =================================================================
    // Edit
    // =================================================================

    public static readonly Command EditUndo = new("Edit", "Undo", "_Undo") {
        RequiresEditor = true,
        DefaultInToolbar = true, ToolbarGlyph = IconGlyphs.Undo, ToolbarTooltip = "Undo",
    };
    public static readonly Command EditRedo = new("Edit", "Redo", "_Redo") {
        RequiresEditor = true,
        DefaultInToolbar = true, ToolbarGlyph = IconGlyphs.Redo, ToolbarTooltip = "Redo",
    };
    public static readonly Command EditCut = new("Edit", "Cut", "Cu_t") {
        RequiresEditor = true,
        DefaultInToolbar = true, ToolbarGlyph = IconGlyphs.Cut, ToolbarTooltip = "Cut",
    };
    public static readonly Command EditCopy = new("Edit", "Copy", "_Copy") {
        RequiresEditor = true,
        DefaultInToolbar = true, ToolbarGlyph = IconGlyphs.Copy, ToolbarTooltip = "Copy",
    };
    public static readonly Command EditPaste = new("Edit", "Paste", "_Paste") {
        RequiresEditor = true,
        DefaultInToolbar = true, ToolbarGlyph = IconGlyphs.Paste, ToolbarTooltip = "Paste",
    };
    public static readonly Command EditPasteMore = new("Edit", "PasteMore", "Paste _More") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditClipboardRing = new("Edit", "ClipboardRing", "Clipboard _Ring") {
        IsAdvanced = true,
        ToolbarGlyph = IconGlyphs.ClipboardRing, ToolbarTooltip = "Clipboard Ring",
    };
    public static readonly Command EditDelete = new("Edit", "Delete", "De_lete") { RequiresEditor = true };
    public static readonly Command EditSelectAll = new("Edit", "SelectAll", "Select _All") { RequiresEditor = true };
    public static readonly Command EditSelectWord = new("Edit", "SelectWord", "Select _Word") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditDeleteLine = new("Edit", "DeleteLine", "Delete Li_ne") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditMoveLineUp = new("Edit", "MoveLineUp", "Move Line _Up") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditMoveLineDown = new("Edit", "MoveLineDown", "Move Line Dow_n") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditInsertLineBelow = new("Edit", "InsertLineBelow", "Insert Line _Below") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditInsertLineAbove = new("Edit", "InsertLineAbove", "Insert Line A_bove") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditDuplicateLine = new("Edit", "DuplicateLine", "D_uplicate Line") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditDeleteWordLeft = new("Edit", "DeleteWordLeft", "Delete Word Le_ft") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditDeleteWordRight = new("Edit", "DeleteWordRight", "Delete Word Ri_ght") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditSmartIndent = new("Edit", "SmartIndent", "Smart In_dent") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditUpperCase = new("Edit", "UpperCase", "_Upper Case") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditLowerCase = new("Edit", "LowerCase", "_Lower Case") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditProperCase = new("Edit", "ProperCase", "_Proper Case") {
        RequiresEditor = true, IsAdvanced = true,
    };

    // Edit commands not in menus
    public static readonly Command EditBackspace = new("Edit", "Backspace") { RequiresEditor = true };
    public static readonly Command EditNewline = new("Edit", "Newline", "Insert Newline") { RequiresEditor = true };
    public static readonly Command EditTab = new("Edit", "Tab", "Insert Tab") { RequiresEditor = true };
    public static readonly Command EditToggleOverwrite = new("Edit", "ToggleOverwrite", "Toggle Overwrite Mode") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditExpandSelection = new("Edit", "ExpandSelection", "Expand Selection") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditIndent = new("Edit", "Indent") { RequiresEditor = true, IsAdvanced = true };
    public static readonly Command EditOutdent = new("Edit", "Outdent") { RequiresEditor = true, IsAdvanced = true };
    public static readonly Command EditIndentToSpaces = new("Edit", "IndentToSpaces", "Convert Indentation to Spaces") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command EditIndentToTabs = new("Edit", "IndentToTabs", "Convert Indentation to Tabs") {
        RequiresEditor = true, IsAdvanced = true,
    };

    // =================================================================
    // Search
    // =================================================================

    public static readonly Command SearchFind = new("Search", "Find", "_Find") {
        ToolbarGlyph = IconGlyphs.Search, ToolbarTooltip = "Find",
    };
    public static readonly Command SearchReplace = new("Search", "Replace", "_Replace") {
        ToolbarGlyph = IconGlyphs.Replace, ToolbarTooltip = "Replace",
    };
    public static readonly Command SearchFindNext = new("Search", "FindNext", "Find _Next");
    public static readonly Command SearchFindPrevious = new("Search", "FindPrevious", "Find _Previous");
    public static readonly Command SearchFindNextSelection = new("Search", "FindNextSelection", "Find Next _Selection") {
        IsAdvanced = true,
    };
    public static readonly Command SearchFindPreviousSelection = new("Search", "FindPreviousSelection", "Find Previous Se_lection") {
        IsAdvanced = true,
    };
    public static readonly Command SearchIncrementalSearch = new("Search", "IncrementalSearch", "_Incremental Search") {
        IsAdvanced = true,
    };
    public static readonly Command SearchGoToLine = new("Search", "GoToLine", "_Go to Line") {
        ToolbarGlyph = IconGlyphs.GoToLine, ToolbarTooltip = "Go to Line",
    };
    public static readonly Command SearchCommandPalette = new("Search", "CommandPalette", "Commands") {
        IsAdvanced = true,
        ToolbarGlyph = IconGlyphs.CommandPalette, ToolbarTooltip = "Commands",
    };

    // =================================================================
    // View
    // =================================================================

    public static readonly Command ViewLineNumbers = new("View", "LineNumbers", "_Line Numbers") {
        IsAdvanced = true,
        ToolbarGlyph = IconGlyphs.LineNumbers, ToolbarTooltip = "Line Numbers",
        IsToolbarToggle = true,
    };
    public static readonly Command ViewStatusBar = new("View", "StatusBar", "_Status Bar") {
        ToolbarGlyph = IconGlyphs.StatusBar, ToolbarTooltip = "Status Bar",
        IsToolbarToggle = true,
    };
    public static readonly Command ViewWrapLines = new("View", "WrapLines", "_Wrap Lines") {
        DefaultInToolbar = true, ToolbarGlyph = IconGlyphs.Wrap, ToolbarTooltip = "Wrap Lines",
        IsToolbarToggle = true,
    };
    public static readonly Command ViewWhitespace = new("View", "Whitespace", "Show W_hitespace") {
        ToolbarGlyph = IconGlyphs.Whitespace, ToolbarTooltip = "Show Whitespace",
        IsToolbarToggle = true,
    };
    public static readonly Command ViewZoomIn = new("View", "ZoomIn", "Zoom _In") {
        ToolbarGlyph = IconGlyphs.ZoomIn, ToolbarTooltip = "Zoom In",
    };
    public static readonly Command ViewZoomOut = new("View", "ZoomOut", "Zoom _Out") {
        ToolbarGlyph = IconGlyphs.ZoomOut, ToolbarTooltip = "Zoom Out",
    };
    public static readonly Command ViewZoomReset = new("View", "ZoomReset", "Zoom _Reset") {
        ToolbarGlyph = IconGlyphs.ZoomReset, ToolbarTooltip = "Zoom Reset",
    };

    // =================================================================
    // Nav
    // =================================================================

    public static readonly Command ViewScrollLineUp = new("View", "ScrollLineUp", "Scroll Line _Up") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command ViewScrollLineDown = new("View", "ScrollLineDown", "Scroll Line _Down") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command NavFocusEditor = new("Nav", "FocusEditor", "Focus Editor");

    // Nav: horizontal movement
    public static readonly Command NavMoveLeft = new("Nav", "MoveLeft", "Move Left") { RequiresEditor = true };
    public static readonly Command NavSelectLeft = new("Nav", "SelectLeft", "Select Left") { RequiresEditor = true };
    public static readonly Command NavMoveRight = new("Nav", "MoveRight", "Move Right") { RequiresEditor = true };
    public static readonly Command NavSelectRight = new("Nav", "SelectRight", "Select Right") { RequiresEditor = true };
    public static readonly Command NavMoveWordLeft = new("Nav", "MoveWordLeft", "Move Word Left") { RequiresEditor = true };
    public static readonly Command NavSelectWordLeft = new("Nav", "SelectWordLeft", "Select Word Left") { RequiresEditor = true };
    public static readonly Command NavMoveWordRight = new("Nav", "MoveWordRight", "Move Word Right") { RequiresEditor = true };
    public static readonly Command NavSelectWordRight = new("Nav", "SelectWordRight", "Select Word Right") { RequiresEditor = true };

    // Nav: vertical movement
    public static readonly Command NavMoveUp = new("Nav", "MoveUp", "Move Up") { RequiresEditor = true };
    public static readonly Command NavSelectUp = new("Nav", "SelectUp", "Select Up") { RequiresEditor = true };
    public static readonly Command NavMoveDown = new("Nav", "MoveDown", "Move Down") { RequiresEditor = true };
    public static readonly Command NavSelectDown = new("Nav", "SelectDown", "Select Down") { RequiresEditor = true };

    // Nav: home/end
    public static readonly Command NavMoveHome = new("Nav", "MoveHome", "Move to Line Start") { RequiresEditor = true };
    public static readonly Command NavSelectHome = new("Nav", "SelectHome", "Select to Line Start") { RequiresEditor = true };
    public static readonly Command NavMoveEnd = new("Nav", "MoveEnd", "Move to Line End") { RequiresEditor = true };
    public static readonly Command NavSelectEnd = new("Nav", "SelectEnd", "Select to Line End") { RequiresEditor = true };

    // Nav: document start/end
    public static readonly Command NavMoveDocStart = new("Nav", "MoveDocStart", "Move to Document Start") { RequiresEditor = true };
    public static readonly Command NavSelectDocStart = new("Nav", "SelectDocStart", "Select to Document Start") { RequiresEditor = true };
    public static readonly Command NavMoveDocEnd = new("Nav", "MoveDocEnd", "Move to Document End") { RequiresEditor = true };
    public static readonly Command NavSelectDocEnd = new("Nav", "SelectDocEnd", "Select to Document End") { RequiresEditor = true };

    // Nav: page up/down
    public static readonly Command NavPageUp = new("Nav", "PageUp", "Page Up") { RequiresEditor = true };
    public static readonly Command NavSelectPageUp = new("Nav", "SelectPageUp", "Select Page Up") { RequiresEditor = true };
    public static readonly Command NavPageDown = new("Nav", "PageDown", "Page Down") { RequiresEditor = true };
    public static readonly Command NavSelectPageDown = new("Nav", "SelectPageDown", "Select Page Down") { RequiresEditor = true };

    // Nav: column selection
    public static readonly Command NavColumnSelectUp = new("Nav", "ColumnSelectUp", "Column Select Up") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command NavColumnSelectDown = new("Nav", "ColumnSelectDown", "Column Select Down") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command NavColumnSelectLeft = new("Nav", "ColumnSelectLeft", "Column Select Left") {
        RequiresEditor = true, IsAdvanced = true,
    };
    public static readonly Command NavColumnSelectRight = new("Nav", "ColumnSelectRight", "Column Select Right") {
        RequiresEditor = true, IsAdvanced = true,
    };

    // =================================================================
    // Window
    // =================================================================

    public static readonly Command WindowNextTab = new("Window", "NextTab", "Next Tab");
    public static readonly Command WindowPrevTab = new("Window", "PrevTab", "Previous Tab");
    public static readonly Command WindowSettings = new("Window", "Settings") {
        DefaultInToolbar = true, ToolbarFixed = true,
        ToolbarGlyph = IconGlyphs.Settings, ToolbarTooltip = "Settings",
    };

    // =================================================================
    // Menu pseudo-commands (Alt+letter access keys)
    // =================================================================

    public static readonly Command PseudoMenuFile = new("Menu", "File", "File Menu");
    public static readonly Command PseudoMenuEdit = new("Menu", "Edit", "Edit Menu");
    public static readonly Command PseudoMenuSearch = new("Menu", "Search", "Search Menu");
    public static readonly Command PseudoMenuView = new("Menu", "View", "View Menu");
    public static readonly Command PseudoMenuHelp = new("Menu", "Help", "Help Menu");

    // =================================================================
    // Master list and lookup
    // =================================================================

    public static IReadOnlyList<Command> All { get; }

    private static readonly Dictionary<string, Command> _byId;

    /// <summary>Returns the command with the given ID, or null if not found.</summary>
    public static Command? TryGet(string id) =>
        _byId.TryGetValue(id, out var c) ? c : null;

    /// <summary>Returns the command with the given ID, or throws.</summary>
    public static Command Get(string id) =>
        _byId.TryGetValue(id, out var c)
            ? c
            : throw new InvalidOperationException($"Command '{id}' is not defined.");

    /// <summary>Executes a command by string ID if found and enabled.</summary>
    public static bool Execute(string id) => TryGet(id)?.Run() ?? false;

    static Commands() {
        var all = DefineMenus();
        All = all;
        _byId = all.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    // =================================================================
    // Menu layout — defines ordering and builds the All list
    // =================================================================

    private static List<Command> DefineMenus() {
        var all = new List<Command>();
        CommandMenu currentMenu = CommandMenu.None;

        void StartMenu(CommandMenu menu) => currentMenu = menu;

        void Item(Command cmd) {
            cmd.Menu = currentMenu;
            all.Add(cmd);
        }

        void Sub(string subMenu, Command cmd) {
            cmd.Menu = currentMenu;
            cmd.SubMenu = subMenu;
            all.Add(cmd);
        }

        void Sep() { } // Separators are positional — order in All is enough.

        void Add(Command cmd) => all.Add(cmd);

        // -- File menu --
        StartMenu(CommandMenu.File);
        Item(FileNew);
        Item(FileOpen);
        Item(FileRecent);
        Sep();
        Item(FileSave);
        Item(FileSaveAs);
        Item(FileSaveAll);
        Item(FileRevertFile);
        Item(FileReloadFile);
        Item(FileToggleReadOnly);
        Sep();
        Item(FilePrint);
        Item(FileSaveAsPdf);
        Sep();
        Item(FileClose);
        Item(FileCloseAll);
        Sep();
        Item(FileExit);

        // -- Edit menu --
        StartMenu(CommandMenu.Edit);
        Item(EditUndo);
        Item(EditRedo);
        Sep();
        Item(EditCut);
        Item(EditCopy);
        Item(EditPaste);
        Item(EditPasteMore);
        Item(EditClipboardRing);
        Item(EditDelete);
        Sep();
        Item(EditSelectAll);
        Item(EditSelectWord);
        Sep();
        Item(EditDeleteLine);
        Item(EditMoveLineUp);
        Item(EditMoveLineDown);
        Sep();
        Item(EditInsertLineBelow);
        Item(EditInsertLineAbove);
        Item(EditDuplicateLine);
        Sep();
        Item(EditDeleteWordLeft);
        Item(EditDeleteWordRight);
        Item(EditSmartIndent);
        Sep();
        Sub("Transform Case", EditUpperCase);
        Sub("Transform Case", EditLowerCase);
        Sub("Transform Case", EditProperCase);

        // -- Search menu --
        StartMenu(CommandMenu.Search);
        Item(SearchFind);
        Item(SearchReplace);
        Sep();
        Item(SearchFindNext);
        Item(SearchFindPrevious);
        Item(SearchFindNextSelection);
        Item(SearchFindPreviousSelection);
        Sep();
        Item(SearchIncrementalSearch);
        Sep();
        Item(SearchGoToLine);
        Sep();
        Item(SearchCommandPalette);

        // -- View menu --
        StartMenu(CommandMenu.View);
        Item(ViewLineNumbers);
        Item(ViewStatusBar);
        Sep();
        Item(ViewWrapLines);
        Item(ViewWhitespace);
        Sep();
        Sub("Zoom", ViewZoomIn);
        Sub("Zoom", ViewZoomOut);
        Sub("Zoom", ViewZoomReset);
        Sep();
        Item(ViewScrollLineUp);
        Item(ViewScrollLineDown);

        // -- Non-menu commands --
        Add(FileClearRecentFiles);

        Add(EditBackspace);
        Add(EditNewline);
        Add(EditTab);
        Add(EditToggleOverwrite);
        Add(EditExpandSelection);
        Add(EditIndent);
        Add(EditOutdent);
        Add(EditIndentToSpaces);
        Add(EditIndentToTabs);



        Add(NavFocusEditor);
        Add(NavMoveLeft);
        Add(NavSelectLeft);
        Add(NavMoveRight);
        Add(NavSelectRight);
        Add(NavMoveWordLeft);
        Add(NavSelectWordLeft);
        Add(NavMoveWordRight);
        Add(NavSelectWordRight);
        Add(NavMoveUp);
        Add(NavSelectUp);
        Add(NavMoveDown);
        Add(NavSelectDown);
        Add(NavMoveHome);
        Add(NavSelectHome);
        Add(NavMoveEnd);
        Add(NavSelectEnd);
        Add(NavMoveDocStart);
        Add(NavSelectDocStart);
        Add(NavMoveDocEnd);
        Add(NavSelectDocEnd);
        Add(NavPageUp);
        Add(NavSelectPageUp);
        Add(NavPageDown);
        Add(NavSelectPageDown);
        Add(NavColumnSelectUp);
        Add(NavColumnSelectDown);
        Add(NavColumnSelectLeft);
        Add(NavColumnSelectRight);

        Add(WindowNextTab);
        Add(WindowPrevTab);
        Add(WindowSettings);

        Add(PseudoMenuFile);
        Add(PseudoMenuEdit);
        Add(PseudoMenuSearch);
        Add(PseudoMenuView);
        Add(PseudoMenuHelp);

        return all;
    }
}
