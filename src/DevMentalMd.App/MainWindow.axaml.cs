using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DevMentalMd.App.Services;
using DevMentalMd.Core.Buffers;
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
        WireStatsBar();

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
        ScrollBar.RowHeight = Editor.RowHeightValue;
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
    // Stats bar wiring
    // -------------------------------------------------------------------------

    private int _statsThrottle;

    private void WireStatsBar() {
        StatusBar.IsVisible = DevMode.IsEnabled;
        Editor.StatsUpdated += () => {
            // Throttle: update every 5th frame to avoid layout thrash
            if (++_statsThrottle % 5 != 0) return;
            // Defer to after the render pass — updating Text during Render()
            // would invalidate a visual mid-pass and throw.
            Dispatcher.UIThread.Post(() => {
                var s = Editor.PerfStats;
                var editStat = s.Edit.Count > 0 ? $"  |  Edit: {s.Edit.Format()}" : "";
                StatsBar.Text =
                    $"Layout: {s.Layout.Format()}  |  " +
                    $"Render: {s.Render.Format()}{editStat}  |  " +
                    $"{s.LogicalLines:N0} lines, {s.ViewportLines} in view ({s.ViewportRows} rows)  |  " +
                    $"{s.ScrollPercent:F1}%";
                string load;
                if (s.FirstChunkTimeMs > 0) {
                    load = $"{s.FirstChunkTimeMs:F1}ms + {s.LoadTimeMs:F1}ms";
                } else {
                    load = s.LoadTimeMs > 0 ? $"{s.LoadTimeMs:F1}ms" : "—";
                }
                var save = s.SaveTimeMs > 0 ? $"{s.SaveTimeMs:F1}ms" : "—";
                StatsBarIO.Text = $"Load: {load}  |  Save: {save}";
            }, DispatcherPriority.Background);
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

        var sw = Stopwatch.StartNew();
        Editor.Document = await FileLoader.LoadAsync(path);
        WireStreamingProgress(sw);

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

        var sw = Stopwatch.StartNew();
        Editor.Document = await FileLoader.LoadAsync(path);
        WireStreamingProgress(sw);

        _currentPath = path;
        Title = $"DevMentalMD — {Path.GetFileName(path)}";

        _recentFiles.Push(path);
        _recentFiles.Save();
        RebuildRecentMenu();
    }

    private void OpenDevSample(ProceduralSample sample) {
        var sw = Stopwatch.StartNew();
        Editor.Document = sample.CreateDocument();
        sw.Stop();
        Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;

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
        var sw = Stopwatch.StartNew();
        await FileSaver.SaveAsync(Editor.Document, path);
        sw.Stop();
        Editor.PerfStats.SaveTimeMs = sw.Elapsed.TotalMilliseconds;
    }

    // -------------------------------------------------------------------------
    // Streaming file load progress
    // -------------------------------------------------------------------------

    /// <summary>
    /// If the current document is backed by a <see cref="StreamingFileBuffer"/>,
    /// subscribes to its progress events so the editor re-layouts incrementally
    /// and the stats bar shows the running load time. For small (non-streaming)
    /// files the stopwatch is stopped immediately.
    /// </summary>
    private void WireStreamingProgress(Stopwatch sw) {
        if (Editor.Document?.Table.OrigBuffer is StreamingFileBuffer streamBuf) {
            // Streaming load — capture time to first renderable chunk, then keep
            // the stopwatch running until fully loaded.
            var firstChunkCaptured = false;
            Editor.PerfStats.FirstChunkTimeMs = 0;
            Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;

            streamBuf.ProgressChanged += () => {
                Dispatcher.UIThread.Post(() => {
                    if (!firstChunkCaptured) {
                        firstChunkCaptured = true;
                        Editor.PerfStats.FirstChunkTimeMs = sw.Elapsed.TotalMilliseconds;
                    }
                    Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                    Editor.InvalidateLayout();
                }, DispatcherPriority.Background);
            };

            streamBuf.LoadComplete += () => {
                Dispatcher.UIThread.Post(() => {
                    sw.Stop();
                    Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                    Editor.InvalidateLayout();
                }, DispatcherPriority.Background);
            };
        } else {
            // Small file — loaded synchronously, stop the clock.
            // No streaming, so first-chunk time is the same as total load time.
            sw.Stop();
            Editor.PerfStats.FirstChunkTimeMs = 0;
            Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
        }
    }
}
