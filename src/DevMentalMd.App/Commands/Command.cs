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
    public bool ShowInPalette { get; }
    public bool RequiresEditor { get; }

    public Command(string id, string displayName, Action execute,
                   bool showInPalette = true, bool requiresEditor = false) {
        Id = id;
        Category = id[..id.IndexOf('.')];
        DisplayName = displayName;
        Execute = execute;
        ShowInPalette = showInPalette;
        RequiresEditor = requiresEditor;
    }
}
