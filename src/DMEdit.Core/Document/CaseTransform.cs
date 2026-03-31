namespace DMEdit.Core.Documents;

/// <summary>Text case transformations for <see cref="Document.TransformCase"/>.</summary>
public enum CaseTransform {
    Upper,
    Lower,
    /// <summary>Title case: first letter of each word capitalized, rest lowered.</summary>
    Proper,
}
