using System;

namespace DMEdit.App.Settings;

public enum SettingKind { Bool, Int, Long, Double, Enum }

/// <summary>
/// Non-generic interface for <see cref="SettingDescriptor{T}"/> so
/// heterogeneous descriptors can live in a single registry list.
/// </summary>
public interface ISettingDescriptor {
    string Key { get; }
    string DisplayName { get; }
    string? Description { get; }
    string Category { get; }
    SettingKind Kind { get; }
    object BoxedDefault { get; }
    object? BoxedMin { get; }
    object? BoxedMax { get; }
    double Increment { get; }
    Type? EnumType { get; }
    string? EnabledWhenKey { get; }
    bool Hidden { get; }
}

/// <summary>
/// Strongly-typed metadata describing a single application setting.
/// <see cref="SettingKind"/> and <see cref="EnumType"/> are derived from
/// <typeparamref name="T"/> so they cannot be mismatched.
/// </summary>
public sealed record SettingDescriptor<T>(
    string Key,
    string DisplayName,
    string? Description,
    string Category,
    T DefaultValue,
    T? Min = default,
    T? Max = default,
    double Increment = 0.1,
    string? EnabledWhenKey = null,
    bool Hidden = false) : ISettingDescriptor where T : struct {

    public SettingKind Kind => typeof(T) switch {
        var t when t == typeof(bool) => SettingKind.Bool,
        var t when t == typeof(int) => SettingKind.Int,
        var t when t == typeof(long) => SettingKind.Long,
        var t when t == typeof(double) => SettingKind.Double,
        var t when t.IsEnum => SettingKind.Enum,
        _ => throw new NotSupportedException($"Unsupported setting type: {typeof(T)}"),
    };

    public Type? EnumType => typeof(T).IsEnum ? typeof(T) : null;

    object ISettingDescriptor.BoxedDefault => DefaultValue;
    object? ISettingDescriptor.BoxedMin => Min;
    object? ISettingDescriptor.BoxedMax => Max;
}
