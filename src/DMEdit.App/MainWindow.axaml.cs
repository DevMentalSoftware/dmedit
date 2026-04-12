using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.App.Controls;
using DMEdit.App.Services;
using DMEdit.App.Settings;
using DMEdit.Core.Buffers;
using DMEdit.Core.Documents;
using DMEdit.Core.IO;
using DMEdit.Core.Printing;

namespace DMEdit.App;

public partial class MainWindow : Window {

    private const int FLASH_RELOAD_DURATION_MS = 1000 / 3;

    private static readonly FilePickerFileType[] FileTypeFilters = [
        new("Text files") { Patterns = ["*.txt", "*.log", "*.md", "*.*"] },
        new("Markdown") { Patterns = ["*.md", "*.markdown"] },
        new("All files") { Patterns = ["*.*"] },
    ];

    private readonly List<TabState> _tabs = [];
    internal TabState? _activeTab;
    private TabState? _findBarTab;
    private CancellationTokenSource? _matchCountCts;
    private readonly RecentFilesStore _recentFiles = RecentFilesStore.Load();
    private readonly AppSettings _settings = AppSettings.Load();
    public AppSettings Settings => _settings;
    private readonly KeyBindingService _keyBindings;
    private int _staticMenuItemCount;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private EditorTheme _theme = EditorTheme.Light;
    private long _statusWarningExpiry;
    private bool _windowStateReady;
    private TabState? _settingsTab;
    private readonly FileWatcherService _watcher = new();
    private Task<PrintResult>? _printTask;
    private readonly UpdateService _updateService = new();
    private readonly List<(MenuItem item, Command cmd)> _menuCommandBindings = [];
    private TextBlock? _lineNumGlyph;
    private TextBlock? _statusBarGlyph;
    private TextBlock? _wrapLinesGlyph;
    private TextBlock? _whitespaceGlyph;

    // Chord gesture display: cached PART_InputGestureText references.
    private readonly Dictionary<MenuItem, Control> _menuGestureParts = [];
    private readonly HashSet<MenuItem> _gestureHooked = [];

    // Chord state: two-keystroke shortcut in progress.
    private KeyGesture? _chordFirst;
    private readonly DispatcherTimer _chordTimer;

    // Alt-menu activation: defer to KeyUp so Alt+drag / Alt+Shift+Arrow
    // don't accidentally activate the menu bar.
    private bool _altPressedClean;
    // Set when TryOpenMenuAccessKey opens a menu via Alt+letter so the
    // subsequent Alt KeyUp doesn't immediately close it.
    private bool _menuAccessKeyActive;

    private string[]? _startupFiles;

    public MainWindow() : this(null) { }

    internal MainWindow(string[]? startupFiles) {
        _startupFiles = startupFiles;
        InitializeComponent();
        RegisterWindowCommands();
        Editor.RegisterCommands();
        // Inject settings so the editor can read "passive" values (e.g.
        // DistributeColumnPaste) directly at the call site rather than going
        // through a per-setting cached property + push from the SettingChanged
        // switch.  Set BEFORE any user input handler can fire.
        Editor.Settings = _settings;
        _keyBindings = new KeyBindingService(_settings);
        _chordTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(_settings.ChordTimeoutMs),
        };
        _chordTimer.Tick += (_, _) => CancelChord();

        // On Linux the WM ignores ExtendClientAreaToDecorationsHint and draws
        // its own title bar. Remove it so we can draw custom chrome buttons
        // in our tab bar, matching the Windows experience. BorderOnly removes
        // the title bar but also loses WM resize handles, so we add a
        // rounded, colored border (matching the tab bar) around the content
        // and handle resize ourselves. The window itself is transparent so
        // the rounded corners clip cleanly against the desktop.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            WindowDecorations = WindowDecorations.BorderOnly;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            WindowBorder.CornerRadius = new CornerRadius(8);
            WindowBorder.Padding = new Thickness(EdgeGrip);
            StatusBar.CornerRadius = new CornerRadius(0, 0, 6, 6);
            AddHandler(PointerPressedEvent, OnEdgePointerPressed,
                RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnEdgePointerMoved,
                RoutingStrategies.Tunnel);
        }

        RestoreWindowSize();

        // Wire each XAML MenuItem to its Command — sets up click handler,
        // IsEnabled tracking, gesture text, and advanced-menu visibility
        // in one call per item.
        // File:
        WireMenu(MenuNew, Cmd.FileNew);
        WireMenu(MenuOpen, Cmd.FileOpen);
        WireMenu(MenuSave, Cmd.FileSave);
        WireMenu(MenuSaveAs, Cmd.FileSaveAs);
        WireMenu(MenuSaveAll, Cmd.FileSaveAll);
        WireMenu(MenuRevertFile, Cmd.FileRevertFile);
        WireMenu(MenuReloadFile, Cmd.FileReloadFile);
        WireMenu(MenuToggleReadOnly, Cmd.FileToggleReadOnly);
        WireMenu(MenuPrint, Cmd.FilePrint);
        WireMenu(MenuSaveAsPdf, Cmd.FileSaveAsPdf);
        WireMenu(MenuClose, Cmd.FileClose);
        WireMenu(MenuCloseAll, Cmd.FileCloseAll);
        WireMenu(MenuExit, Cmd.FileExit);
        // Edit:
        WireMenu(MenuUndo, Cmd.EditUndo);
        WireMenu(MenuRedo, Cmd.EditRedo);
        WireMenu(MenuCut, Cmd.EditCut);
        WireMenu(MenuCopy, Cmd.EditCopy);
        WireMenu(MenuPaste, Cmd.EditPaste);
        WireMenu(MenuPasteMore, Cmd.EditPasteMore);
        WireMenu(MenuClipboardRing, Cmd.EditClipboardRing);
        WireMenu(MenuDelete, Cmd.EditDelete);
        WireMenu(MenuSelectAll, Cmd.EditSelectAll);
        WireMenu(MenuSelectWord, Cmd.EditSelectWord);
        WireMenu(MenuDeleteLine, Cmd.EditDeleteLine);
        WireMenu(MenuMoveLineUp, Cmd.EditMoveLineUp);
        WireMenu(MenuMoveLineDown, Cmd.EditMoveLineDown);
        WireMenu(MenuInsertLineBelow, Cmd.EditInsertLineBelow);
        WireMenu(MenuInsertLineAbove, Cmd.EditInsertLineAbove);
        WireMenu(MenuDuplicateLine, Cmd.EditDuplicateLine);
        WireMenu(MenuDeleteWordLeft, Cmd.EditDeleteWordLeft);
        WireMenu(MenuDeleteWordRight, Cmd.EditDeleteWordRight);
        WireMenu(MenuIndent, Cmd.EditSmartIndent);
        WireMenu(MenuCaseUpper, Cmd.EditUpperCase);
        WireMenu(MenuCaseLower, Cmd.EditLowerCase);
        WireMenu(MenuCaseProper, Cmd.EditProperCase);
        // Search:
        WireMenu(MenuFind, Cmd.SearchFind);
        WireMenu(MenuReplace, Cmd.SearchReplace);
        WireMenu(MenuFindNext, Cmd.SearchFindNext);
        WireMenu(MenuFindPrevious, Cmd.SearchFindPrevious);
        WireMenu(MenuFindNextSelection, Cmd.SearchFindNextSelection);
        WireMenu(MenuFindPreviousSelection, Cmd.SearchFindPreviousSelection);
        WireMenu(MenuIncrementalSearch, Cmd.SearchIncrementalSearch);
        WireMenu(MenuGoToLine, Cmd.SearchGoToLine);
        WireMenu(MenuCommandPalette, Cmd.SearchCommandPalette);
        // View:
        WireMenu(MenuLineNumbers, Cmd.ViewLineNumbers);
        WireMenu(MenuStatusBar, Cmd.ViewStatusBar);
        WireMenu(MenuWrapLines, Cmd.ViewWrapLines);
        WireMenu(MenuWhitespace, Cmd.ViewWhitespace);
        WireMenu(MenuZoomIn, Cmd.ViewZoomIn);
        WireMenu(MenuZoomOut, Cmd.ViewZoomOut);
        WireMenu(MenuZoomReset, Cmd.ViewZoomReset);
        WireMenu(MenuScrollLineUp, Cmd.ViewScrollLineUp);
        WireMenu(MenuScrollLineDown, Cmd.ViewScrollLineDown);

        // Print is only available on Windows when the WPF DLL is present.
        if (!WindowsPrintService.IsAvailable) {
            MenuPrint.IsVisible = false;
            // Avalonia 12: IsVisible on MenuItems may not hide them in all
            // cases.  Remove from parent as a belt-and-suspenders fallback.
            if (MenuPrint.Parent is MenuItem parent) {
                parent.Items.Remove(MenuPrint);
            } else if (MenuPrint.Parent is Menu menu) {
                menu.Items.Remove(MenuPrint);
            }
        }

        // Help items don't have commands.
        MenuSubmitFeedback.Click += async (_, _) => {
            var doc = _activeTab?.Document?.Table;
            var name = _activeTab?.DisplayName;
            var dlg = new SubmitFeedbackDialog(_theme, doc, name);
            await dlg.ShowDialog(this);
        };
        MenuAbout.Click += async (_, _) => {
            var dlg = new AboutDialog(_theme);
            await dlg.ShowDialog(this);
        };

        MenuFile.SubmenuOpened += OnTopMenuOpened;
        MenuEdit.SubmenuOpened += OnTopMenuOpened;
        MenuSearch.SubmenuOpened += OnTopMenuOpened;
        MenuView.SubmenuOpened += OnTopMenuOpened;

        _staticMenuItemCount = MenuFile.Items.Count;

        PruneRecentFiles();
        RebuildRecentMenu();
        WireScrollBar();
        WireViewMenuState();
        ApplyMenuIcons();
        InitializeToolbar();
        InitializeTabToolbar();
        ApplyAdvancedMenuVisibility();
        WireSettingsPanel();
        WireThemeSettings();
        WireTabBar();
        WireStatsBar();
        WireStatusBarButtons();
        WireFindBar();
        WireWindowState();
        WireFileWatcher();
        SyncMenuGestures();

        // Clicking on empty menu bar space should fully dismiss any open menu,
        // but must not intercept clicks on actual MenuItems in the bar.
        MenuBarBorder.PointerPressed += (_, e) => {
            for (var src = e.Source as Control; src != null && src != MenuBarBorder; src = src.Parent as Control) {
                if (src is MenuItem) return;
            }
            MenuBar.Close();
        };

        // Any mouse press while Alt is held means Alt is being used as a
        // modifier (e.g. column selection), not for menu activation.
        // Use tunnel routing so this fires before child controls handle the event.
        AddHandler(PointerPressedEvent, (_, _) => {
            if (_altPressedClean) _altPressedClean = false;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // File drag-and-drop: open dropped files in new tabs.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // When all menus close, return focus to the editor.
        MenuBar.Closed += (_, _) => {
            if (_activeTab is not { IsSettings: true }) {
                Editor.Focus();
            }
        };

        // Window activation tracking for tab bar (active/inactive styling).
        // No focus stealing — focus stays wherever the user left it.
        Activated += (_, _) => {
            TabBar.IsWindowActive = true;
            // Recheck the active tab's file on window activation in case
            // FSW missed events (e.g. network drives).
            if (_activeTab is not null) {
                _watcher.Recheck(_activeTab);
            }
        };
        Deactivated += (_, _) => {
            TabBar.IsWindowActive = false;
            CancelChord();
        };

        // Give the editor initial focus once at startup.
        Opened += (_, _) => {
            if (_activeTab is not { IsSettings: true }) {
                Editor.Focus();
            }
        };

        // Update check: always check on startup; AutoUpdate controls auto-download.
        _updateService.UpdateAvailable += OnUpdateAvailable;
        _updateService.UpdateReady += OnUpdateReady;

        _ = Task.Run(async () => {
            try { await _updateService.CheckAsync(autoDownload: _settings.AutoUpdate); }
            catch { /* best-effort */ }
        });

        InitSessionAsync();
    }
    // --- moved to MainWindow.Tabs.cs ---


    // -------------------------------------------------------------------------
    // Centralized keyboard dispatch (command system)
    // -------------------------------------------------------------------------
    // --- moved to MainWindow.Input.cs ---

    // --- moved to MainWindow.MenuToolbar.cs ---



    // -------------------------------------------------------------------------
    // Stats / status bar
    // -------------------------------------------------------------------------
    // --- moved to MainWindow.StatusBar.cs ---


    // -------------------------------------------------------------------------
    // File menu handlers
    // -------------------------------------------------------------------------
    // --- moved to MainWindow.FileOps.cs ---


    // -------------------------------------------------------------------------
    // Settings panel
    // -------------------------------------------------------------------------
    // --- moved to MainWindow.Dialogs.cs ---


    // --- moved to MainWindow.Session.cs ---

}
