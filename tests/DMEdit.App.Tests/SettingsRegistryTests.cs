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
    public void EveryDescriptorDefault_IsAssignableToProperty() {
        var props = typeof(AppSettings).GetProperties(
            BindingFlags.Public | BindingFlags.Instance);
        var propByName = props.ToDictionary(p => p.Name, p => p);

        var mismatches = new List<string>();
        foreach (var desc in SettingsRegistry.All) {
            if (!propByName.TryGetValue(desc.Key, out var prop)) {
                continue;
            }
            var propType = System.Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var defaultType = desc.BoxedDefault.GetType();

            // The generic SettingDescriptor<T> guarantees Kind matches T,
            // but we still verify the property type matches the boxed default
            // since the Key→property mapping is string-based.
            if (!propType.IsAssignableFrom(defaultType)) {
                mismatches.Add(
                    $"{desc.Key}: default is {defaultType.Name} but property is {propType.Name}");
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
