using DevMentalMd.App.Commands;
using Cmd = DevMentalMd.App.Commands.Commands;

namespace DevMentalMd.App.Tests;

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
