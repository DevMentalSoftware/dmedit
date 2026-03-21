using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DevMentalMd.App.Services;
using DevMentalMd.Core.Printing;

namespace DevMentalMd.App;

/// <summary>
/// Avalonia print dialog combining printer selection with page setup
/// (paper size, orientation, margins, copies). Themed by the app's
/// current light/dark theme via <see cref="EditorTheme"/>.
/// </summary>
public class PrintDialog : Window {

    private readonly ComboBox _printerCombo;
    private readonly ComboBox _paperCombo;
    private readonly ToggleButton _paperToggle;
    private readonly RadioButton _portrait;
    private readonly RadioButton _landscape;
    private readonly NumericUpDown _marginTop;
    private readonly NumericUpDown _marginRight;
    private readonly NumericUpDown _marginBottom;
    private readonly NumericUpDown _marginLeft;
    private readonly NumericUpDown _copies;
    private readonly Border _rootBorder;

    private readonly ISystemPrintService? _printService;
    private IReadOnlyList<PaperSizeInfo> _allPaperSizes;
    private bool _showAllPapers;

    /// <summary>The completed job ticket after the user clicks Print. Null if cancelled.</summary>
    public PrintJobTicket? JobTicket { get; private set; }

    /// <summary>The chosen <see cref="PrintSettings"/> (also inside <see cref="JobTicket"/>).</summary>
    public PrintSettings? ResultSettings { get; private set; }

    /// <param name="printers">Available system printers.</param>
    /// <param name="paperSizes">Paper sizes for the initially selected printer.</param>
    /// <param name="initial">Page layout defaults (from Document or AppSettings).</param>
    /// <param name="savedPrinterName">
    /// Last-used printer name from settings. If it still exists in
    /// <paramref name="printers"/>, it will be pre-selected; otherwise the
    /// system default is used.
    /// </param>
    /// <param name="theme">Current editor theme for dialog background.</param>
    /// <param name="printService">
    /// Optional print service for refreshing paper sizes when the user
    /// changes the selected printer.
    /// </param>
    public PrintDialog(
        IReadOnlyList<PrinterInfo> printers,
        IReadOnlyList<PaperSizeInfo> paperSizes,
        PrintSettings initial,
        string? savedPrinterName,
        EditorTheme theme,
        ISystemPrintService? printService = null) {
        _printService = printService;
        _allPaperSizes = paperSizes.Count > 0 ? paperSizes : PaperSizeInfo.Defaults;

        Title = "Print";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.Full;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(20), Spacing = 4 };

        // -- Printer --
        root.Children.Add(MakeLabel("Printer"));
        _printerCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var defaultIdx = 0;
        var savedIdx = -1;
        for (var i = 0; i < printers.Count; i++) {
            _printerCombo.Items.Add(printers[i].Name);
            if (printers[i].IsDefault) {
                defaultIdx = i;
            }
            if (savedPrinterName is not null && printers[i].Name == savedPrinterName) {
                savedIdx = i;
            }
        }
        if (_printerCombo.Items.Count > 0) {
            _printerCombo.SelectedIndex = savedIdx >= 0 ? savedIdx : defaultIdx;
        }
        _printerCombo.SelectionChanged += OnPrinterChanged;
        root.Children.Add(_printerCombo);
        root.Children.Add(new Panel { Height = 8 });

        // -- Paper Size (with toggle) --
        var paperHeader = new DockPanel();
        _paperToggle = new ToggleButton {
            Content = "A",
            FontWeight = FontWeight.Bold,
            FontSize = 11,
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(_paperToggle, "Show all paper sizes");
        _paperToggle.Click += OnPaperToggle;
        DockPanel.SetDock(_paperToggle, Dock.Right);
        paperHeader.Children.Add(_paperToggle);       // docked right — must be added before fill child
        paperHeader.Children.Add(MakeLabel("Paper size")); // fill child — last
        root.Children.Add(paperHeader);

        _paperCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        PopulatePaperSizes(initial.Paper);
        root.Children.Add(_paperCombo);
        root.Children.Add(new Panel { Height = 8 });

        // -- Orientation --
        root.Children.Add(MakeLabel("Orientation"));
        var orientPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(0, 2, 0, 0),
        };
        _portrait = new RadioButton {
            Content = "Portrait",
            IsChecked = initial.Orientation == PageOrientation.Portrait,
            GroupName = "Orientation",
        };
        _landscape = new RadioButton {
            Content = "Landscape",
            IsChecked = initial.Orientation == PageOrientation.Landscape,
            GroupName = "Orientation",
        };
        orientPanel.Children.Add(_portrait);
        orientPanel.Children.Add(_landscape);
        root.Children.Add(orientPanel);
        root.Children.Add(new Panel { Height = 8 });

        // -- Margins --
        root.Children.Add(MakeLabel("Margins (inches)"));
        var marginsGrid = new Grid {
            Margin = new Thickness(0, 2, 0, 0),
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,Auto,24,Auto,Auto"),
            RowDefinitions = RowDefinitions.Parse("Auto,6,Auto"),
        };
        _marginTop = AddMarginField(marginsGrid, "Top:", 0, 0, initial.Margins.Top);
        _marginRight = AddMarginField(marginsGrid, "Right:", 0, 3, initial.Margins.Right);
        _marginBottom = AddMarginField(marginsGrid, "Bottom:", 2, 0, initial.Margins.Bottom);
        _marginLeft = AddMarginField(marginsGrid, "Left:", 2, 3, initial.Margins.Left);
        root.Children.Add(marginsGrid);
        root.Children.Add(new Panel { Height = 8 });

        // -- Copies --
        root.Children.Add(MakeLabel("Copies"));
        _copies = new NumericUpDown {
            Minimum = 1,
            Maximum = 999,
            Value = 1,
            Increment = 1,
            FormatString = "0",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        root.Children.Add(_copies);
        root.Children.Add(new Panel { Height = 16 });

        // -- Buttons --
        var buttonPanel = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        var printBtn = new Button { Content = "Print", MinWidth = 80 };
        printBtn.Click += OnPrint;
        var cancelBtn = new Button { Content = "Cancel", MinWidth = 80 };
        cancelBtn.Click += (_, _) => Close();
        buttonPanel.Children.Add(printBtn);
        buttonPanel.Children.Add(cancelBtn);
        root.Children.Add(buttonPanel);

        _rootBorder = new Border { Child = root };
        Content = _rootBorder;

        ApplyTheme(theme);
    }

    private void OnPrinterChanged(object? sender, SelectionChangedEventArgs e) {
        if (_printService is null) return;
        var name = _printerCombo.SelectedItem as string;
        if (name is null) return;

        // Re-fetch paper sizes for the newly selected printer.
        _allPaperSizes = _printService.GetPaperSizes(name);
        if (_allPaperSizes.Count == 0) {
            _allPaperSizes = PaperSizeInfo.Defaults;
        }

        // Preserve the current selection if the new printer supports it.
        var currentPaper = _paperCombo.SelectedItem as PaperSizeItem;
        PopulatePaperSizes(currentPaper?.Info);
    }

    private void OnPaperToggle(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        _showAllPapers = _paperToggle.IsChecked == true;
        ToolTip.SetTip(_paperToggle,
            _showAllPapers ? "Show common paper sizes" : "Show all paper sizes");
        var currentPaper = _paperCombo.SelectedItem as PaperSizeItem;
        PopulatePaperSizes(currentPaper?.Info);
    }

    private void PopulatePaperSizes(PaperSizeInfo? selectTarget) {
        _paperCombo.Items.Clear();
        var sizes = _showAllPapers
            ? _allPaperSizes
            : _allPaperSizes.Where(s => s.IsCommon).ToList();

        // If filtering leaves nothing, show all.
        if (sizes.Count == 0) {
            sizes = _allPaperSizes;
        }

        var bestIdx = 0;
        for (var i = 0; i < sizes.Count; i++) {
            _paperCombo.Items.Add(new PaperSizeItem(sizes[i]));
            if (selectTarget is not null && sizes[i].Name == selectTarget.Name) {
                bestIdx = i;
            }
        }
        if (_paperCombo.Items.Count > 0) {
            _paperCombo.SelectedIndex = bestIdx;
        }
    }

    private void OnPrint(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        var paper = (_paperCombo.SelectedItem as PaperSizeItem)?.Info ?? PaperSizeInfo.Letter;
        var orientation = _landscape.IsChecked == true
            ? PageOrientation.Landscape
            : PageOrientation.Portrait;

        var top = (double)(_marginTop.Value ?? 1m);
        var right = (double)(_marginRight.Value ?? 1m);
        var bottom = (double)(_marginBottom.Value ?? 1m);
        var left = (double)(_marginLeft.Value ?? 1m);

        var settings = new PrintSettings {
            Paper = paper,
            Orientation = orientation,
            Margins = new PrintMargins(top * 72, right * 72, bottom * 72, left * 72),
        };
        ResultSettings = settings;

        var printerName = _printerCombo.SelectedItem as string ?? "";
        JobTicket = new PrintJobTicket {
            PrinterName = printerName,
            Settings = settings,
            Copies = (int)(_copies.Value ?? 1),
        };

        Close();
    }

    private void ApplyTheme(EditorTheme theme) {
        _rootBorder.Background = theme.TabActiveBackground;
        Background = theme.TabActiveBackground;
        Foreground = theme.TabForeground;
        RequestedThemeVariant = theme == EditorTheme.Dark
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;
    }

    private static TextBlock MakeLabel(string text) => new() {
        Text = text,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 0, 0, 2),
    };

    private static NumericUpDown AddMarginField(Grid grid, string label, int row, int col, double pointValue) {
        var lbl = new TextBlock {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Grid.SetRow(lbl, row);
        Grid.SetColumn(lbl, col);
        grid.Children.Add(lbl);

        var inches = (decimal)(pointValue / 72.0);
        var nud = new NumericUpDown {
            Minimum = 0m,
            Maximum = 10m,
            Value = Math.Round(inches, 2),
            Increment = 0.1m,
            FormatString = "0.0#",
            Width = 90,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetRow(nud, row);
        Grid.SetColumn(nud, col + 1);
        grid.Children.Add(nud);

        return nud;
    }

    /// <summary>Wrapper for <see cref="PaperSizeInfo"/> to display in a ComboBox.</summary>
    private sealed class PaperSizeItem(PaperSizeInfo info) {
        public PaperSizeInfo Info => info;
        public override string ToString() => info.Name;
    }
}
