using Stillbar.App.Services;
using Stillbar.App.Shell;

namespace Stillbar.App.Tests;

/// <summary>The Система tab on SampleControls: nothing here touches the real radios, theme, VPN or power.</summary>
[Collection("wpf")]
public class ControlCentreTests(WpfHost host)
{
    private static (BarViewModel Vm, SampleControls C, Rig R) Make(Action<SampleControls>? setup = null)
    {
        var c = new SampleControls();
        setup?.Invoke(c);
        var r = new Rig();
        var vm = new BarViewModel(new BarServices(
            new FakeMedia(), new FakeVolume(), new FakeWeather(null), new FakeLyrics(), new FakeDownloads(), new FakeDock(), new FakeScreen(),
            Clock: () => r.Now, Controls: c), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        return (vm, c, r);
    }

    [Fact]
    public Task State_is_read_without_switching_anything() => host.RunAsync(async () =>
    {
        var (vm, c, _) = Make();
        await vm.RefreshControls();
        Assert.True(vm.HasWifi);
        Assert.True(vm.WifiOn);
        Assert.False(vm.BluetoothOn);
        Assert.True(vm.DarkTheme);
        Assert.Equal("Работа", Assert.Single(vm.VpnRows).Name);
        Assert.Empty(c.Calls); // reading must never write
    });

    [Fact]
    public Task Paired_bluetooth_devices_are_listed_with_their_state() => host.RunAsync(async () =>
    {
        var (vm, c, _) = Make();
        await vm.RefreshControls();
        Assert.Equal(["Наушники", "Мышь"], vm.BluetoothRows.Select(b => b.Name));
        Assert.Equal("подключено", vm.BluetoothRows[0].State);
        Assert.Equal("", vm.BluetoothRows[0].Glyph);
        Assert.False(vm.HasNoPaired);
        vm.OpenWindowsSettingsCommand.Execute("ms-settings:bluetooth");
        Assert.Equal(["ms-settings:bluetooth"], c.Calls);
    });

    [Fact]
    public Task No_paired_devices_says_so() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make(c => c.State = c.State with { Paired = [] });
        await vm.RefreshControls();
        Assert.True(vm.HasNoPaired);
    });

    [Fact]
    public Task No_adapter_disables_the_tile() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make(c => c.State = c.State with { Bluetooth = null });
        await vm.RefreshControls();
        Assert.False(vm.HasBluetooth);
    });

    [Fact]
    public Task Toggle_switches_the_radio() => host.RunAsync(async () =>
    {
        var (vm, c, _) = Make();
        await vm.RefreshControls();
        vm.BluetoothOn = true;
        await WpfHost.Settle();
        Assert.Equal(["Bluetooth:True"], c.Calls);
        Assert.True(vm.BluetoothOn);
    });

    [Fact]
    public Task Refused_switch_flips_back_and_says_so() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make(c => c.RadioAllowed = false);
        await vm.RefreshControls();
        vm.WifiOn = false;
        await WpfHost.Settle();
        Assert.True(vm.WifiOn);
        Assert.Equal("Windows не разрешила переключить Wi-Fi", vm.StatusText);
    });

    [Fact]
    public Task Theme_toggle_writes_once() => host.RunAsync(async () =>
    {
        var (vm, c, _) = Make();
        await vm.RefreshControls();
        vm.DarkTheme = false;
        Assert.Equal(["dark:False"], c.Calls);
        Assert.Equal("Светлая тема Windows", vm.StatusText);
    });

    [Fact]
    public Task Vpn_connects_and_reports() => host.RunAsync(async () =>
    {
        var (vm, c, _) = Make();
        await vm.RefreshControls();
        await vm.ToggleVpnCommand.ExecuteAsync(vm.VpnRows[0]);
        Assert.Equal(["vpn:Работа:True"], c.Calls);
        Assert.True(vm.VpnRows[0].Connected);
        Assert.Equal("Отключить", vm.VpnRows[0].Action);
        Assert.Equal("Работа подключён", vm.StatusText);
    });

    [Fact]
    public Task Vpn_error_is_shown() => host.RunAsync(async () =>
    {
        var (vm, _, _) = Make(c => c.VpnError = "VPN не подключился (ошибка 800)");
        await vm.RefreshControls();
        await vm.ToggleVpnCommand.ExecuteAsync(vm.VpnRows[0]);
        Assert.Equal("VPN не подключился (ошибка 800)", vm.StatusText);
        Assert.False(vm.VpnRows[0].Connected);
    });

    [Fact]
    public Task Restart_needs_a_second_click_within_four_seconds() => host.RunAsync(async () =>
    {
        var (vm, c, r) = Make();
        vm.RestartComputerCommand.Execute(null);
        Assert.Empty(c.Calls);
        Assert.Equal("Точно перезагрузить?", vm.RestartLabel);
        r.Now += TimeSpan.FromSeconds(5);
        vm.RestartComputerCommand.Execute(null); // too late: asks again
        Assert.Empty(c.Calls);
        r.Now += TimeSpan.FromSeconds(1);
        vm.RestartComputerCommand.Execute(null);
        Assert.Equal(["restart"], c.Calls);
        Assert.Equal("Перезагрузка", vm.RestartLabel);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Shut_down_confirmation_is_not_shared_with_restart() => host.RunAsync(async () =>
    {
        var (vm, c, _) = Make();
        vm.RestartComputerCommand.Execute(null);
        vm.ShutDownComputerCommand.Execute(null);
        Assert.Empty(c.Calls);
        Assert.Equal("Точно выключить?", vm.ShutdownLabel);
        Assert.Equal("Перезагрузка", vm.RestartLabel);
        vm.ShutDownComputerCommand.Execute(null);
        Assert.Equal(["shutdown"], c.Calls);
        await Task.CompletedTask;
    });

    [Fact]
    public Task Lock_and_sleep_act_at_once_and_close_the_panel() => host.RunAsync(async () =>
    {
        var (vm, c, _) = Make();
        int collapsed = 0;
        vm.CollapseRequested += (_, _) => collapsed++;
        vm.LockComputerCommand.Execute(null);
        vm.SleepComputerCommand.Execute(null);
        Assert.Equal(["lock", "sleep"], c.Calls);
        Assert.Equal(2, collapsed);
        await Task.CompletedTask;
    });
}
