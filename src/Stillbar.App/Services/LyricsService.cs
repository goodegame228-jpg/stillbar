using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stillbar.Core.Lyrics;
using Stillbar.Core.Media;

namespace Stillbar.App.Services;

public interface ILyricsService
{
    Task<LyricsResult> GetAsync(MediaSnapshot track, CancellationToken ct);
}

/// <summary>
/// LRCLIB with a disk cache in %LocalAppData%\Stillbar\cache\lyrics. Found lyrics are kept for good;
/// "not found" is kept for 3 days so we do not ask again on every replay. Exact match first, then search.
/// </summary>
public sealed class LrcLibLyricsService : ILyricsService
{
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(3);
    private static readonly HttpClient Http = CreateClient();
    private readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stillbar", "cache", "lyrics");

    private sealed record CacheEntry(LyricsKind Kind, string? Text, DateTimeOffset At);

    public async Task<LyricsResult> GetAsync(MediaSnapshot track, CancellationToken ct)
    {
        if (track.Title.Length == 0 || track.Artist.Length == 0) return LyricsResult.NotFound;

        var file = Path.Combine(_cacheDir, Key(track) + ".json");
        if (ReadCache(file) is { } cached) return cached;

        LyricsResult result;
        try
        {
            result = await FetchAsync(track, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warn("lyrics", $"'{track.Artist} — {track.Title}': {ex.Message}");
            return LyricsResult.Unavailable; // network or server trouble: do not cache, try again next time
        }

        Log.Info("lyrics", $"'{track.Artist} — {track.Title}': {result.Kind}, {result.Lines.Count} lines");
        WriteCache(file, result);
        return result;
    }

    private static async Task<LyricsResult> FetchAsync(MediaSnapshot t, CancellationToken ct)
    {
        // Exact lookup needs album and duration (LRCLIB answers 400 without them), so only try it when we have both.
        if (t.Album.Length > 0 && t.Duration > TimeSpan.Zero)
        {
            var exact = await GetAsync(LrcLib.GetUrl(t.Artist, t.Title, t.Album, t.Duration), ct);
            if (exact is not null)
            {
                var r = LrcLib.ParseRecord(exact);
                if (r.Kind != LyricsKind.NotFound) return r;
            }
        }

        // Search by artist + title: album names differ ("Deluxe", "Remastered"), and players often send
        // every artist in one string ("KARADA, сложно найти") while LRCLIB files the song under the first.
        var artists = new[] { t.Artist, Artists.Primary(t.Artist) }.Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var artist in artists)
        {
            var json = await GetAsync(LrcLib.SearchUrl(artist, t.Title), ct);
            if (json is null) continue;
            var r = LrcLib.ParseSearch(json, t.Duration);
            if (r.Kind != LyricsKind.NotFound) return r;
        }
        return LyricsResult.NotFound;
    }

    /// <summary>Body of a 2xx answer; null for "no such thing" (4xx). Server trouble (5xx) throws, so it is not cached as "not found".</summary>
    private static async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync(ct);
        if ((int)resp.StatusCode >= 500) throw new HttpRequestException($"lrclib {(int)resp.StatusCode}");
        return null;
    }

    private static string Key(MediaSnapshot t)
    {
        var raw = $"{t.Artist.ToLowerInvariant()}|{t.Title.ToLowerInvariant()}|{(int)Math.Round(t.Duration.TotalSeconds)}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
    }

    private static LyricsResult? ReadCache(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var e = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file));
            if (e is null) return null;
            return e.Kind switch
            {
                LyricsKind.NotFound when DateTimeOffset.Now - e.At > NotFoundTtl => null,
                LyricsKind.NotFound => LyricsResult.NotFound,
                LyricsKind.Instrumental => LyricsResult.Instrumental,
                LyricsKind.Synced => new LyricsResult(LyricsKind.Synced, Lrc.Parse(e.Text ?? "")),
                _ => new LyricsResult(LyricsKind.Plain,
                    (e.Text ?? "").Split('\n').Where(l => l.Length > 0).Select(l => new LrcLine(TimeSpan.Zero, l)).ToList()),
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static void WriteCache(string file, LyricsResult r)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string? text = r.Kind switch
            {
                LyricsKind.Synced => string.Join('\n', r.Lines.Select(l => $"[{(int)l.At.TotalMinutes:00}:{l.At.Seconds:00}.{l.At.Milliseconds / 10:00}]{l.Text}")),
                LyricsKind.Plain => string.Join('\n', r.Lines.Select(l => l.Text)),
                _ => null,
            };
            File.WriteAllText(file, JsonSerializer.Serialize(new CacheEntry(r.Kind, text, DateTimeOffset.Now)));
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"lyrics cache: {ex.Message}");
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        // LRCLIB asks clients to identify themselves.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Stillbar/0.1 (desktop bar; personal project)");
        return http;
    }
}

/// <summary>Made-up synced lines for --snapshot renders.</summary>
public sealed class SampleLyricsService : ILyricsService
{
    public Task<LyricsResult> GetAsync(MediaSnapshot track, CancellationToken ct) => Task.FromResult(new LyricsResult(LyricsKind.Synced,
    [
        new LrcLine(TimeSpan.FromSeconds(40), "Фонари считают шаги по мосту"),
        new LrcLine(TimeSpan.FromSeconds(45), "Вода держит город на весу"),
        new LrcLine(TimeSpan.FromSeconds(51), "И трамвай уходит в темноту"),
    ]));
}
