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
                    return new ChordGesture(
                        KeyGesture.Parse(parts[0]),
                        KeyGesture.Parse(parts[1]));
                } catch {
                    // Fall through to single-key parse.
                }
            }
        }
        try {
            return new ChordGesture(KeyGesture.Parse(text));
        } catch {
            return null;
        }
    }
}
