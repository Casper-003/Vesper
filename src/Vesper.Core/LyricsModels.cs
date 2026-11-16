namespace Vesper.Core;

public sealed record LyricsSyllable(int StartMs, int DurationMs, string Text);

public sealed record LyricsLine(
    int StartMs,
    int EndMs,
    string Text,
    string? Translation,
    IReadOnlyList<LyricsSyllable> Syllables,
    bool IsWordSynced);

public sealed record ParsedLyrics(
    IReadOnlyList<LyricsLine> Lines,
    bool HasWordSync,
    bool HasLineSync,
    string SourceFormat);

public readonly record struct FluidStagePalette(
    uint DeepBase,
    uint PrimaryOrb,
    uint SecondaryOrb,
    uint AccentOrb);

