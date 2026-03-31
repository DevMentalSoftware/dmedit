using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DMEdit.Core.Blocks;

/// <summary>
/// A single block in a <see cref="BlockDocument"/>. Owns its text content
/// and inline formatting spans. All text editing is scoped to one block.
///
/// <b>Text storage — pristine vs. dirty:</b>
/// When loaded from a file, a block is <b>pristine</b>: its text is a
/// <see cref="ReadOnlyMemory{T}"/> slice of the original load buffer.
/// No per-block string allocation occurs. The first edit promotes the
/// block to <b>dirty</b> state, materializing a mutable <c>string</c>.
/// Use <see cref="TextMemory"/> for zero-copy access (file I/O, streaming)
/// and <see cref="Text"/> when a <c>string</c> is needed (rendering,
/// assertions).
///
/// <b>Content rules by type:</b>
/// <list type="bullet">
///   <item>Paragraph, Headings: text never contains <c>\n</c>. Single logical line;
///         multi-line only through word wrapping. <see cref="InsertText"/> rejects
///         newlines for these types.</item>
///   <item>CodeBlock: text routinely contains <c>\n</c>. Each <c>\n</c>-delimited
///         segment is an independent line. Enter inserts <c>\n</c>, does not split.</item>
///   <item>Other types: treated like paragraphs (no newlines) for now.</item>
/// </list>
/// </summary>
public sealed class Block {
    private ReadOnlyMemory<char> _textMemory;
    private string? _materializedText; // null while pristine and unread; cached after first Text access or edit
    private bool _isDirty; // true once any mutation has occurred
    private readonly List<InlineSpan> _spans = [];

    /// <summary>The block type (heading, paragraph, code, etc.).</summary>
    public BlockType Type { get; set; }

    /// <summary>
    /// The block's text content as a <c>string</c>. Lazily materialized from
    /// <see cref="TextMemory"/> on first access. Prefer <see cref="TextMemory"/>
    /// for zero-copy streaming (e.g., file save).
    /// </summary>
    public string Text => _materializedText ??= new string(_textMemory.Span);

    /// <summary>
    /// Zero-copy access to the block's text. For pristine blocks loaded from a
    /// file, this is a slice of the original load buffer — no allocation. For
    /// dirty blocks, this wraps the owned <c>string</c>.
    /// </summary>
    public ReadOnlyMemory<char> TextMemory => _textMemory;

    /// <summary>Character count of the text.</summary>
    public int Length => _textMemory.Length;

    /// <summary>Read-only view of the inline formatting spans.</summary>
    public IReadOnlyList<InlineSpan> Spans => _spans;

    /// <summary>
    /// Optional metadata. For <see cref="BlockType.CodeBlock"/>, holds the
    /// language tag (e.g., "csharp"). For <see cref="BlockType.Image"/>,
    /// holds the image URL. Null for types that don't need it.
    /// </summary>
    public string? Metadata { get; set; }

    /// <summary>
    /// Indentation level for hierarchical structures (nested lists, block
    /// quote groups). 0 = top level. The renderer multiplies this by an
    /// indent width to produce visual indentation. Tab/Shift+Tab adjusts
    /// this value for list items.
    /// </summary>
    public int IndentLevel { get; set; }

    /// <summary>Fired when text or spans change.</summary>
    public event EventHandler? Changed;

    // -----------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------

    /// <summary>
    /// Creates a block with owned <c>string</c> text. The block starts
    /// in dirty state (owns its text). Used for programmatic creation,
    /// tests, and new blocks created during editing.
    /// </summary>
    public Block(BlockType type, string text = "") {
        Type = type;
        _materializedText = text;
        _textMemory = text.AsMemory();
        _isDirty = true; // string-constructed blocks own their text
    }

    /// <summary>
    /// Creates a pristine block whose text is a zero-copy slice of an
    /// existing buffer (typically the file-load string). No per-block
    /// allocation occurs. The first edit promotes to dirty state.
    /// </summary>
    internal Block(BlockType type, ReadOnlyMemory<char> text) {
        Type = type;
        _textMemory = text;
        _materializedText = null; // pristine — no string allocated
    }

    /// <summary>
    /// Returns true if this block type allows newline characters in its text.
    /// Currently only <see cref="BlockType.CodeBlock"/> allows them.
    /// </summary>
    public bool AllowsNewlines => Type == BlockType.CodeBlock;

    /// <summary>
    /// True if the block has not been edited since creation. Its text is a
    /// zero-copy slice of the original load buffer. Reading <see cref="Text"/>
    /// caches a string but does not make the block dirty.
    /// </summary>
    public bool IsPristine => !_isDirty;

    // -----------------------------------------------------------------
    // Text editing
    // -----------------------------------------------------------------

    /// <summary>
    /// Inserts <paramref name="text"/> at the given character offset within
    /// this block. Adjusts all spans accordingly. Promotes to dirty state.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown if <paramref name="text"/> contains newline characters and this
    /// block type does not allow them (see <see cref="AllowsNewlines"/>).
    /// </exception>
    public void InsertText(int offset, string text) {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);
        if (text.Length == 0) {
            return;
        }
        if (!AllowsNewlines && text.Contains('\n')) {
            throw new ArgumentException(
                $"Block type {Type} does not allow newline characters. " +
                "Use BlockDocument.SplitBlock() to create a new block instead.",
                nameof(text));
        }

        SetTextInternal(Text.Insert(offset, text));
        AdjustSpansForInsert(offset, text.Length);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Deletes <paramref name="length"/> characters starting at <paramref name="offset"/>.
    /// Adjusts all spans accordingly. Spans fully within the deleted range are removed.
    /// Promotes to dirty state.
    /// </summary>
    public void DeleteText(int offset, int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0) {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, Length);

        SetTextInternal(Text.Remove(offset, length));
        AdjustSpansForDelete(offset, length);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the entire text content. Clears all spans. Promotes to dirty state.
    /// For non-code blocks, newlines are stripped.
    /// </summary>
    public void SetText(string text) {
        var cleaned = !AllowsNewlines ? text.Replace("\n", "").Replace("\r", "") : text;
        SetTextInternal(cleaned);
        _spans.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Writes the block's text to a <see cref="TextWriter"/> without allocating
    /// a <c>string</c> if the block is pristine. Use this for file save / export.
    /// </summary>
    public void WriteTo(TextWriter writer) {
        if (_materializedText != null) {
            writer.Write(_materializedText);
        } else {
            writer.Write(_textMemory.Span);
        }
    }

    // -----------------------------------------------------------------
    // Inline span management
    // -----------------------------------------------------------------

    /// <summary>
    /// Applies an inline formatting span. If an adjacent or overlapping span
    /// of the same type exists, they are merged.
    /// </summary>
    public void ApplySpan(InlineSpanType type, int start, int length, string? url = null) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0) {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, Length);

        var end = start + length;

        // Remove any existing spans of the same type that overlap or are adjacent
        var merged = _spans
            .Where(s => s.Type == type && s.Start <= end && s.End >= start)
            .ToList();

        var newStart = start;
        var newEnd = end;
        foreach (var s in merged) {
            newStart = Math.Min(newStart, s.Start);
            newEnd = Math.Max(newEnd, s.End);
            _spans.Remove(s);
        }

        _spans.Add(new InlineSpan(type, newStart, newEnd - newStart, url));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes formatting of the given type from the specified range.
    /// Spans that partially overlap are trimmed. Spans fully within are removed.
    /// Spans that straddle the range are split into two.
    /// </summary>
    public void RemoveSpan(InlineSpanType type, int start, int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length == 0) {
            return;
        }
        var end = start + length;

        var affected = _spans.Where(s => s.Type == type && s.Overlaps(start, length)).ToList();
        foreach (var s in affected) {
            _spans.Remove(s);

            // Left remnant: part of span before the removed range
            if (s.Start < start) {
                _spans.Add(new InlineSpan(s.Type, s.Start, start - s.Start, s.Url));
            }
            // Right remnant: part of span after the removed range
            if (s.End > end) {
                _spans.Add(new InlineSpan(s.Type, end, s.End - end, s.Url));
            }
        }

        if (affected.Count > 0) {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Returns true if the character at <paramref name="offset"/> has the
    /// given inline formatting applied.
    /// </summary>
    public bool HasSpanAt(InlineSpanType type, int offset) {
        return _spans.Any(s => s.Type == type && s.Start <= offset && offset < s.End);
    }

    // -----------------------------------------------------------------
    // Span adjustment (private helpers)
    // -----------------------------------------------------------------

    /// <summary>
    /// Adjusts all spans after a text insertion. Spans starting at or after
    /// the insert point shift right; spans straddling the insert point expand.
    /// </summary>
    private void AdjustSpansForInsert(int offset, int length) {
        for (var i = 0; i < _spans.Count; i++) {
            var s = _spans[i];
            if (s.Start >= offset) {
                // Span starts at or after insert — shift right
                _spans[i] = s.Shift(length);
            } else if (s.End > offset) {
                // Insert is inside the span — expand it
                _spans[i] = s.Resize(length);
            }
            // else: span ends at or before insert point — no change
        }
    }

    /// <summary>
    /// Adjusts all spans after a text deletion. Spans fully within the
    /// deleted range are removed. Spans straddling the range are trimmed or
    /// shrunk. Spans after the range shift left.
    /// </summary>
    private void AdjustSpansForDelete(int offset, int length) {
        var delEnd = offset + length;
        for (var i = _spans.Count - 1; i >= 0; i--) {
            var s = _spans[i];

            if (s.Start >= delEnd) {
                // Entirely after delete — shift left
                _spans[i] = s.Shift(-length);
            } else if (s.End <= offset) {
                // Entirely before delete — no change
            } else if (s.Start >= offset && s.End <= delEnd) {
                // Fully within delete range — remove
                _spans.RemoveAt(i);
            } else if (s.Start < offset && s.End > delEnd) {
                // Delete is fully within span — shrink
                _spans[i] = s.Resize(-length);
            } else if (s.Start < offset) {
                // Delete overlaps end of span — trim end
                _spans[i] = s with { Length = offset - s.Start };
            } else {
                // Delete overlaps start of span — trim start and shift
                var trimmed = delEnd - s.Start;
                _spans[i] = new InlineSpan(s.Type, offset, s.Length - trimmed, s.Url);
            }
        }
    }

    // -----------------------------------------------------------------
    // Split / merge helpers (used by BlockDocument)
    // -----------------------------------------------------------------

    /// <summary>
    /// Splits this block at the given offset. Returns a new block containing
    /// the text and spans from <paramref name="offset"/> onward. This block
    /// is truncated to [0, offset). The new block has the same type.
    /// Both halves preserve pristine state if the original block was pristine
    /// (they become slices of the same underlying buffer).
    /// </summary>
    internal Block SplitAt(int offset) {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);

        var rightBlock = new Block(Type, _textMemory[offset..]) {
            Metadata = Metadata,
            IndentLevel = IndentLevel,
        };

        // Partition spans
        var rightSpans = new List<InlineSpan>();
        var leftSpans = new List<InlineSpan>();

        foreach (var s in _spans) {
            if (s.End <= offset) {
                // Entirely in the left block
                leftSpans.Add(s);
            } else if (s.Start >= offset) {
                // Entirely in the right block (shift by -offset)
                rightSpans.Add(s.Shift(-offset));
            } else {
                // Straddles the split point — split the span
                leftSpans.Add(s with { Length = offset - s.Start });
                rightSpans.Add(new InlineSpan(s.Type, 0, s.End - offset, s.Url));
            }
        }

        // Truncate this block's text — preserves pristine state
        _textMemory = _textMemory[..offset];
        _materializedText = null; // invalidate cached string
        _spans.Clear();
        _spans.AddRange(leftSpans);

        rightBlock._spans.AddRange(rightSpans);

        Changed?.Invoke(this, EventArgs.Empty);
        return rightBlock;
    }

    /// <summary>
    /// Appends the text and spans from <paramref name="other"/> to the end
    /// of this block. The other block is not modified. Promotes to dirty state
    /// (concatenation requires a new string).
    /// </summary>
    internal void MergeFrom(Block other) {
        var offsetShift = Length;
        SetTextInternal(Text + other.Text);

        foreach (var s in other._spans) {
            _spans.Add(s.Shift(offsetShift));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Sets the text to a new string, updating both the memory view and
    /// the materialized string cache. The block becomes dirty.
    /// </summary>
    private void SetTextInternal(string newText) {
        _materializedText = newText;
        _textMemory = newText.AsMemory();
        _isDirty = true;
    }
}
