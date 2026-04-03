namespace DMEdit.Rendering.Layout;

/// <summary>
/// Thrown when a line exceeds the maximum length that can be laid out in
/// normal mode.  The editor should catch this and suggest character-wrapping
/// mode to the user.
/// </summary>
public sealed class LineTooLongException : Exception {
    public int LineLength { get; }
    public int MaxLength { get; }

    public LineTooLongException(int lineLength, int maxLength)
        : base($"Line has {lineLength:N0} characters, which exceeds the layout limit of {maxLength:N0}. " +
               "Consider enabling character-wrapping mode for this file.") {
        LineLength = lineLength;
        MaxLength = maxLength;
    }
}
