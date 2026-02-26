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
    public static IReadOnlyList<ProceduralSample> All { get; } = [
        new("[DEV] 100 lines",
            100L,
            i => $"Line {i + 1}: Sample content for interactive testing."),

        new("[DEV] 10 000 lines",
            10_000L,
            i => $"Line {i + 1:D5}: The quick brown fox jumps over the lazy dog."),

        new("[DEV] 1 000 000 lines",
            1_000_000L,
            i => $"Line {i + 1:D7}: Generated document stress test."),
    ];
}
