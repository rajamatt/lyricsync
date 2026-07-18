namespace LyricSync.Models;

public enum LyricsStatus
{
    Idle,
    Searching,
    Synced,
    PlainOnly,
    Instrumental,
    NotFound,
    Error,
}

public sealed record LyricsResult(LyricsStatus Status, IReadOnlyList<LyricLine> Lines, string? PlainLyrics)
{
    public static readonly LyricsResult NotFound = new(LyricsStatus.NotFound, [], null);
    public static readonly LyricsResult Error = new(LyricsStatus.Error, [], null);
    public static readonly LyricsResult Instrumental = new(LyricsStatus.Instrumental, [], null);
}
