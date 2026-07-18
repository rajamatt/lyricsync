using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyricSync.Models;

namespace LyricSync.Services;

/// <summary>
/// Fetches time-synced lyrics from LRCLIB (https://lrclib.net) — a free, open lyrics
/// database that requires no API key — with an in-memory and on-disk cache.
/// </summary>
public sealed class LyricsService
{
    private const string BaseUrl = "https://lrclib.net/api";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LyricsResult> _memoryCache = new();
    private readonly string _cacheDir;

    public LyricsService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("LyricSync/1.0");

        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LyricSync", "lyrics-cache");
    }

    /// <summary>Wipes both the in-memory and on-disk lyrics caches.</summary>
    public void ClearCache()
    {
        _memoryCache.Clear();
        try
        {
            if (Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }
        }
        catch
        {
            // Best effort — a locked file just means some entries survive until next time.
        }
    }

    public async Task<LyricsResult> GetLyricsAsync(TrackInfo track, CancellationToken ct)
    {
        var cacheKey = $"{track.Key}\n{track.DurationSeconds}";
        if (_memoryCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var result = await FetchAsync(track, ct);

        // Negative results are kept in memory only, so a transient failure
        // does not permanently mark a song as lyric-less on disk.
        _memoryCache[cacheKey] = result;
        return result;
    }

    private async Task<LyricsResult> FetchAsync(TrackInfo track, CancellationToken ct)
    {
        var diskCached = TryReadDiskCache(track);
        if (diskCached is not null)
        {
            return BuildResult(diskCached);
        }

        try
        {
            var record = await QueryLrclibAsync(track, ct);
            if (record is null)
            {
                return LyricsResult.NotFound;
            }

            WriteDiskCache(track, record);
            return BuildResult(record);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LyricsResult.Error;
        }
    }

    private async Task<LrclibTrack?> QueryLrclibAsync(TrackInfo track, CancellationToken ct)
    {
        // 1. Exact lookup — LRCLIB matches with a small duration tolerance.
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

    private static LyricsResult BuildResult(LrclibTrack record)
    {
        if (record.Instrumental)
        {
            return LyricsResult.Instrumental;
        }

        if (!string.IsNullOrEmpty(record.SyncedLyrics))
        {
            var lines = LrcParser.Parse(record.SyncedLyrics);
            if (lines.Count > 0)
            {
                return new LyricsResult(LyricsStatus.Synced, lines, record.PlainLyrics);
            }
        }

        if (!string.IsNullOrEmpty(record.PlainLyrics))
        {
            return new LyricsResult(LyricsStatus.PlainOnly, [], record.PlainLyrics);
        }

        return LyricsResult.NotFound;
    }

    private string DiskCachePath(TrackInfo track)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(track.Key)));
        return Path.Combine(_cacheDir, $"{hash}.json");
    }

    private LrclibTrack? TryReadDiskCache(TrackInfo track)
    {
        try
        {
            var path = DiskCachePath(track);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<LrclibTrack>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void WriteDiskCache(TrackInfo track, LrclibTrack record)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(DiskCachePath(track), JsonSerializer.Serialize(record, JsonOptions));
        }
        catch
        {
            // Cache writes are best-effort.
        }
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
