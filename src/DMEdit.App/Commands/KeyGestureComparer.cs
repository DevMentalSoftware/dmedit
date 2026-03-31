using System;
using System.Collections.Generic;
using Avalonia.Input;

namespace DMEdit.App.Commands;

/// <summary>
/// Equality comparer for <see cref="KeyGesture"/> based on Key + KeyModifiers.
/// Avalonia's KeyGesture does not override Equals/GetHashCode, so a custom
/// comparer is needed for dictionary lookups.
/// </summary>
public sealed class KeyGestureComparer : IEqualityComparer<KeyGesture> {
    public static readonly KeyGestureComparer Instance = new();

    public bool Equals(KeyGesture? x, KeyGesture? y) {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.Key == y.Key && x.KeyModifiers == y.KeyModifiers;
    }

    public int GetHashCode(KeyGesture obj) =>
        HashCode.Combine(obj.Key, obj.KeyModifiers);
}
