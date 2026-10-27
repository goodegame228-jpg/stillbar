using Stillbar.Core.Settings;

namespace Stillbar.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stillbar-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_gives_defaults() =>
        Assert.Equal(new AppSettings(), new JsonSettingsStore(_dir).Load());

    [Fact]
    public void Save_then_load_round_trips()
    {
        var store = new JsonSettingsStore(_dir);
        var s = new AppSettings { Scale = 150, City = new CitySetting("Казань", 55.79, 49.12) };
        store.Save(s);
        Assert.Equal(s, store.Load());
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Cyrillic_is_written_as_is()
    {
        var store = new JsonSettingsStore(_dir);
        store.Save(new AppSettings());
        Assert.Contains("\"Москва\"", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Broken_file_is_kept_aside_and_defaults_used()
    {
        var store = new JsonSettingsStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Equal(new AppSettings(), store.Load());
        Assert.True(File.Exists(store.FilePath + ".bad"));
    }

    [Fact]
    public void Out_of_range_scale_is_clamped()
    {
        var store = new JsonSettingsStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.FilePath, """{ "version": 1, "scale": 500 }""");
        Assert.Equal(200, store.Load().Scale);
    }
}
