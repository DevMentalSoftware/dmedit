using System.Linq;
using System.Reflection;
using DevMentalMd.App.Commands;

namespace DevMentalMd.App.Tests;

public class CommandRegistryTests {
    [Fact]
    public void AllCommandIdsAreUnique() {
        var ids = CommandRegistry.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void AllDefaultGesturesAreUnique() {
        var gestures = CommandRegistry.All
            .Where(c => c.DefaultGesture != null)
            .Select(c => (c.Id, c.DefaultGesture!))
            .ToList();

        var comparer = KeyGestureComparer.Instance;
        var seen = new HashSet<Avalonia.Input.KeyGesture>(comparer);
        var duplicates = new List<string>();

        foreach (var (id, gesture) in gestures) {
            if (!seen.Add(gesture)) {
                duplicates.Add($"{id} ({gesture})");
            }
        }

        Assert.True(duplicates.Count == 0,
            $"Duplicate default gestures: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void AllCommandsHaveNonEmptyDisplayName() {
        foreach (var cmd in CommandRegistry.All) {
            Assert.False(string.IsNullOrWhiteSpace(cmd.DisplayName),
                $"Command {cmd.Id} has empty display name");
        }
    }

    [Fact]
    public void AllCommandCategoriesAreValid() {
        foreach (var cmd in CommandRegistry.All) {
            Assert.Contains(cmd.Category, CommandRegistry.Categories);
        }
    }

    [Fact]
    public void EveryCommandIdConstantAppearsInRegistry() {
        var fields = typeof(CommandIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        var registryIds = CommandRegistry.All.Select(c => c.Id).ToHashSet();

        // Every const in CommandIds should be in the registry.
        var missing = fields.Except(registryIds).ToList();
        Assert.True(missing.Count == 0,
            $"CommandIds constants not in registry: {string.Join(", ", missing)}");

        // Every registry entry should have a CommandIds constant.
        var extra = registryIds.Except(fields).ToList();
        Assert.True(extra.Count == 0,
            $"Registry entries without CommandIds constant: {string.Join(", ", extra)}");
    }

    [Fact]
    public void CommandIdFormatIsCategoryDotName() {
        foreach (var cmd in CommandRegistry.All) {
            Assert.StartsWith(cmd.Category + ".", cmd.Id);
        }
    }
}
