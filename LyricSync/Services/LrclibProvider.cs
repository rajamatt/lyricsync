using System.Net;
using System.Text.Json;
using LyricSync.Models;

namespace LyricSync.Services;

/// <summary>
/// Primary provider: LRCLIB (https://lrclib.net) — free, open, no API key, high-quality
/// synced lyrics. Exact lookup by title/artist/album/duration first (the API applies a
/// small duration tolerance), then a fuzzy search fallback scored by duration proximity.
/// </summary>
public sealed class LrclibProvider : ILyricsProvider
{
    private const string BaseUrl = "https://lrclib.net/api";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public LrclibProvider(HttpClient http) => _http = http;

    public string Name => "LRCLIB";

    public async Task<ProviderLyrics?> TryGetAsync(TrackInfo track, CancellationToken ct)
    {
        var record = await QueryAsync(track, ct);
        if (record is null)
        {
            return null;
        }

        return new ProviderLyrics(record.SyncedLyrics, record.PlainLyrics, record.Instrumental);
    }

    private async Task<LrclibTrack?> QueryAsync(TrackInfo track, CancellationToken ct)
    {
        // 1. Exact lookup.
        if (track.DurationSeconds > 0)
        {
            var url = $"{BaseUrl}/get?track_name={Uri.EscapeDataString(track.Title)}" +
                      $"&artist_name={Uri.EscapeDataString(track.Artist)}" +
                      $"&album_name={Uri.EscapeDataString(track.Album)}" +
                      $"&duration={track.DurationSeconds}";

            var exact = await GetJsonAsync<LrclibTrack>(url, ct);
            if (exact is not null)
            {
                return exact;
            }
        }

        // 2. Fuzzy search fallback.
        var searchUrl = $"{BaseUrl}/search?track_name={Uri.EscapeDataString(track.Title)}" +
                        $"&artist_name={Uri.EscapeDataString(track.Artist)}";
        var candidates = await GetJsonAsync<List<LrclibTrack>>(searchUrl, ct);
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(c => !string.IsNullOrEmpty(c.SyncedLyrics))
            .ThenBy(c => track.DurationSeconds > 0 && c.Duration is > 0
                ? Math.Abs(c.Duration.Value - track.DurationSeconds)
                : double.MaxValue)
            .FirstOrDefault(c => !string.IsNullOrEmpty(c.SyncedLyrics)
                || !string.IsNullOrEmpty(c.PlainLyrics)
                || c.Instrumental);
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
        where T : class
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private sealed class LrclibTrack
    {
        public string? TrackName { get; set; }
        public string? ArtistName { get; set; }
        public string? AlbumName { get; set; }
        public double? Duration { get; set; }
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
    }
}
