using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyricSync.Models;

namespace LyricSync.Services;

/// <summary>
/// Orchestrates the lyrics providers: LRCLIB first (best quality), then NetEase and
/// Kugou as fallbacks for recent/lesser-known tracks. The first provider returning
/// parseable synced lyrics wins; plain (unsynced) lyrics are kept as a last resort.
/// Results are cached in memory and on disk.
/// </summary>
public sealed class LyricsService
{
    private static readonly TimeSpan PerProviderTimeout = TimeSpan.FromSeconds(8);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly ConcurrentDictionary<string, LyricsResult> _memoryCache = new();
    private readonly string _cacheDir;

    public LyricsService()
    {
        // Cookies must stay OFF: NetEase sets session cookies on the first response and
        // then serves trending/unrelated results to searches carrying them (anti-scraping).
        // With no cookie jar every request looks fresh, matching plain curl behavior.
        _http = new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("LyricSync/1.0");

        _providers =
        [
            new LrclibProvider(_http),
            new NeteaseLyricsProvider(_http),
            new KugouLyricsProvider(_http),
        ];

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

        CachedLyrics? plainFallback = null;
        var anyProviderFailed = false;

        foreach (var provider in _providers)
        {
            ct.ThrowIfCancellationRequested();

            ProviderLyrics? found;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(PerProviderTimeout);
                found = await provider.TryGetAsync(track, timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // real cancellation (track changed), not a provider timeout
            }
            catch
            {
                anyProviderFailed = true;
                continue;
            }

            if (found is null)
            {
                continue;
            }

            if (found.Instrumental)
            {
                var instrumental = new CachedLyrics { Instrumental = true, Source = provider.Name };
                WriteDiskCache(track, instrumental);
                return BuildResult(instrumental);
            }

            if (!string.IsNullOrEmpty(found.SyncedLrc) && LrcParser.Parse(found.SyncedLrc).Count > 0)
            {
                var synced = new CachedLyrics
                {
                    SyncedLyrics = found.SyncedLrc,
                    PlainLyrics = found.PlainLyrics,
                    Source = provider.Name,
                };
                WriteDiskCache(track, synced);
                return BuildResult(synced);
            }

            if (plainFallback is null && !string.IsNullOrEmpty(found.PlainLyrics))
            {
                plainFallback = new CachedLyrics { PlainLyrics = found.PlainLyrics, Source = provider.Name };
            }
        }

        if (plainFallback is not null)
        {
            WriteDiskCache(track, plainFallback);
            return BuildResult(plainFallback);
        }

        return anyProviderFailed ? LyricsResult.Error : LyricsResult.NotFound;
    }

    private static LyricsResult BuildResult(CachedLyrics record)
    {
        if (record.Instrumental)
        {
            return LyricsResult.Instrumental with { Source = record.Source };
        }

        if (!string.IsNullOrEmpty(record.SyncedLyrics))
        {
            var lines = LrcParser.Parse(record.SyncedLyrics);
            if (lines.Count > 0)
            {
                return new LyricsResult(LyricsStatus.Synced, lines, record.PlainLyrics, record.Source);
            }
        }

        if (!string.IsNullOrEmpty(record.PlainLyrics))
        {
            return new LyricsResult(LyricsStatus.PlainOnly, [], record.PlainLyrics, record.Source);
        }

        return LyricsResult.NotFound;
    }

    private string DiskCachePath(TrackInfo track)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(track.Key)));
        return Path.Combine(_cacheDir, $"{hash}.json");
    }

    private CachedLyrics? TryReadDiskCache(TrackInfo track)
    {
        try
        {
            var path = DiskCachePath(track);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CachedLyrics>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void WriteDiskCache(TrackInfo track, CachedLyrics record)
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

    /// <summary>
    /// On-disk cache entry. Property names intentionally match the pre-provider-chain
    /// format (which stored raw LRCLIB responses), so existing cache files keep working;
    /// their null Source is displayed as LRCLIB, which is what they came from.
    /// </summary>
    private sealed class CachedLyrics
    {
        public bool Instrumental { get; set; }
        public string? PlainLyrics { get; set; }
        public string? SyncedLyrics { get; set; }
        public string? Source { get; set; }
    }
}
