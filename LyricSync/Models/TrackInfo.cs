namespace LyricSync.Models;

/// <summary>Metadata of the track currently reported by the media session.</summary>
public sealed partial record TrackInfo(string Title, string Artist, string Album, int DurationSeconds, string SourceApp)
{
    /// <summary>Sentinel for "nothing playing".</summary>
    public static readonly TrackInfo None = new(string.Empty, string.Empty, string.Empty, 0, string.Empty);

    /// <summary>
    /// Bumped to force a re-fetch of the same song (e.g. after clearing the lyrics cache):
    /// it participates in record equality, so the Track feed sees a "new" value, while
    /// <see cref="Key"/>-based comparisons are unaffected.
    /// </summary>
    public int Generation { get; init; }

    public bool IsEmpty => Title.Length == 0;

    /// <summary>Identity used to detect track changes (duration is excluded, it can arrive late).</summary>
    public string Key { get; } = $"{Title}\n{Artist}".ToLowerInvariant();

    public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} — {Artist}";
}
