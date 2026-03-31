using System.Runtime.InteropServices;
using DevMentalMd.Core.Collections;

namespace DevMentalMd.Core.Documents;

/// <summary>
/// Scans characters and builds line length entries with terminator type tracking.
/// Handles CR, LF, CRLF detection and pseudo-line splitting at
/// <see cref="MaxPseudoLine"/> content characters.
/// </summary>
/// <remarks>
/// Feed characters via <see cref="Scan(ReadOnlySpan{char})"/> (may be called
/// multiple times for chunked input), then call <see cref="Finish"/> to emit
/// the final line.  The result is available via <see cref="LineLengths"/>,
/// <see cref="DocLineLengths"/>, <see cref="TerminatorRuns"/>, and
/// <see cref="LineCount"/>.
///
/// <see cref="LineLengths"/> stores buf-space lengths (real buffer characters,
/// same as before).  <see cref="DocLineLengths"/> stores doc-space lengths
/// (pseudo-lines get +1 for the virtual terminator).
/// </remarks>
public sealed class LineScanner {
    private readonly int _maxPseudoLine;
    private readonly List<int> _lineLengths;     // buf-space lengths (primary)
    private readonly List<int> _docLineLengths;   // doc-space lengths (secondary)
    private readonly List<(long StartLine, LineTerminatorType Type)> _terminatorRuns = new();

    private int _currentLineLen;
    private bool _prevWasCr;
    private long _lineIndex;
    private LineTerminatorType _currentRunType = (LineTerminatorType)255; // sentinel: no run yet

    // Line-ending counters.
    private int _lfCount;
    private int _crlfCount;
    private int _crCount;

    // Indentation counters.
    private int _spaceIndentCount;
    private int _tabIndentCount;
    private bool _atLineStart = true;

    public LineScanner(int maxPseudoLine = -1, int estimatedLines = 16) {
        _maxPseudoLine = maxPseudoLine > 0 ? maxPseudoLine : PieceTable.MaxPseudoLine;
        _lineLengths = new List<int>(estimatedLines);
        _docLineLengths = new List<int>(estimatedLines);
    }

    /// <summary>Number of lines emitted so far (including the in-progress line).</summary>
    public long LineCount => _lineIndex + 1;

    /// <summary>Line-ending detection counts.</summary>
    public int LfCount => _lfCount;
    public int CrlfCount => _crlfCount;
    public int CrCount => _crCount;

    /// <summary>
    /// The accumulated buf-space line lengths (real buffer characters).
    /// Only complete after <see cref="Finish"/> is called.
    /// </summary>
    public List<int> LineLengths => _lineLengths;

    /// <summary>
    /// The accumulated doc-space line lengths (pseudo-lines get +1 virtual terminator).
    /// Only complete after <see cref="Finish"/> is called.
    /// </summary>
    public List<int> DocLineLengths => _docLineLengths;

    /// <summary>
    /// Run-length encoded terminator types.  Only complete after
    /// <see cref="Finish"/> is called.
    /// </summary>
    public List<(long StartLine, LineTerminatorType Type)> TerminatorRuns => _terminatorRuns;

    /// <summary>
    /// Scans a span of characters, emitting line entries as newlines and
    /// pseudo-line boundaries are encountered.
    /// </summary>
    public void Scan(ReadOnlySpan<char> data) {
        for (int i = 0; i < data.Length; i++) {
            var ch = data[i];
            if (ch == '\n') {
                if (_prevWasCr) {
                    // \r\n — upgrade previous CR line to CRLF.
                    _crlfCount++;
                    _lineLengths[^1]++;
                    _docLineLengths[^1]++;
                    UpgradeLastTerminatorToCRLF();
                    _prevWasCr = false;
                    _atLineStart = true;
                    continue;
                }
                _lfCount++;
                _currentLineLen++;
                RecordTerminator(LineTerminatorType.LF);
                _lineLengths.Add(_currentLineLen);
                _docLineLengths.Add(_currentLineLen);  // LF: doc == buf
                _lineIndex++;
                _currentLineLen = 0;
                _atLineStart = true;
            } else if (ch == '\r') {
                if (_prevWasCr) {
                    _crCount++;
                }
                _currentLineLen++;
                RecordTerminator(LineTerminatorType.CR);
                _lineLengths.Add(_currentLineLen);
                _docLineLengths.Add(_currentLineLen);  // CR: doc == buf
                _lineIndex++;
                _currentLineLen = 0;
                _prevWasCr = true;
                _atLineStart = true;
            } else {
                if (_prevWasCr) {
                    _crCount++;
                }
                _prevWasCr = false;

                // Pseudo-split when content reaches MaxPseudoLine.
                if (_currentLineLen >= _maxPseudoLine) {
                    RecordTerminator(LineTerminatorType.Pseudo);
                    _lineLengths.Add(_currentLineLen);           // buf: real chars only
                    _docLineLengths.Add(_currentLineLen + 1);    // doc: +1 virtual terminator
                    _lineIndex++;
                    _currentLineLen = 0;
                }

                _currentLineLen++;

                // Track indentation style: check first char of each line.
                if (_atLineStart) {
                    if (ch == ' ') _spaceIndentCount++;
                    else if (ch == '\t') _tabIndentCount++;
                    _atLineStart = false;
                }
            }
        }
    }

    /// <summary>
    /// Finalises the scan: handles trailing bare CR and emits the final line.
    /// Must be called exactly once after all <see cref="Scan"/> calls.
    /// </summary>
    public void Finish() {
        if (_prevWasCr) {
            _crCount++;
            _prevWasCr = false;
        }
        // Emit the final line (content after the last newline, may be empty).
        RecordTerminator(LineTerminatorType.None);
        _lineLengths.Add(_currentLineLen);
        _docLineLengths.Add(_currentLineLen);  // None: doc == buf
    }

    /// <summary>
    /// Builds a <see cref="LineIndexTree"/> from the accumulated line lengths.
    /// Call after <see cref="Finish"/>.
    /// </summary>
    public LineIndexTree BuildTree() =>
        LineIndexTree.FromValues(
            CollectionsMarshal.AsSpan(_lineLengths),
            CollectionsMarshal.AsSpan(_docLineLengths));

    /// <summary>
    /// Returns detected line ending info from the scan counts.
    /// </summary>
    public LineEndingInfo DetectedLineEnding =>
        LineEndingInfo.FromCounts(_lfCount, _crlfCount, _crCount);

    /// <summary>Indentation detection counts.</summary>
    public int SpaceIndentCount => _spaceIndentCount;
    public int TabIndentCount => _tabIndentCount;

    /// <summary>
    /// Returns detected indentation info from the scan counts.
    /// </summary>
    public IndentInfo DetectedIndent =>
        IndentInfo.FromCounts(_spaceIndentCount, _tabIndentCount);

    private void RecordTerminator(LineTerminatorType type) {
        if (_currentRunType != type) {
            _terminatorRuns.Add((_lineIndex, type));
            _currentRunType = type;
        }
    }

    private void UpgradeLastTerminatorToCRLF() {
        if (_terminatorRuns.Count > 0) {
            var last = _terminatorRuns[^1];
            if (last.StartLine == _lineIndex - 1
                && last.Type == LineTerminatorType.CR) {
                _terminatorRuns[^1] = (_lineIndex - 1, LineTerminatorType.CRLF);
                _currentRunType = LineTerminatorType.CRLF;
                return;
            }
        }
        RecordTerminator(LineTerminatorType.CRLF);
    }
}
