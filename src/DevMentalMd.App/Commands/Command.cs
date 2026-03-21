using System;

namespace DevMentalMd.App.Commands;

/// <summary>
/// A registered application command with its identity, display metadata, and
/// execution delegate. Commands are registered into <see cref="CommandRegistry"/>
/// and can be bound to keys, menus, context menus, or executed directly from
/// the command palette.
/// </summary>
public sealed class Command {
    public string Id { get; }
    public string Category { get; }
    public string DisplayName { get; }
    public Action Execute { get; }
    public Func<bool>? CanExecute { get; }
    public bool ShowInPalette { get; }
    public bool RequiresEditor { get; }

    public Command(string id, string displayName, Action execute,
                   Func<bool>? canExecute = null,
                   bool showInPalette = true, bool requiresEditor = false) {
        Id = id;
        Category = id[..id.IndexOf('.')];
        DisplayName = displayName;
        Execute = execute;
        CanExecute = canExecute;
        ShowInPalette = showInPalette;
        RequiresEditor = requiresEditor;
    }

    /// <summary>
    /// Returns true if the command can currently execute. Commands without a
    /// <see cref="CanExecute"/> predicate are always enabled.
    /// </summary>
    public bool IsEnabled => CanExecute?.Invoke() ?? true;
}
