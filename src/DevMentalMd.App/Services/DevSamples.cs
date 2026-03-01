using System;
using System.Collections.Generic;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.App.Services;

/// <summary>A named procedural document usable for interactive developer testing.</summary>
public sealed record ProceduralSample(
    string DisplayName,
    long LineCount,
    Func<long, string> Generator) {

    /// <summary>
    /// Creates a <see cref="Document"/> backed by a <see cref="ProceduralBuffer"/>.
    /// The buffer generates content on demand — no large allocation occurs.
    /// </summary>
    public Document CreateDocument() =>
        new(new PieceTable(new ProceduralBuffer(LineCount, Generator)));
}

/// <summary>
/// Developer-mode sample documents shown in the Recent Files menu when
/// <see cref="DevMode.IsEnabled"/> is <see langword="true"/>.
/// </summary>
public static class DevSamples {
    // Phrases of varying lengths used to build realistic, varied paragraphs.
    private static readonly string[] Phrases = [
        "The quick brown fox jumps over the lazy dog.",
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua.",
        "In a hole in the ground there lived a hobbit.",
        "It was the best of times, it was the worst of times.",
        "To be or not to be, that is the question.",
        "All that glitters is not gold; not all those who wander are lost.",
        "The only thing we have to fear is fear itself.",
        "Debugging is twice as hard as writing the code in the first place.",
        "Any sufficiently advanced technology is indistinguishable from magic — Arthur C. Clarke.",
        "We choose to go to the Moon in this decade and do the other things, not because they are easy, but because they are hard.",
        "Ask not what your country can do for you; ask what you can do for your country.",
        "A journey of a thousand miles begins with a single step.",
        "Stay hungry, stay foolish.",
        "The unexamined life is not worth living.",
        "I think, therefore I am.",
        "That which does not kill us makes us stronger.",
    ];

    /// <summary>
    /// Generates a line for the complex million-line sample. Uses a simple
    /// hash of the line index to produce varied content: occasional headings,
    /// short lines, long wrapping paragraphs, and blank lines.
    /// </summary>
    private static string GenerateComplexLine(long i) {
        // Simple deterministic hash for variety (avoid System.Random for repeatability)
        var h = (uint)(i * 2654435761L); // Knuth multiplicative hash

        // Every 500 lines: a heading
        if (i % 500 == 0) {
            var level = (int)(h % 3) + 1; // H1–H3
            var hashes = new string('#', level);
            return $"{hashes} Section {i / 500 + 1} — {Phrases[h % (uint)Phrases.Length]}";
        }

        // Every 50 lines: a blank line (paragraph break)
        if (i % 50 == 0) {
            return "";
        }

        // Mix of short and long lines
        var phraseCount = (int)(h % 5) + 1; // 1–5 phrases per line
        var parts = new string[phraseCount];
        for (var p = 0; p < phraseCount; p++) {
            var idx = (int)((h + (uint)p * 7919) % (uint)Phrases.Length);
            parts[p] = Phrases[idx];
        }
        return $"[{i + 1:D7}] {string.Join(" ", parts)}";
    }

    public static IReadOnlyList<ProceduralSample> All { get; } = [
        new("100 lines",
            100L,
            i => $"Line {i + 1}: Sample content for interactive testing."),

        new("10,000 lines",
            10_000L,
            i => $"Line {i + 1:D5}: The quick brown fox jumps over the lazy dog."),

        new("1,000,000 lines",
            1_000_000L,
            i => $"Line {i + 1:D7}: All work and no play makes Jack a dull boy."),

        new("1,000,000 lines (complex)",
            1_000_000L,
            GenerateComplexLine),

        new("5,000,000 lines (complex)",
            5_000_000L,
            GenerateComplexLine),
    ];
}
