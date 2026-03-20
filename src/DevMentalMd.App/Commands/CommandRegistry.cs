using System;
using System.Collections.Generic;
using System.Linq;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Dictionary-based registry of all application commands. Commands register
/// their identity, display name, and execution delegate at startup. The
/// registry provides O(1) lookup by ID and supports iteration for the command
/// palette and keyboard settings UI.
/// </summary>
public sealed class CommandRegistry {
    private readonly Dictionary<string, Command> _commands = new(StringComparer.Ordinal);

    public void Register(string id, string displayName, Action execute,
                         bool showInPalette = true, bool requiresEditor = false) {
        _commands[id] = new Command(id, displayName, execute, showInPalette, requiresEditor);
    }

    /// <summary>Executes the command with the given ID. Returns true if found.</summary>
    public bool Execute(string id) {
        if (_commands.TryGetValue(id, out var cmd)) {
            cmd.Execute();
            return true;
        }
        return false;
    }

    public Command? TryGet(string id) =>
        _commands.TryGetValue(id, out var cmd) ? cmd : null;

    public IReadOnlyCollection<Command> All => _commands.Values;

    public IReadOnlyList<string> Categories =>
        _commands.Values.Select(c => c.Category).Distinct().OrderBy(c => c).ToList();
}
