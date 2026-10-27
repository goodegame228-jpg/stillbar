using System.Text.Json;

namespace Stillbar.Core.Settings;

public sealed class JsonSettingsStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true, // hand edits: "ReserveStrip" works as well as "reserveStrip"
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // People edit this file by hand: keep "Москва" readable instead of М... (it is never served to a browser).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string FilePath { get; } = Path.Combine(directory, "settings.json");

    /// <summary>Missing or unreadable file means defaults; a broken file is kept aside as settings.json.bad, never overwritten silently.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new AppSettings();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
            return Migrate(s);
        }
        catch (JsonException)
        {
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            return new AppSettings();
        }
    }

    /// <summary>Atomic: write a temp file, then move it over, so a crash mid-write cannot leave half a file.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings with { Version = AppSettings.CurrentVersion }, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    private static AppSettings Migrate(AppSettings s) =>
        // Only version 1 exists. Future: if (s.Version < 2) s = ... ;
        s with { Version = AppSettings.CurrentVersion, Scale = Math.Clamp(s.Scale, 80, 200) };
}
