using System.Linq;
using System.Reflection;
using DMEdit.App.Services;
using DMEdit.App.Settings;

namespace DMEdit.App.Tests;

/// <summary>
/// Reflection smoke tests for <see cref="SettingsRegistry"/>.  Catches the
/// class of bug where a setting descriptor references a property that's been
/// renamed or removed from <see cref="AppSettings"/> — those bugs are silent
/// at compile time because the descriptors look the property up by string key.
/// </summary>
public class SettingsRegistryTests {
    [Fact]
    public void EveryDescriptorKey_ResolvesToRealAppSettingsProperty() {
        var props = typeof(AppSettings).GetProperties(
            BindingFlags.Public | BindingFlags.Instance);
        var propByName = props.ToDictionary(p => p.Name, p => p);

        var missing = new List<string>();
        foreach (var desc in SettingsRegistry.All) {
            if (!propByName.ContainsKey(desc.Key)) {
                missing.Add(desc.Key);
            }
        }

        Assert.True(missing.Count == 0,
            $"SettingsRegistry.All entries reference missing AppSettings properties: " +
            $"{string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryDescriptorKey_IsUnique() {
        var dup = SettingsRegistry.All
            .GroupBy(d => d.Key)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(dup.Count == 0,
            $"Duplicate SettingsRegistry keys: {string.Join(", ", dup)}");
    }

    [Fact]
    public void EveryDescriptor_HasCategoryInTheCategoriesList() {
        var valid = new HashSet<string>(SettingsRegistry.Categories);
        var mismatched = SettingsRegistry.All
            .Where(d => !valid.Contains(d.Category))
            .Select(d => $"{d.Key} -> {d.Category}")
            .ToList();
        Assert.True(mismatched.Count == 0,
            $"Descriptors reference undeclared categories: " +
            $"{string.Join(", ", mismatched)}");
    }

    [Fact]
    public void EveryDescriptorDefault_MatchesPropertyType() {
        var props = typeof(AppSettings).GetProperties(
            BindingFlags.Public | BindingFlags.Instance);
        var propByName = props.ToDictionary(p => p.Name, p => p);

        var mismatches = new List<string>();
        foreach (var desc in SettingsRegistry.All) {
            if (!propByName.TryGetValue(desc.Key, out var prop)) {
                // Already reported by the first test.
                continue;
            }
            var propType = System.Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            switch (desc.Kind) {
                case SettingKind.Bool:
                    if (propType != typeof(bool)) {
                        mismatches.Add($"{desc.Key}: Kind=Bool but property is {propType.Name}");
                    }
                    break;
                case SettingKind.Int:
                    if (propType != typeof(int)) {
                        mismatches.Add($"{desc.Key}: Kind=Int but property is {propType.Name}");
                    }
                    break;
                case SettingKind.Long:
                    if (propType != typeof(long)) {
                        mismatches.Add($"{desc.Key}: Kind=Long but property is {propType.Name}");
                    }
                    break;
                case SettingKind.Double:
                    if (propType != typeof(double)) {
                        mismatches.Add($"{desc.Key}: Kind=Double but property is {propType.Name}");
                    }
                    break;
                case SettingKind.Enum:
                    if (!propType.IsEnum) {
                        mismatches.Add($"{desc.Key}: Kind=Enum but property is {propType.Name}");
                    } else if (desc.EnumType != null && desc.EnumType != propType) {
                        mismatches.Add(
                            $"{desc.Key}: EnumType={desc.EnumType.Name} but property is {propType.Name}");
                    }
                    break;
            }
        }

        Assert.True(mismatches.Count == 0,
            $"SettingsRegistry type mismatches: {string.Join("; ", mismatches)}");
    }

    [Fact]
    public void EnabledWhenKey_ReferencesExistingDescriptor() {
        var allKeys = new HashSet<string>(SettingsRegistry.All.Select(d => d.Key));
        var broken = SettingsRegistry.All
            .Where(d => d.EnabledWhenKey is not null && !allKeys.Contains(d.EnabledWhenKey))
            .Select(d => $"{d.Key} -> EnabledWhenKey={d.EnabledWhenKey}")
            .ToList();
        Assert.True(broken.Count == 0,
            $"EnabledWhenKey references non-existent descriptor: " +
            $"{string.Join(", ", broken)}");
    }
}
