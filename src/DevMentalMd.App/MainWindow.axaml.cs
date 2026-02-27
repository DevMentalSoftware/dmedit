using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DevMentalMd.App.Services;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.IO;

namespace DevMentalMd.App;

public partial class MainWindow : Window {
    private string? _currentPath;
    private readonly RecentFilesStore _recentFiles = RecentFilesStore.Load();
    private readonly AppSettings _settings = AppSettings.Load();

    public MainWindow() {
        InitializeComponent();

        MenuNew.Click += OnNew;
        MenuOpen.Click += OnOpen;
        MenuSave.Click += OnSave;
        MenuSaveAs.Click += OnSaveAs;

        RebuildRecentMenu();
        WireScrollBar();
        WireDevMenu();

        var sampleText =
            "# Welcome to DevMentalMD\n" +
            "\n" +
            "This is a plain-text editing surface — the first milestone.\n" +
            "\n" +
            "You can:\n" +
            "- Type and delete text\n" +
            "- Move the caret with arrow keys (Ctrl+Left/Right for word movement)\n" +
            "- Select text with Shift+arrow or click-drag\n" +
            "- Ctrl+A to select all\n" +
            "- Ctrl+Z / Ctrl+Y to undo and redo\n" +
            "- Home / End to jump to line start/end\n" +
            "- File → Open / Save to work with .md files\n" +
            "\n" +
            "Markdown rendering and formatting come next.\n";

        Editor.Document = new Document(sampleText);
    }

    // -------------------------------------------------------------------------
    // Scroll bar wiring
    // -------------------------------------------------------------------------

    private void WireScrollBar() {
        // Editor → ScrollBar: push scroll state whenever it changes
        Editor.ScrollChanged += (_, _) => SyncScrollBarFromEditor();

        // ScrollBar → Editor: update scroll offset when user drags/clicks scrollbar
        ScrollBar.ScrollRequested += newValue => {
            Editor.ScrollValue = newValue;
        };
    }

    private void SyncScrollBarFromEditor() {
        ScrollBar.Maximum = Editor.ScrollMaximum;
        ScrollBar.Value = Editor.ScrollValue;
        ScrollBar.ViewportSize = Editor.ScrollViewportHeight;
        ScrollBar.ExtentSize = Editor.ScrollExtentHeight;
        ScrollBar.LineHeight = Editor.LineHeightValue;
    }

    // -------------------------------------------------------------------------
    // Dev menu wiring
    // -------------------------------------------------------------------------

    private void WireDevMenu() {
        MenuDev.IsVisible = DevMode.IsEnabled;

        // Initialize slider from settings
        SliderOuterScrollRate.Value = _settings.OuterThumbScrollRateMultiplier;
        ScrollBar.OuterScrollRateMultiplier = _settings.OuterThumbScrollRateMultiplier;
        LabelOuterScrollRate.Text = _settings.OuterThumbScrollRateMultiplier.ToString("F1");

        SliderOuterScrollRate.PropertyChanged += (_, e) => {
            if (e.Property.Name != "Value") {
                return;
            }
            var val = SliderOuterScrollRate.Value;
            ScrollBar.OuterScrollRateMultiplier = val;
            LabelOuterScrollRate.Text = val.ToString("F1");
            _settings.OuterThumbScrollRateMultiplier = val;
            _settings.Save();
        };
    }

    // -------------------------------------------------------------------------
    // File menu handlers
    // -------------------------------------------------------------------------

    private void OnNew(object? sender, RoutedEventArgs e) {
        Editor.Document = new Document();
        _currentPath = null;
        Title = "DevMentalMD";
    }

    private async void OnOpen(object? sender, RoutedEventArgs e) {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Open Markdown File",
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });

        if (files.Count == 0) {
            return;
        }
        var path = files[0].TryGetLocalPath();
        if (path is null) {
            return;
        }

        Editor.Document = await FileLoader.LoadAsync(path);
        _currentPath = path;
        Title = $"DevMentalMD — {Path.GetFileName(path)}";

        _recentFiles.Push(path);
        _recentFiles.Save();
        RebuildRecentMenu();
    }

    private async void OnSave(object? sender, RoutedEventArgs e) {
        if (_currentPath is null) {
            await SaveAsAsync();
            return;
        }
        await SaveToAsync(_currentPath);
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveAsAsync();

    // -------------------------------------------------------------------------
    // Recent Files menu
    // -------------------------------------------------------------------------

    private void RebuildRecentMenu() {
        MenuRecent.Items.Clear();

        foreach (var path in _recentFiles.Paths) {
            var captured = path;
            var item = new MenuItem { Header = path };
            item.Click += async (_, _) => await OpenRecentFileAsync(captured);
            MenuRecent.Items.Add(item);
        }

        if (DevMode.IsEnabled) {
            if (_recentFiles.Paths.Count > 0) {
                MenuRecent.Items.Add(new Separator());
            }
            foreach (var sample in DevSamples.All) {
                var captured = sample;
                var item = new MenuItem { Header = sample.DisplayName };
                item.Click += (_, _) => OpenDevSample(captured);
                MenuRecent.Items.Add(item);
            }
        }

        if (MenuRecent.Items.Count == 0) {
            MenuRecent.Items.Add(new MenuItem { Header = "(no recent files)", IsEnabled = false });
        }
    }

    private async Task OpenRecentFileAsync(string path) {
        if (!File.Exists(path)) {
            _recentFiles.Remove(path);
            _recentFiles.Save();
            RebuildRecentMenu();
            return;
        }

        Editor.Document = await FileLoader.LoadAsync(path);
        _currentPath = path;
        Title = $"DevMentalMD — {Path.GetFileName(path)}";

        _recentFiles.Push(path);
        _recentFiles.Save();
        RebuildRecentMenu();
    }

    private void OpenDevSample(ProceduralSample sample) {
        Editor.Document = sample.CreateDocument();
        _currentPath = null;
        Title = $"DevMentalMD — {sample.DisplayName}";
    }

    // -------------------------------------------------------------------------
    // Save helpers
    // -------------------------------------------------------------------------

    private async Task SaveAsAsync() {
        var suggestedName = _currentPath is null
            ? "untitled.md"
            : Path.GetFileName(_currentPath);

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = "Save Markdown File",
            SuggestedFileName = suggestedName,
            FileTypeChoices = [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });

        if (file is null) {
            return;
        }
        var path = file.TryGetLocalPath();
        if (path is null) {
            return;
        }

        await SaveToAsync(path);
        _currentPath = path;
        Title = $"DevMentalMD — {Path.GetFileName(path)}";
    }

    private async Task SaveToAsync(string path) {
        if (Editor.Document is null) {
            return;
        }
        await FileSaver.SaveAsync(Editor.Document, path);
    }
}
