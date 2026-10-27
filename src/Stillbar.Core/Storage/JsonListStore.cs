using System.Text.Json;

namespace Stillbar.Core.Storage;

/// <summary>
/// A list of records in one JSON file (clipboard history, shelf, notes, reminders). Same rules as dock.json:
/// missing file means empty, a broken file is kept aside as .bad, saving is atomic.
/// </summary>
public sealed class JsonListStore<T>(string directory, string fileName)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string FilePath { get; } = Path.Combine(directory, fileName);

    public List<T> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(FilePath), Json)?.Where(x => x is not null).ToList() ?? [];
        }
        catch (JsonException)
        {
            File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            return [];
        }
    }

    public void Save(IEnumerable<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items.ToList(), Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
