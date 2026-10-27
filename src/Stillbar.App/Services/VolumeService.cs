using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Stillbar.App.Services;

public sealed record AudioDevice(string Id, string Name)
{
    /// <summary>"Динамики (Realtek(R) Audio)" → "Динамики".</summary>
    public string ShortName => Name.IndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? Name[..i] : Name;
}

public interface IVolumeService
{
    int Volume { get; }
    bool Muted { get; }
    AudioDevice? Device { get; }
    IReadOnlyList<AudioDevice> Devices { get; }
    event EventHandler? Changed;

    void SetVolume(int percent);
    void ToggleMute();

    /// <summary>False if Windows refused; the caller offers the sound settings instead.</summary>
    bool SetDefaultDevice(string id);

    /// <summary>Apps playing (or able to play) on the current device, one row per app, read now.</summary>
    IReadOnlyList<AppVolume> Apps() => [];
    void SetAppVolume(string key, int percent) { }
    void ToggleAppMute(string key) { }
}

/// <summary>One row of the mixer. An app with several audio sessions (a browser) is one row; Key is its process name.</summary>
public sealed record AppVolume(string Key, string Name, int Volume, bool Muted);

/// <summary>Default output device through Core Audio (NAudio). All COM work stays on the UI thread it was created on.</summary>
public sealed class CoreAudioVolumeService : IVolumeService, IMMNotificationClient, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;

    public CoreAudioVolumeService(Dispatcher ui)
    {
        _ui = ui;
        _enumerator.RegisterEndpointNotificationCallback(this);
        Attach();
    }

    public int Volume { get; private set; }
    public bool Muted { get; private set; }
    public AudioDevice? Device { get; private set; }
    public IReadOnlyList<AudioDevice> Devices { get; private set; } = [];
    public event EventHandler? Changed;

    public void SetVolume(int percent)
    {
        if (_device is null) return;
        _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
    }

    public void ToggleMute()
    {
        if (_device is null) return;
        _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
    }

    public bool SetDefaultDevice(string id)
    {
        try
        {
            PolicyConfig.SetDefault(id);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Debug.WriteLine($"set default device: {ex.Message}");
            return false;
        }
    }

    public IReadOnlyList<AppVolume> Apps()
    {
        if (_device is null) return [];
        try
        {
            return Sessions()
                .GroupBy(s => s.Key)
                .Select(g => new AppVolume(
                    g.Key,
                    g.First().Name,
                    (int)Math.Round(g.Max(s => s.Session.SimpleAudioVolume.Volume) * 100),
                    g.All(s => s.Session.SimpleAudioVolume.Mute)))
                .OrderBy(a => a.Key == SystemKey) // system sounds last
                .ThenBy(a => a.Name, StringComparer.CurrentCulture)
                .ToList();
        }
        catch (COMException) { return []; }
    }

    public void SetAppVolume(string key, int percent)
    {
        float v = Math.Clamp(percent, 0, 100) / 100f;
        try { foreach (var s in Sessions().Where(s => s.Key == key)) s.Session.SimpleAudioVolume.Volume = v; }
        catch (COMException) { }
    }

    public void ToggleAppMute(string key)
    {
        try
        {
            var group = Sessions().Where(s => s.Key == key).ToList();
            bool mute = !group.All(s => s.Session.SimpleAudioVolume.Mute);
            foreach (var s in group) s.Session.SimpleAudioVolume.Mute = mute;
        }
        catch (COMException) { }
    }

    private const string SystemKey = "\0system";
    private readonly Dictionary<uint, (string Key, string Name)> _processNames = [];

    /// <summary>Live sessions on the current device with a stable key and a readable name per app.</summary>
    private IEnumerable<(string Key, string Name, AudioSessionControl Session)> Sessions()
    {
        var manager = _device!.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
            if (s.IsSystemSoundsSession) { yield return (SystemKey, "Системные звуки", s); continue; }
            var (key, name) = NameOf(s.GetProcessID);
            if (key.Length > 0) yield return (key, name, s);
        }
    }

    private (string Key, string Name) NameOf(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var known)) return known;
        (string, string) result;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string name = p.ProcessName;
            try
            {
                var description = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { } // elevated or protected process
            result = (p.ProcessName.ToLowerInvariant(), name);
        }
        catch (ArgumentException) { result = ("", ""); } // already exited
        if (result.Item1.Length > 0) _processNames[pid] = result;
        return result;
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(this);
        Detach();
        _enumerator.Dispose();
    }

    private void Attach()
    {
        Detach();
        try
        {
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
            Volume = (int)Math.Round(_device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            Muted = _device.AudioEndpointVolume.Mute;
            Device = new AudioDevice(_device.ID, _device.FriendlyName);
        }
        catch (COMException)
        {
            // No output device at all (all unplugged / disabled).
            _device = null;
            Device = null;
        }

        Devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDevice(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name, StringComparer.CurrentCulture)
            .ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Detach()
    {
        if (_device is null) return;
        _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
        _device.Dispose();
        _device = null;
    }

    private void OnVolume(AudioVolumeNotificationData data)
    {
        // Arrives on an audio thread; values come with it, so no COM call is needed here.
        Volume = (int)Math.Round(data.MasterVolume * 100);
        Muted = data.Muted;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Reattach() => _ui.InvokeAsync(Attach);

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) Reattach();
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Reattach();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Reattach();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Reattach();
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    /// <summary>
    /// Windows has no public API to change the default output device. This is the undocumented IPolicyConfig
    /// that the sound settings use themselves; it has been stable since Windows 7 but may break in an update.
    /// </summary>
    private static class PolicyConfig
    {
        public static void SetDefault(string id)
        {
            var config = (IPolicyConfig)new CPolicyConfigClient();
            try
            {
                Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(id, 0)); // eConsole
                Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(id, 1)); // eMultimedia
            }
            finally
            {
                Marshal.ReleaseComObject(config);
            }
        }

        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class CPolicyConfigClient;

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            // Order matters: only the last-but-one slot is called, the others pad the vtable.
            [PreserveSig] int GetMixFormat(nint id, nint format);
            [PreserveSig] int GetDeviceFormat(nint id, int useDefault, nint format);
            [PreserveSig] int ResetDeviceFormat(nint id);
            [PreserveSig] int SetDeviceFormat(nint id, nint endpointFormat, nint mixFormat);
            [PreserveSig] int GetProcessingPeriod(nint id, int useDefault, nint defaultPeriod, nint minPeriod);
            [PreserveSig] int SetProcessingPeriod(nint id, nint period);
            [PreserveSig] int GetShareMode(nint id, nint mode);
            [PreserveSig] int SetShareMode(nint id, nint mode);
            [PreserveSig] int GetPropertyValue(nint id, int fxStore, nint key, nint value);
            [PreserveSig] int SetPropertyValue(nint id, int fxStore, nint key, nint value);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
            [PreserveSig] int SetEndpointVisibility(nint id, int visible);
        }
    }
}

/// <summary>Fixed values for --snapshot renders.</summary>
public sealed class SampleVolumeService : IVolumeService
{
    public int Volume => 62;
    public bool Muted => false;
    public AudioDevice? Device { get; } = new("a", "Динамики (Встроенное аудио)");
    public IReadOnlyList<AudioDevice> Devices { get; } =
        [new("a", "Динамики (Встроенное аудио)"), new("b", "Наушники (Беспроводные)")];
    public event EventHandler? Changed { add { } remove { } }
    public void SetVolume(int percent) { }
    public void ToggleMute() { }
    public bool SetDefaultDevice(string id) => true;
    public IReadOnlyList<AppVolume> Apps() =>
        [new("browser", "Браузер", 80, false), new("player", "Плеер", 100, false), new("chat", "Мессенджер", 35, true),
         new("\0system", "Системные звуки", 60, false)];
}
