using Stillbar.App.Services;
using Stillbar.App.Shell;
using Stillbar.Core.Clipboard;
using Stillbar.Core.Shelf;

namespace Stillbar.App.Tests;

public sealed class FakeClipboard : IClipboardService
{
    public ClipHistory History { get; } = new();
    public List<string> Pasted { get; } = [];
    public bool Busy { get; set; }
    public event EventHandler? Changed;

    public void Copy(string text, string? source = null)
    {
        History.AddText(text, source, DateTimeOffset.Now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<ClipEntry> View(string? query) => History.View(query);
    public void SetPinned(string id, bool pinned) { History.SetPinned(id, pinned); Changed?.Invoke(this, EventArgs.Empty); }
    public void Remove(string id) { History.Remove(id); Changed?.Invoke(this, EventArgs.Empty); }
    public void Clear() { History.Clear(); Changed?.Invoke(this, EventArgs.Empty); }

    public bool Paste(ClipEntry entry)
    {
        if (Busy) return false;
        Pasted.Add(entry.Text);
        return true;
    }
}

public sealed class FakeShelf : IShelfService
{
    private List<ShelfItem> _items = [];
    public event EventHandler? Changed;
    public IReadOnlyList<ShelfItem> Items => _items;
    public void Add(IEnumerable<string> paths) { _items = ShelfList.Add(_items, paths, DateTimeOffset.Now); Changed?.Invoke(this, EventArgs.Empty); }
    public void Remove(string path) { _items = ShelfList.Remove(_items, path); Changed?.Invoke(this, EventArgs.Empty); }
}

/// <summary>The Clipboard tab (S05) on fakes: nothing here touches the real clipboard or keyboard.</summary>
[Collection("wpf")]
public class ClipboardTabTests(WpfHost host)
{
    private static (BarViewModel Vm, FakeClipboard Clip, FakeShelf Shelf) Make()
    {
        var clip = new FakeClipboard();
        var shelf = new FakeShelf();
        var vm = new BarViewModel(new BarServices(
            new FakeMedia(), new FakeVolume(), new FakeWeather(null), new FakeLyrics(), new FakeDownloads(), new FakeDock(), new FakeScreen(),
            Clipboard: clip, Shelf: shelf), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        return (vm, clip, shelf);
    }

    private static async Task Settle() => await Task.Delay(50);

    [Fact]
    public Task Empty_history_invites_copying() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make();
        await Settle();
        Assert.True(vm.HasNoClips);
        Assert.Equal("Скопируйте что-нибудь — оно появится здесь.", vm.ClipEmptyText);
    });

    [Fact]
    public Task Copied_text_shows_as_one_line_with_source_and_time() => host.RunAsync(async () =>
    {
        var (vm, clip, _) = Make();
        clip.Copy("первая строка\r\n   вторая", "Блокнот");
        await Settle();
        var row = Assert.Single(vm.ClipRows);
        Assert.Equal("первая строка вторая", row.Text);
        Assert.Equal("Блокнот · только что", row.Meta);
        Assert.False(vm.HasNoClips);
    });

    [Fact]
    public Task Search_filters_and_says_when_nothing_matches() => host.RunAsync(async () =>
    {
        var (vm, clip, _) = Make();
        clip.Copy("кофе");
        clip.Copy("чай");
        await Settle();
        vm.ClipQuery = "КОФ";
        Assert.Equal(["кофе"], vm.ClipRows.Select(r => r.Text));
        vm.ClipQuery = "сок";
        Assert.True(vm.HasNoClips);
        Assert.Equal("Ничего не нашлось.", vm.ClipEmptyText);
    });

    [Fact]
    public Task Click_pastes_after_handing_focus_back() => host.RunAsync(async () =>
    {
        var (vm, clip, _) = Make();
        clip.Copy("вставь меня");
        await Settle();
        var order = new List<string>();
        vm.ReturnFocusRequested += (_, _) => order.Add("focus");
        vm.PasteClipCommand.Execute(vm.ClipRows[0]);
        order.Add(clip.Pasted.Count == 1 ? "pasted" : "not pasted");
        Assert.Equal(["focus", "pasted"], order);
        Assert.Equal("Вставлено", vm.StatusText);
    });

    [Fact]
    public Task Busy_clipboard_is_reported() => host.RunAsync(async () =>
    {
        var (vm, clip, _) = Make();
        clip.Copy("x");
        clip.Busy = true;
        await Settle();
        vm.PasteClipCommand.Execute(vm.ClipRows[0]);
        Assert.Equal("Буфер обмена занят. Попробуйте ещё раз", vm.StatusText);
    });

    [Fact]
    public Task Pin_moves_to_top_and_survives_clear() => host.RunAsync(async () =>
    {
        var (vm, clip, _) = Make();
        clip.Copy("старое");
        clip.Copy("новое");
        await Settle();
        vm.TogglePinCommand.Execute(vm.ClipRows.Single(r => r.Text == "старое"));
        await Settle();
        Assert.Equal("старое", vm.ClipRows[0].Text);
        Assert.Equal("Открепить", vm.ClipRows[0].PinLabel);
        vm.ClearClipsCommand.Execute(null);
        await Settle();
        Assert.Equal(["старое"], vm.ClipRows.Select(r => r.Text));
    });

    [Fact]
    public Task Remove_deletes_one_row() => host.RunAsync(async () =>
    {
        var (vm, clip, _) = Make();
        clip.Copy("a");
        clip.Copy("b");
        await Settle();
        vm.RemoveClipCommand.Execute(vm.ClipRows.Single(r => r.Text == "a"));
        await Settle();
        Assert.Equal(["b"], vm.ClipRows.Select(r => r.Text));
    });

    [Fact]
    public Task Shelf_adds_and_removes_files() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make();
        Assert.True(vm.HasNoShelf);
        vm.AddToShelf([@"C:\Windows\win.ini"]);
        await Settle();
        Assert.Equal("win.ini", Assert.Single(vm.ShelfRows).Name);
        Assert.False(vm.HasNoShelf);
        vm.RemoveFromShelfCommand.Execute(vm.ShelfRows[0]);
        await Settle();
        Assert.Empty(vm.ShelfRows);
    });

    [Fact]
    public Task Click_on_a_shelf_file_opens_it() => host.RunAsync(async () =>
    {
        var clip = new FakeClipboard();
        var shelf = new FakeShelf();
        var dock = new FakeDock();
        var vm = new BarViewModel(new BarServices(
            new FakeMedia(), new FakeVolume(), new FakeWeather(null), new FakeLyrics(), new FakeDownloads(), dock, new FakeScreen(),
            Clipboard: clip, Shelf: shelf), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        vm.AddToShelf([@"C:\Windows\win.ini"]);
        await Settle();
        vm.OpenShelfItemCommand.Execute(vm.ShelfRows[0]);
        Assert.Equal([@"C:\Windows\win.ini"], dock.Launched);
    });

    [Fact]
    public Task Tabs_switch_pages_and_the_player_falls_back_home() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make();
        vm.IsClipboardPage = true;
        Assert.True(vm.IsClipboardPage);
        Assert.False(vm.IsHomePage);
        Assert.True(vm.ShowTabBar);
        vm.IsPlayerPage = false; // an unchecked radio button: no effect off the player
        Assert.True(vm.IsClipboardPage);
        await Task.CompletedTask;
    });
}
