namespace LyricSync.Models;

/// <summary>A single timed lyric line parsed from an LRC document.</summary>
public sealed record LyricLine(TimeSpan Start, string Text);
