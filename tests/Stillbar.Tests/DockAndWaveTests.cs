using Stillbar.Core.Dock;
using Stillbar.Core.Motion;

namespace Stillbar.Tests;

public sealed class DockAndWaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stillbar-dock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Add_skips_duplicates_ignoring_case()
    {
        var list = DockStore.Add([new DockEntry(@"C:\Windows\explorer.exe")],
            [@"c:\windows\EXPLORER.EXE", @"C:\Windows\notepad.exe"]);
        Assert.Equal([@"C:\Windows\explorer.exe", @"C:\Windows\notepad.exe"], list.Select(e => e.Path));
    }

    [Fact]
    public void Add_stops_at_the_limit()
    {
        var paths = Enumerable.Range(0, 20).Select(i => $@"C:\a{i}.exe");
        Assert.Equal(DockStore.MaxItems, DockStore.Add([], paths).Count);
    }

    [Fact]
    public void Save_load_remove_round_trip()
    {
        var store = new DockStore(_dir);
        Assert.False(store.Exists);
        store.Save([new DockEntry(@"C:\Программы\a.exe"), new DockEntry(@"C:\b.lnk")]);
        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Contains("Программы", File.ReadAllText(store.FilePath));
        Assert.Equal([@"C:\b.lnk"], DockStore.Remove(loaded, @"c:\программы\A.EXE").Select(e => e.Path));
    }

    [Theory]
    [InlineData(@"C:\a.exe", true)]
    [InlineData(@"C:\a.lnk", true)]
    [InlineData(@"C:\a.txt", false)]
    public void Only_programs_and_shortcuts(string path, bool ok) => Assert.Equal(ok, DockStore.IsLaunchable(path));

    [Fact]
    public void Wave_meets_the_corners_and_stays_inside_the_shape()
    {
        var pts = Wave.BottomEdge(720, 300, 28, depth: 6, wavelength: 46, phase: 1.3);
        Assert.Equal(692, pts[0].X);
        Assert.Equal(300, pts[0].Y, 6);
        Assert.Equal((28.0, 300.0), pts[^1]);
        Assert.All(pts, p => Assert.InRange(p.Y, 294, 300)); // never below the bottom: WPF would clip it
        Assert.Contains(pts, p => 300 - p.Y > 4); // it actually waves in the middle
    }

    [Fact]
    public void Zero_amplitude_is_a_straight_edge() =>
        Assert.All(Wave.BottomEdge(720, 300, 28, 0, 46, 0), p => Assert.Equal(300, p.Y));
}
