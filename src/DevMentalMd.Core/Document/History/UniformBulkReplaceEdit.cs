namespace DevMentalMd.Core.Documents.History;

/// <summary>
/// Records a bulk replace where all matches have the same length and the same
/// replacement string. Undo restores the saved piece list and line tree in O(1).
/// </summary>
public sealed class UniformBulkReplaceEdit : IDocumentEdit {
    private readonly long[] _matchPositions;
    private readonly int _matchLen;
    private readonly string _replacement;
    private readonly Piece[] _savedPieces;
    private readonly int[] _savedLineLengths;
    private readonly long _savedAddBufLen;

    public UniformBulkReplaceEdit(
        long[] matchPositions, int matchLen, string replacement,
        Piece[] savedPieces, int[] savedLineLengths, long savedAddBufLen) {
        _matchPositions = matchPositions;
        _matchLen = matchLen;
        _replacement = replacement;
        _savedPieces = savedPieces;
        _savedLineLengths = savedLineLengths;
        _savedAddBufLen = savedAddBufLen;
    }

    /// <summary>Match positions (sorted ascending).</summary>
    public long[] MatchPositions => _matchPositions;

    /// <summary>Length of each match (same for all).</summary>
    public int MatchLen => _matchLen;

    /// <summary>Replacement string (same for all matches).</summary>
    public string Replacement => _replacement;

    /// <summary>Number of matches.</summary>
    public int MatchCount => _matchPositions.Length;

    public void Apply(PieceTable table) {
        table.BulkReplace(_matchPositions, _matchLen, _replacement);
    }

    public void Revert(PieceTable table) {
        table.TrimAddBuffer(_savedAddBufLen);
        table.RestorePieces(_savedPieces);
        table.InstallLineTree(_savedLineLengths);
    }
}
