using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Stillbar.Core.Controls;
using Windows.Devices.Radios;

namespace Stillbar.App.Services;

public enum RadioKind { WiFi, Bluetooth }

/// <summary>One VPN connection from Windows' settings and whether it is up.</summary>
public sealed record VpnState(string Name, bool Connected);

/// <summary>A paired Bluetooth device. Kind: "audio", "input", "phone" or "other", for its icon.</summary>
public sealed record PairedDevice(string Name, bool Connected, string Kind);

/// <summary>What the control centre shows. A null radio means the computer has no such adapter.</summary>
public sealed record ControlState(bool? WiFi, bool? Bluetooth, bool DarkTheme, IReadOnlyList<VpnState> Vpns, IReadOnlyList<PairedDevice>? Paired = null);

public interface ISystemControls
{
    Task<ControlState> ReadAsync();
    /// <summary>False if Windows refused (no permission, adapter busy, airplane mode).</summary>
    Task<bool> SetRadioAsync(RadioKind kind, bool on);
    void SetDarkTheme(bool dark);
    /// <summary>Null when it worked; otherwise why not, for the pill.</summary>
    Task<string?> SetVpnAsync(string name, bool connect);
    void Lock();
    void Sleep();
    void Restart();
    void ShutDown();
    /// <summary>A page of Windows Settings (ms-settings:…).</summary>
    void OpenSettings(string uri);
}

/// <summary>
/// The real thing: radios through Windows.Devices.Radios, the theme through the same two registry values the
/// Settings app writes, VPN through rasdial (it uses the credentials Windows saved), power through user32/powrprof
/// and shutdown.exe. Nothing here needs administrator rights.
/// </summary>
public sealed partial class SystemControls : ISystemControls
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // rasdial prints in the console code page (866 for Russian Windows), which .NET only knows with this provider.
    static SystemControls() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    private bool _radioAccess;

    public async Task<ControlState> ReadAsync()
    {
        var radios = await Radios();
        bool? State(RadioKind k) => radios.FirstOrDefault(r => r.Kind == ToWinRt(k)) is { } r ? r.State == RadioState.On : null;
        var bt = State(RadioKind.Bluetooth);
        return new ControlState(State(RadioKind.WiFi), bt, IsDarkTheme(), await Vpns(), bt is null ? [] : await PairedDevices());
    }

    public async Task<bool> SetRadioAsync(RadioKind kind, bool on)
    {
        var radio = (await Radios()).FirstOrDefault(r => r.Kind == ToWinRt(kind));
        if (radio is null) return false;
        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        Log.Info("controls", $"{kind} → {(on ? "on" : "off")}: {result}");
        return result == RadioAccessStatus.Allowed;
    }

    public void SetDarkTheme(bool dark)
    {
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
        key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        // Tell running apps and the taskbar, the way the Settings app does.
        SendMessageTimeoutW(0xFFFF, 0x001A /* WM_SETTINGCHANGE */, 0, "ImmersiveColorSet", 0x2 /* SMTO_ABORTIFHUNG */, 500, out _);
    }

    public async Task<string?> SetVpnAsync(string name, bool connect)
    {
        var (code, output) = await Rasdial(connect ? $"\"{name}\"" : $"\"{name}\" /disconnect", TimeSpan.FromSeconds(30));
        if (code == 0) return null;
        Log.Warn("controls", $"rasdial {name}: {code} {output.Trim()}");
        // 691: wrong user name or password; 703: the connection needs a password typed in (no saved credentials).
        return code is 691 or 703 ? "VPN просит логин и пароль: подключитесь один раз в параметрах Windows" : $"VPN не подключился (ошибка {code})";
    }

    public void Lock() => LockWorkStation();

    public void Sleep() => SetSuspendState(false, false, false);

    public void Restart() => Shutdown("/r /t 0");

    public void ShutDown() => Shutdown("/s /t 0");

    public void OpenSettings(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    private static void Shutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });

    /// <summary>
    /// Paired devices (classic and LE) with their connection state, from Windows.Devices.Enumeration. Connecting
    /// headphones has no public API for other apps, so the tab links to Windows' Bluetooth page for that.
    /// </summary>
    private static async Task<IReadOnlyList<PairedDevice>> PairedDevices()
    {
        const string IsConnected = "System.Devices.Aep.IsConnected";
        const string Category = "System.Devices.Aep.Category";
        try
        {
            var found = new List<PairedDevice>();
            foreach (var selector in new[]
            {
                Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            })
            {
                var devices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector, [IsConnected, Category]);
                foreach (var d in devices)
                {
                    if (string.IsNullOrWhiteSpace(d.Name)) continue;
                    bool connected = d.Properties.TryGetValue(IsConnected, out var c) && c is true;
                    var cats = d.Properties.TryGetValue(Category, out var k) && k is string[] arr ? string.Join(" ", arr) : "";
                    string kind = cats.Contains("Audio", StringComparison.OrdinalIgnoreCase) || cats.Contains("Headphone", StringComparison.OrdinalIgnoreCase) ? "audio"
                        : cats.Contains("Input", StringComparison.OrdinalIgnoreCase) || cats.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) || cats.Contains("Mouse", StringComparison.OrdinalIgnoreCase) ? "input"
                        : cats.Contains("Phone", StringComparison.OrdinalIgnoreCase) ? "phone" : "other";
                    found.Add(new PairedDevice(d.Name, connected, kind));
                }
            }
            // A device that is both classic and LE (many headphones) shows up twice: keep one, connected if either is.
            return found.GroupBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => g.First() with { Connected = g.Any(p => p.Connected) })
                .OrderByDescending(p => p.Connected).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Log.Warn("controls", $"bluetooth devices: {ex.Message}");
            return [];
        }
    }

    private static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    private async Task<IReadOnlyList<Radio>> Radios()
    {
        try
        {
            if (!_radioAccess) _radioAccess = await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed;
            return _radioAccess ? await Radio.GetRadiosAsync() : [];
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Log.Warn("controls", $"radios: {ex.Message}");
            return [];
        }
    }

    private static Windows.Devices.Radios.RadioKind ToWinRt(RadioKind k) =>
        k == RadioKind.WiFi ? Windows.Devices.Radios.RadioKind.WiFi : Windows.Devices.Radios.RadioKind.Bluetooth;

    private static async Task<IReadOnlyList<VpnState>> Vpns()
    {
        var names = new List<string>();
        foreach (var dir in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData })
        {
            var pbk = Path.Combine(Environment.GetFolderPath(dir), "Microsoft", "Network", "Connections", "Pbk", "rasphone.pbk");
            try { if (File.Exists(pbk)) names.AddRange(Vpn.PhonebookNames(await File.ReadAllTextAsync(pbk))); }
            catch (IOException) { }
        }
        if (names.Count == 0) return [];
        var (_, status) = await Rasdial("", TimeSpan.FromSeconds(5));
        var up = Vpn.ConnectedNames(status);
        return names.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new VpnState(n, up.Contains(n, StringComparer.OrdinalIgnoreCase))).ToList();
    }

    private static async Task<(int Code, string Output)> Rasdial(string args, TimeSpan timeout)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("rasdial.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            })!;
            var output = await p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }

    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool LockWorkStation();
    [LibraryImport("powrprof.dll")][return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool force, [MarshalAs(UnmanagedType.Bool)] bool wakeupEventsDisabled);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}

/// <summary>Snapshots and tests: a fixed state, records what was asked.</summary>
public sealed class SampleControls : ISystemControls
{
    public ControlState State { get; set; } = new(true, false, true, [new("Работа", false)],
        [new("Наушники", true, "audio"), new("Мышь", false, "input")]);
    public List<string> Calls { get; } = [];
    public bool RadioAllowed { get; set; } = true;
    public string? VpnError { get; set; }

    public Task<ControlState> ReadAsync() => Task.FromResult(State);

    public Task<bool> SetRadioAsync(RadioKind kind, bool on)
    {
        Calls.Add($"{kind}:{on}");
        if (RadioAllowed) State = kind == RadioKind.WiFi ? State with { WiFi = on } : State with { Bluetooth = on };
        return Task.FromResult(RadioAllowed);
    }

    public void SetDarkTheme(bool dark) { Calls.Add($"dark:{dark}"); State = State with { DarkTheme = dark }; }

    public Task<string?> SetVpnAsync(string name, bool connect)
    {
        Calls.Add($"vpn:{name}:{connect}");
        if (VpnError is null) State = State with { Vpns = State.Vpns.Select(v => v.Name == name ? v with { Connected = connect } : v).ToList() };
        return Task.FromResult(VpnError);
    }

    public void Lock() => Calls.Add("lock");
    public void Sleep() => Calls.Add("sleep");
    public void Restart() => Calls.Add("restart");
    public void ShutDown() => Calls.Add("shutdown");
    public void OpenSettings(string uri) => Calls.Add(uri);
}
