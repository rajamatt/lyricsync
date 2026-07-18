using System.Text.Json;
using LyricSync.Models;

namespace LyricSync.Services;

/// <summary>
/// Fallback provider: NetEase Cloud Music (music.163.com) — free public API, no key,
/// strong coverage of recent and lesser-known releases. Search returns loosely related
/// songs and some entries have no lyrics at all, so candidates are filtered by
/// title/artist match, ordered by duration proximity, and tried until one yields
/// parseable synced lines.
/// </summary>
public sealed class NeteaseLyricsProvider : ILyricsProvider
{
    private const int MaxCandidatesToTry = 3;

    private readonly HttpClient _http;

    public NeteaseLyricsProvider(HttpClient http) => _http = http;

    public string Name => "NetEase";

    public async Task<ProviderLyrics?> TryGetAsync(TrackInfo track, CancellationToken ct)
    {
        var query = Uri.EscapeDataString(LyricsCandidateMatcher.SearchQuery(track));
        using var searchDoc = await GetJsonAsync(
            $"https://music.163.com/api/search/get?s={query}&type=1&limit=10", ct);

        if (searchDoc is null
            || !searchDoc.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("songs", out var songs)
            || songs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = new List<(long Id, double DurationSeconds)>();
        foreach (var song in songs.EnumerateArray())
        {
            var title = song.TryGetProperty("name", out var n) ? n.GetString() : null;
            var artist = song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? string.Join(", ", artists.EnumerateArray()
                    .Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : null)
                    .Where(a => a is not null))
                : null;

            if (!LyricsCandidateMatcher.Matches(track, title, artist))
            {
                continue;
            }

            var id = song.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
            var durationMs = song.TryGetProperty("duration", out var d) ? d.GetDouble() : 0;
            if (id > 0)
            {
                candidates.Add((id, durationMs / 1000.0));
            }
        }

        foreach (var (id, _) in LyricsCandidateMatcher
                     .ByDurationProximity(candidates, c => c.DurationSeconds, track)
                     .Take(MaxCandidatesToTry))
        {
            ct.ThrowIfCancellationRequested();

            using var lyricDoc = await GetJsonAsync(
                $"https://music.163.com/api/song/lyric?os=pc&id={id}&lv=-1&kv=-1&tv=-1", ct);

            var lrc = lyricDoc is not null
                && lyricDoc.RootElement.TryGetProperty("lrc", out var lrcProp)
                && lrcProp.TryGetProperty("lyric", out var lyricProp)
                    ? lyricProp.GetString()
                    : null;

            // "暂无歌词" = "no lyrics yet" placeholder.
            if (!string.IsNullOrWhiteSpace(lrc) && !lrc.Contains("暂无歌词") && LrcParser.Parse(lrc).Count >= 4)
            {
                return new ProviderLyrics(lrc, null, Instrumental: false);
            }
        }

        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
