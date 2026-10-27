using System.Windows;
using System.Windows.Media;
using Stillbar.App.Shell;
using Stillbar.Core.Display;

namespace Stillbar.App.Tests;

/// <summary>The "stays out of the way" fixes: per-app mixer, bottom edge, battery saver.</summary>
[Collection("wpf")]
public class StaysOutOfTheWayTests(WpfHost host)
{
    [Fact]
    public Task Mixer_lists_apps_and_closes_the_device_list() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.ToggleDeviceListCommand.Execute(null);
        r.Vm.ToggleMixerCommand.Execute(null);
        await Task.Yield();
        Assert.True(r.Vm.IsMixerOpen);
        Assert.False(r.Vm.IsDeviceListOpen);
        Assert.Equal(["Google Chrome", "Telegram"], r.Vm.MixerApps.Select(a => a.Name));
        Assert.False(r.Vm.HasNoMixerApps);
    });

    [Fact]
    public Task Mixer_slider_sets_that_app_only_and_sync_does_not_echo() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.ToggleMixerCommand.Execute(null);
        r.Vm.MixerApps[0].VolumePercent = 25;
        r.Vm.RefreshMixer(); // reading the new value back must not call the service again
        await Task.Yield();
        Assert.Equal([("chrome", 25)], r.Volume.AppSetCalls);
        Assert.Equal(25, r.Vm.MixerApps[0].VolumePercent);
    });

    [Fact]
    public Task Mixer_rows_follow_apps_starting_and_stopping_in_place() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.ToggleMixerCommand.Execute(null);
        var chrome = r.Vm.MixerApps[0];
        r.Volume.AppList.RemoveAt(1);
        r.Volume.AppList.Add(new("spotify", "Spotify", 100, false));
        r.Vm.RefreshMixer();
        await Task.Yield();
        Assert.Equal(["Google Chrome", "Spotify"], r.Vm.MixerApps.Select(a => a.Name));
        Assert.Same(chrome, r.Vm.MixerApps[0]); // the row under the mouse is not replaced
    });

    [Fact]
    public Task Mixer_mute_button_toggles_the_app_and_shows_it() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Vm.ToggleMixerCommand.Execute(null);
        var telegram = r.Vm.MixerApps[1];
        Assert.Equal("Включить звук: Telegram", telegram.MuteLabel);
        telegram.ToggleMuteCommand.Execute(null);
        r.Vm.RefreshMixer();
        await Task.Yield();
        Assert.Equal("Выключить звук: Telegram", telegram.MuteLabel);
    });

    [Fact]
    public Task Empty_mixer_says_so_and_closing_the_panel_closes_it() => host.RunAsync(async () =>
    {
        var r = new Rig();
        r.Volume.AppList.Clear();
        r.Vm.OnExpandedChanged(true);
        r.Vm.ToggleMixerCommand.Execute(null);
        Assert.True(r.Vm.HasNoMixerApps);
        r.Vm.OnExpandedChanged(false);
        await Task.Yield();
        Assert.False(r.Vm.IsMixerOpen);
    });

    [Fact]
    public Task Bottom_edge_anchors_the_shape_to_the_bottom_and_mirrors_only_the_outline() => host.RunAsync(async () =>
    {
        var view = new BarView { DataContext = new Rig().Vm, Edge = BarEdge.Bottom };
        await Task.Yield();
        var bar = (FrameworkElement)view.FindName("Bar");
        var outline = (FrameworkElement)view.FindName("Outline");
        var body = (FrameworkElement)view.FindName("Body");
        Assert.Equal(VerticalAlignment.Bottom, bar.VerticalAlignment);
        Assert.Equal(-1, ((ScaleTransform)outline.RenderTransform).ScaleY);
        Assert.True(body.RenderTransform.Value.IsIdentity);

        view.Edge = BarEdge.Top;
        Assert.Equal(VerticalAlignment.Top, bar.VerticalAlignment);
        Assert.True(outline.RenderTransform.Value.IsIdentity);
    });
}
