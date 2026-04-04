using System;

namespace DMEdit.App.Settings;

public enum SettingKind { Bool, Int, Long, Double, Enum }

/// <summary>
/// Metadata describing a single application setting: its name, type,
/// default value, constraints, and display information.
/// </summary>
public sealed record SettingDescriptor(
    string Key,
    string DisplayName,
    string? Description,
    string Category,
    SettingKind Kind,
    object DefaultValue,
    object? Min = null,
    object? Max = null,
    double Increment = 0.1,
    Type? EnumType = null,
    string? EnabledWhenKey = null,
    bool Hidden = false);
