using System.Text;
using System.Text.Json;
using LyricSync.Models;

namespace LyricSync.Services;

/// <summary>
/// Fallback provider: Kugou (krcs.kugou.com) — free public lyrics API, no key. The
/// search endpoint is duration-aware and returns candidates with an id + access key;
/// the download endpoint returns base64-encoded LRC. Same guardrails as NetEase:
/// title/artist match required, closest duration first, several candidates tried.
/// </summary>
public sealed class KugouLyricsProvider : ILyricsProvider
{
    private const int MaxCandidatesToTry = 3;

    private readonly HttpClient _http;

    public KugouLyricsProvider(HttpClient http) => _http = http;

    public string Name => "Kugou";

    public async Task<ProviderLyrics?> TryGetAsync(TrackInfo track, CancellationToken ct)
    {
        var keyword = Uri.EscapeDataString(LyricsCandidateMatcher.SearchQuery(track));
        var duration = track.DurationSeconds > 0 ? (track.DurationSeconds * 1000).ToString() : string.Empty;

        using var searchDoc = await GetJsonAsync(
            $"http://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword={keyword}&duration={duration}&hash=", ct);

        if (searchDoc is null
            || !searchDoc.RootElement.TryGetProperty("candidates", out var candidatesProp)
            || candidatesProp.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = new List<(string Id, string AccessKey, double DurationSeconds)>();
        foreach (var candidate in candidatesProp.EnumerateArray())
        {
            var song = candidate.TryGetProperty("song", out var s) ? s.GetString() : null;
            var singer = candidate.TryGetProperty("singer", out var g) ? g.GetString() : null;
            if (!LyricsCandidateMatcher.Matches(track, song, singer))
            {
                continue;
            }

            var id = candidate.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var accessKey = candidate.TryGetProperty("accesskey", out var keyProp) ? keyProp.GetString() : null;
            var durationMs = candidate.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDouble()
                : 0;

            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(accessKey))
            {
                candidates.Add((id!, accessKey!, durationMs / 1000.0));
            }
        }

        foreach (var (id, accessKey, _) in LyricsCandidateMatcher
                     .ByDurationProximity(candidates, c => c.DurationSeconds, track)
                     .Take(MaxCandidatesToTry))
        {
            ct.ThrowIfCancellationRequested();

            using var downloadDoc = await GetJsonAsync(
                $"http://lyrics.kugou.com/download?ver=1&client=pc&id={id}&accesskey={accessKey}&fmt=lrc&charset=utf8", ct);

            var contentBase64 = downloadDoc is not null
                && downloadDoc.RootElement.TryGetProperty("content", out var content)
                    ? content.GetString()
                    : null;

            if (string.IsNullOrEmpty(contentBase64))
            {
                continue;
            }

            string lrc;
            try
            {
                lrc = Encoding.UTF8.GetString(Convert.FromBase64String(contentBase64));
            }
            catch (FormatException)
            {
                continue;
            }

            if (LrcParser.Parse(lrc).Count >= 4)
            {
                return new ProviderLyrics(lrc, null, Instrumental: false);
            }
        }

        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
