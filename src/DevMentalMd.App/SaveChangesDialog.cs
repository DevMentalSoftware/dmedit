using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DevMentalMd.App.Services;

namespace DevMentalMd.App;

/// <summary>
/// Per-document save decision.
/// </summary>
public enum SaveChoice {
    Save,
    DontSave,
    Cancel,
}

/// <summary>
/// Result of a single-tab "Do you want to save?" dialog.
/// </summary>
public sealed class SingleSaveResult {
    public SaveChoice Choice { get; init; }
}

/// <summary>
/// Per-tab decision in the multi-tab close dialog.
/// </summary>
public sealed class TabSaveDecision {
    public required TabState Tab { get; init; }
    public SaveChoice Choice { get; set; } = SaveChoice.DontSave;
}

/// <summary>
/// Result of the multi-tab "Save changes?" dialog.
/// </summary>
public sealed class MultiSaveResult {
    /// <summary>True if the user cancelled the entire operation.</summary>
    public bool Cancelled { get; init; }
    public IReadOnlyList<TabSaveDecision> Decisions { get; init; } = [];
}

/// <summary>
/// "Do you want to save changes to {filename}?" dialog for a single tab.
/// Buttons: [Save] [Don't Save] [Cancel].
/// </summary>
public class SaveChangesDialog : Window {
    public SingleSaveResult Result { get; private set; } = new() { Choice = SaveChoice.Cancel };

    public SaveChangesDialog(string displayName, EditorTheme? theme = null) {
        Title = "Save Changes";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.Full;

        var message = new TextBlock {
            Text = $"Do you want to save changes to {displayName}?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20),
            FontSize = 14,
        };

        var saveBtn = new Button { Content = "Save", MinWidth = 80 };
        saveBtn.Click += (_, _) => {
            Result = new SingleSaveResult { Choice = SaveChoice.Save };
            Close();
        };

        var dontSaveBtn = new Button { Content = "Don't Save", MinWidth = 80 };
        dontSaveBtn.Click += (_, _) => {
            Result = new SingleSaveResult { Choice = SaveChoice.DontSave };
            Close();
        };

        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
        cancelBtn.Click += (_, _) => {
            Result = new SingleSaveResult { Choice = SaveChoice.Cancel };
            Close();
        };

        var buttonPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { saveBtn, dontSaveBtn, cancelBtn },
        };

        Content = new StackPanel {
            Margin = new Thickness(20),
            Children = { message, buttonPanel },
        };

        if (theme is not null) {
            Background = theme.TabActiveBackground;
            Foreground = theme.TabForeground;
        }
    }
}

/// <summary>
/// Multi-tab save dialog shown when closing multiple tabs with unsaved changes.
/// Each row shows a document name with [Save] and [Don't Save] buttons.
/// Per-row [Save] invokes the save callback immediately (before the dialog closes).
/// Once all rows are decided, [Save All]/[Save None] are disabled and
/// [Cancel] becomes [Close].
/// Bottom buttons: [Save All] [Save None] [Cancel].
/// </summary>
public class MultiSaveChangesDialog : Window {
    private readonly List<TabSaveDecision> _decisions;
    private readonly Func<TabState, Task<bool>> _saveCallback;
    private readonly HashSet<int> _decided = new();
    private bool _cancelled = true;
    private bool _busy;
    private Button _saveAllBtn = null!;
    private Button _saveNoneBtn = null!;
    private Button _cancelBtn = null!;

    public MultiSaveResult Result => _cancelled
        ? new MultiSaveResult { Cancelled = true }
        : new MultiSaveResult { Cancelled = false, Decisions = _decisions };

    /// <param name="dirtyTabs">Tabs with unsaved changes.</param>
    /// <param name="saveCallback">
    /// Called immediately when the user clicks a per-row [Save] or [Save All].
    /// Returns true on success, false if the save was cancelled (e.g. user
    /// dismissed a Save As dialog for an untitled document).
    /// </param>
    public MultiSaveChangesDialog(
            IReadOnlyList<TabState> dirtyTabs,
            Func<TabState, Task<bool>> saveCallback,
            EditorTheme? theme = null) {
        _decisions = dirtyTabs.Select(t => new TabSaveDecision { Tab = t }).ToList();
        _saveCallback = saveCallback;

        Title = "Save Changes";
        Width = 480;
        MinWidth = 380;
        MaxWidth = 700;
        Height = Math.Clamp(140 + _decisions.Count * 36, 220, 500);
        MinHeight = 200;
        MaxHeight = 600;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.Full;

        var heading = new TextBlock {
            Text = "The following files have unsaved changes:",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8),
        };

        // Per-file rows.
        var listPanel = new StackPanel { Spacing = 2 };

        for (var i = 0; i < _decisions.Count; i++) {
            listPanel.Children.Add(BuildRow(i));
        }

        var listScroll = new ScrollViewer {
            Content = listPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // Bottom action buttons.
        _saveAllBtn = new Button { Content = "Save All", MinWidth = 80 };
        _saveAllBtn.Click += async (_, _) => await SaveAllAsync();

        _saveNoneBtn = new Button { Content = "Save None", MinWidth = 80 };
        _saveNoneBtn.Click += (_, _) => {
            foreach (var d in _decisions) {
                d.Choice = SaveChoice.DontSave;
            }
            _cancelled = false;
            Close();
        };

        _cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
        _cancelBtn.Click += (_, _) => {
            if (_decided.Count == _decisions.Count) {
                // All decided — "Close" means accept the per-row choices.
                _cancelled = false;
            } else {
                _cancelled = true;
            }
            Close();
        };

        var bottomPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0),
            Children = { _saveAllBtn, _saveNoneBtn, _cancelBtn },
        };

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(bottomPanel, Dock.Bottom);
        root.Children.Add(heading);
        root.Children.Add(bottomPanel);
        root.Children.Add(listScroll); // fills remaining space

        Content = root;

        if (theme is not null) {
            Background = theme.TabActiveBackground;
            Foreground = theme.EditorForeground;
        }
    }

    private Border BuildRow(int index) {
        var decision = _decisions[index];

        var nameBlock = new TextBlock {
            Text = decision.Tab.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var saveBtn = new Button {
            Content = "Save",
            MinWidth = 60,
            Padding = new Thickness(8, 2),
        };
        var dontSaveBtn = new Button {
            Content = "Don't Save",
            MinWidth = 80,
            Padding = new Thickness(8, 2),
        };

        var rowButtons = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { saveBtn, dontSaveBtn },
        };

        var row = new DockPanel { Margin = new Thickness(0, 1) };
        DockPanel.SetDock(rowButtons, Dock.Right);
        row.Children.Add(rowButtons);
        row.Children.Add(nameBlock);

        var border = new Border {
            Child = row,
            Padding = new Thickness(6, 4),
            CornerRadius = new CornerRadius(3),
        };

        saveBtn.Click += async (_, _) => {
            if (_busy) return;
            _busy = true;
            try {
                decision.Choice = SaveChoice.Save;
                var ok = await _saveCallback(decision.Tab);
                if (ok) {
                    MarkRow(border, nameBlock, "Saved", index);
                } else {
                    // Save was cancelled (e.g. user dismissed Save As) — reset.
                    decision.Choice = SaveChoice.DontSave;
                }
            } finally {
                _busy = false;
            }
        };
        dontSaveBtn.Click += (_, _) => {
            if (_busy) return;
            decision.Choice = SaveChoice.DontSave;
            MarkRow(border, nameBlock, null, index);
        };

        return border;
    }

    private async Task SaveAllAsync() {
        if (_busy) return;
        _busy = true;
        try {
            for (var i = 0; i < _decisions.Count; i++) {
                if (_decided.Contains(i)) continue;
                var decision = _decisions[i];
                decision.Choice = SaveChoice.Save;
                var ok = await _saveCallback(decision.Tab);
                if (!ok) {
                    // Save cancelled — abort Save All, leave remaining undecided.
                    decision.Choice = SaveChoice.DontSave;
                    return;
                }
            }
            _cancelled = false;
            Close();
        } finally {
            _busy = false;
        }
    }

    private void MarkRow(Border border, TextBlock nameBlock, string? suffix, int index) {
        // Dim the row to indicate it's been handled.
        border.Opacity = 0.45;
        border.IsHitTestVisible = false;
        if (suffix is not null) {
            nameBlock.Text += $"  ({suffix})";
        }

        _decided.Add(index);
        if (_decided.Count == _decisions.Count) {
            // All rows decided — disable bulk buttons, rename Cancel to Close.
            _saveAllBtn.IsEnabled = false;
            _saveNoneBtn.IsEnabled = false;
            _cancelBtn.Content = "Close";
        }
    }
}
