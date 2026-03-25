using System;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Menu that a command belongs to. <see cref="None"/> means the command
/// is not in any menu (e.g. navigation keys, pseudo-commands).
/// </summary>
public enum CommandMenu { None, File, Edit, Search, View, Help }

/// <summary>
/// A single application command — identity, metadata, menu/toolbar placement,
/// and runtime execution delegate all in one object. Static instances live in
/// <see cref="Commands"/>; call <see cref="Wire"/> at startup to connect the
/// runtime action.
/// </summary>
public sealed class Command {
    // -- Identity (immutable) --

    /// <summary>Unique string ID like "File.Save" — used as JSON key in profiles and settings.</summary>
    public string Id { get; }

    /// <summary>Which top-level menu this command belongs to, or <see cref="CommandMenu.None"/>.
    /// Set by <see cref="Commands.DefineMenus"/>.</summary>
    public CommandMenu Menu { get; internal set; }

    /// <summary>Category prefix (e.g. "File", "Edit", "Nav"). Derived from the menu enum
    /// name for menu commands, or set explicitly for non-menu commands.</summary>
    public string Category { get; }

    /// <summary>User-visible name with <c>_</c> stripped (e.g. "Save As"). Used in the
    /// command palette and toolbar tooltips.</summary>
    public string DisplayName { get; }

    /// <summary>Display name with <c>_</c> access-key marker intact (e.g. "Save _As\u2026").
    /// Used as <c>MenuItem.Header</c> in XAML menus.</summary>
    public string MenuDisplayName { get; }

    // -- Flags (set in field initializer) --

    public bool RequiresEditor { get; init; }
    public bool IsAdvanced { get; init; }

    // -- Toolbar (set in field initializer) --

    /// <summary>Whether this command appears in the toolbar by default.
    /// The user can override this at runtime via Settings > Commands.</summary>
    public bool DefaultInToolbar { get; init; }

    public string? ToolbarGlyph { get; init; }
    public string? ToolbarTooltip { get; init; }
    public bool IsToolbarToggle { get; init; }

    // -- Menu placement (set by Commands.DefineMenus) --

    /// <summary>Submenu name (e.g. "Transform Case", "Zoom"), or null for top-level items.</summary>
    internal string? SubMenu { get; set; }

    // -- Runtime --

    internal Action? _execute;
    internal Func<bool>? _canExecute;

    /// <summary>Returns true if the command can currently execute.</summary>
    public bool IsEnabled => _canExecute?.Invoke() ?? true;

    /// <summary>Wires the runtime action and optional enabled-check at startup.</summary>
    public void Wire(Action execute, Func<bool>? canExecute = null) {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <summary>Executes the command if enabled. Returns true if executed.</summary>
    public bool Run() {
        if (!IsEnabled) return false;
        _execute?.Invoke();
        return true;
    }

    /// <summary>Creates a command.</summary>
    /// <param name="category">Category prefix for the ID (e.g. "File", "Edit", "Nav").</param>
    /// <param name="name">Short PascalCase name used as the ID suffix (e.g. "Save", "MoveLeft").
    /// Also used as the default display name.</param>
    /// <param name="displayName">User-visible name if different from <paramref name="name"/>.
    /// May include <c>_</c> for menu access keys (e.g. "Save _As\u2026").</param>
    public Command(string category, string name, string? displayName = null) {
        Category = category;
        Id = category + "." + name;
        var dn = displayName ?? name;
        MenuDisplayName = dn;
        DisplayName = dn.Replace("_", "");
    }
}
