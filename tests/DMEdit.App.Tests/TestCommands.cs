using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;

namespace DMEdit.App.Tests;

/// <summary>
/// Wires all commands with no-op actions for testing.
/// </summary>
static class TestCommands {
    public static void WireAll() {
        foreach (var cmd in Cmd.All) {
            cmd.Wire(Noop);
        }
    }

    private static void Noop() { }
}
