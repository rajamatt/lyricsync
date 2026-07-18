using System.Globalization;
using System.Text.RegularExpressions;
using LyricSync.Models;

namespace LyricSync.Services;

/// <summary>Parses standard and enhanced LRC documents into timed lines.</summary>
public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex TimeTagRegex();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>")]
    private static partial Regex InlineWordTagRegex();

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return [];
        }

        var lines = new List<LyricLine>();
        foreach (var rawLine in lrc.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var matches = TimeTagRegex().Matches(line);
            if (matches.Count == 0)
            {
                continue; // metadata tag ([ar:...], [ti:...]) or plain text
            }

            // Only honor the run of consecutive tags at the start of the line, so
            // square brackets inside the lyric text are not mistaken for timestamps.
            var leading = new List<Match>();
            var expectedIndex = line.Length - line.TrimStart().Length;
            foreach (Match match in matches)
            {
                if (match.Index != expectedIndex)
                {
                    break;
                }

                leading.Add(match);
                expectedIndex = match.Index + match.Length;
            }

            if (leading.Count == 0)
            {
                continue;
            }

            var text = line[expectedIndex..].Trim();
            text = InlineWordTagRegex().Replace(text, string.Empty).Trim();

            foreach (var tag in leading)
            {
                var minutes = int.Parse(tag.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = double.Parse(tag.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromSeconds(minutes * 60 + seconds), text));
            }
        }

        lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        return lines;
    }
}
