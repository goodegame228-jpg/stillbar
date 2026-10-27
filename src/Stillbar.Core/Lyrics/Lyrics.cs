using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stillbar.Core.Lyrics;

public sealed record LrcLine(TimeSpan At, string Text);

public enum LyricsKind
{
    NotFound,
    Instrumental,
    Plain,
    Synced,

    /// <summary>The service did not answer (network, 5xx). Never cached: try again next time.</summary>
    Unavailable,
}

/// <summary>Plain lyrics carry their lines with At = 0 and are shown without highlighting.</summary>
public sealed record LyricsResult(LyricsKind Kind, IReadOnlyList<LrcLine> Lines)
{
    public static readonly LyricsResult NotFound = new(LyricsKind.NotFound, []);
    public static readonly LyricsResult Instrumental = new(LyricsKind.Instrumental, []);
    public static readonly LyricsResult Unavailable = new(LyricsKind.Unavailable, []);
}

public static partial class Lrc
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    /// <summary>
    /// "[01:02.34]text" lines; several tags on one line repeat it; metadata tags ([ar:...]) are ignored.
    /// Lines come back sorted by time; empty texts are kept (they are pauses between verses).
    /// </summary>
    public static IReadOnlyList<LrcLine> Parse(string lrc)
    {
        var lines = new List<LrcLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int pos = 0;
            var times = new List<TimeSpan>();
            Match m;
            while ((m = TimeTag().Match(line, pos)).Success && m.Index == pos)
            {
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                int ms = m.Groups[3].Success ? int.Parse(m.Groups[3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) : 0;
                times.Add(new TimeSpan(0, 0, min, sec, ms));
                pos = m.Index + m.Length;
            }
            if (times.Count == 0) continue;
            var text = line[pos..].Trim();
            foreach (var t in times) lines.Add(new LrcLine(t, text));
        }
        return lines.OrderBy(l => l.At).ToList();
    }

    /// <summary>Index of the line being sung at <paramref name="position"/>; -1 before the first line.</summary>
    public static int IndexAt(IReadOnlyList<LrcLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].At <= position) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }
}

/// <summary>lrclib.net: free, keyless, community lyrics database.</summary>
public static class LrcLib
{
    public static string GetUrl(string artist, string title, string album, TimeSpan duration)
    {
        var sb = new StringBuilder("https://lrclib.net/api/get?artist_name=")
            .Append(Uri.EscapeDataString(artist))
            .Append("&track_name=").Append(Uri.EscapeDataString(title));
        if (album.Length > 0) sb.Append("&album_name=").Append(Uri.EscapeDataString(album));
        if (duration > TimeSpan.Zero) sb.Append("&duration=").Append(((int)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string SearchUrl(string artist, string title) =>
        $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";

    /// <summary>One record from /api/get.</summary>
    public static LyricsResult ParseRecord(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromRecord(doc.RootElement);
        }
        catch (JsonException)
        {
            return LyricsResult.NotFound;
        }
    }

    /// <summary>
    /// /api/search returns many records; take the first whose duration is within 3 s, preferring synced lyrics.
    /// Duration zero means unknown: accept any.
    /// </summary>
    public static LyricsResult ParseSearch(string json, TimeSpan duration)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return LyricsResult.NotFound;
            LyricsResult? plain = null;
            foreach (var rec in doc.RootElement.EnumerateArray())
            {
                if (duration > TimeSpan.Zero && rec.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    && Math.Abs(d.GetDouble() - duration.TotalSeconds) > 3) continue;
                var r = FromRecord(rec);
                if (r.Kind == LyricsKind.Synced) return r;
                if (r.Kind != LyricsKind.NotFound) plain ??= r;
            }
            return plain ?? LyricsResult.NotFound;
        }
        catch (JsonException)
        {
            return LyricsResult.NotFound;
        }
    }

    private static LyricsResult FromRecord(JsonElement rec)
    {
        if (rec.ValueKind != JsonValueKind.Object) return LyricsResult.NotFound;
        if (rec.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True) return LyricsResult.Instrumental;
        if (rec.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
        {
            var lines = Lrc.Parse(s.GetString()!);
            if (lines.Count > 0) return new LyricsResult(LyricsKind.Synced, lines);
        }
        if (rec.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String)
        {
            var lines = p.GetString()!.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)
                .Select(l => new LrcLine(TimeSpan.Zero, l)).ToList();
            if (lines.Count > 0) return new LyricsResult(LyricsKind.Plain, lines);
        }
        return LyricsResult.NotFound;
    }
}
