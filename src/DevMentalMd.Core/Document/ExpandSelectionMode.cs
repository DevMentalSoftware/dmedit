namespace DevMentalMd.Core.Documents;

/// <summary>
/// Controls the hierarchy of levels used by <see cref="Document.ExpandSelection"/>.
/// </summary>
public enum ExpandSelectionMode {
    /// <summary>Whitespace boundaries → line → document.</summary>
    Word,

    /// <summary>Subword (camelCase/underscore) → whitespace → line → document.</summary>
    SubwordFirst,
}
