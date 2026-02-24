namespace DevMentalMd.Core.Documents;

/// <summary>Which of the two piece-table buffers a piece references.</summary>
public enum BufferKind {
    /// <summary>The original read-only buffer containing the file's initial content.</summary>
    Original,
    /// <summary>The append-only add buffer containing all inserted text.</summary>
    Add
}
