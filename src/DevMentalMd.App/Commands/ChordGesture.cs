using System;
using Avalonia.Input;

namespace DevMentalMd.App.Commands;

/// <summary>
/// A gesture binding that can be either a single keystroke or a
/// two-keystroke chord. When <see cref="Second"/> is null, it behaves
/// as a plain single-key gesture. When <see cref="Second"/> is set,
/// the user must press <see cref="First"/> then <see cref="Second"/>.
/// Implicit conversion from <see cref="KeyGesture"/> allows seamless
/// use in <see cref="CommandDescriptor"/> gesture slots.
/// </summary>
public sealed class ChordGesture {
    public KeyGesture First { get; }
    public KeyGesture? Second { get; }
    public bool IsChord => Second != null;

    public ChordGesture(KeyGesture first, KeyGesture? second = null) {
        First = first;
        Second = second;
    }

    public static implicit operator ChordGesture(KeyGesture gesture) => new(gesture);

    public override string ToString() =>
        Second != null ? $"{First}, {Second}" : First.ToString();

    /// <summary>
    /// Parses a gesture string that may be a chord (comma-separated) or a
    /// plain single-key gesture. Returns null if the string is malformed.
    /// </summary>
    public static ChordGesture? Parse(string text) {
        if (text.Contains(',')) {
            var parts = text.Split(',', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2) {
                try {
                    var first = KeyGesture.Parse(parts[0]);
                    var second = KeyGesture.Parse(parts[1]);
                    if (first.Key == Key.None || second.Key == Key.None)
                        return null;
                    return new ChordGesture(first, second);
                } catch {
                    // Fall through to single-key parse.
                }
            }
        }
        try {
            var single = KeyGesture.Parse(text);
            // Reject gestures with Key.None — typically caused by numeric
            // strings like "0" being parsed as enum value 0 rather than
            // the intended key (e.g. Key.D0 for the "0" key).
            return single.Key == Key.None ? null : new ChordGesture(single);
        } catch {
            return null;
        }
    }
}
