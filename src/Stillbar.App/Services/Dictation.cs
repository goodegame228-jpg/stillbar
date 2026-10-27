using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Stillbar.Core.Voice;
using Vosk;

namespace Stillbar.App.Services;

public interface IDictation
{
    bool IsListening { get; }
    /// <summary>What is being heard right now (Vosk's partial result); empty between phrases.</summary>
    string Partial { get; }
    /// <summary>Started, stopped, or the partial text changed. Raised on the audio thread.</summary>
    event EventHandler? Changed;
    /// <summary>A phrase ended (a pause): the words, lower case, no punctuation. Raised on the audio thread.</summary>
    event EventHandler<string>? Heard;

    /// <summary>Downloads the model the first time (progress in words), opens the microphone. Null when listening; otherwise why not.</summary>
    Task<string?> StartAsync(IProgress<string> status);
    void Stop();
}

/// <summary>
/// Offline dictation with Vosk and its small Russian model: no internet after the one-time model download, no
/// Windows speech services (which a policy can switch off). Audio never leaves the computer and is not saved.
/// Stops by itself after 20 s without speech.
/// </summary>
public sealed class VoskDictation : IDictation, IDisposable
{
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(20);
    private static readonly string ModelsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stillbar", "models");
    private static string ModelDir => Path.Combine(ModelsDir, VoiceModel.Name);

    private readonly object _gate = new();
    private Model? _model;
    private VoskRecognizer? _recognizer;
    private WasapiCapture? _mic;
    private SpeechResampler? _resampler;
    private DateTime _lastSpeech;

    public bool IsListening { get; private set; }
    public string Partial { get; private set; } = "";
    public event EventHandler? Changed;
    public event EventHandler<string>? Heard;

    public async Task<string?> StartAsync(IProgress<string> status)
    {
        if (IsListening) return null;
        try
        {
            if (!VoiceModel.IsComplete(ModelDir)) await DownloadModelAsync(status);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Log.Warn("voice", $"model download: {ex.Message}");
            return "Не удалось скачать модель распознавания. Проверьте интернет";
        }

        try
        {
            if (_model is null)
            {
                status.Report("Загружаю модель…");
                Vosk.Vosk.SetLogLevel(-1);
                _model = await Task.Run(() => new Model(ModelDir));
            }
            _recognizer = new VoskRecognizer(_model, SpeechResampler.TargetRate);

            using var devices = new MMDeviceEnumerator();
            var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            _mic = new WasapiCapture(device);
            var f = _mic.WaveFormat;
            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat
                || (f is WaveFormatExtensible x && x.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            if (!isFloat && f.BitsPerSample != 16) return "Микрофон отдаёт звук в формате, который Stillbar не понимает";
            _resampler = new SpeechResampler(f.SampleRate, f.Channels, isFloat);
            _mic.DataAvailable += OnAudio;
            _mic.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) Log.Warn("voice", $"microphone stopped: {e.Exception.Message}");
                Finish();
            };
            _lastSpeech = DateTime.UtcNow;
            _mic.StartRecording();
            IsListening = true;
            Log.Info("voice", $"listening: {device.FriendlyName}, {f.SampleRate} Hz, {f.Channels} ch");
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (COMException ex)
        {
            Log.Warn("voice", $"microphone: {ex.Message}");
            Cleanup();
            // E_ACCESSDENIED: Settings → Privacy → Microphone is off for desktop apps. No device: nothing plugged in.
            return (uint)ex.HResult == 0x80070005
                ? "Нет доступа к микрофону: Параметры → Конфиденциальность → Микрофон"
                : "Микрофон не найден";
        }
    }

    public void Stop() => _mic?.StopRecording(); // RecordingStopped finishes the last phrase

    public void Dispose()
    {
        Stop();
        Cleanup();
        _model?.Dispose();
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_recognizer is null || _resampler is null) return;
            var pcm = _resampler.Convert(e.Buffer, e.BytesRecorded);
            if (pcm.Length == 0) return;
            if (_recognizer.AcceptWaveform(pcm, pcm.Length))
            {
                var text = VoskResult.Text(_recognizer.Result());
                Partial = "";
                if (text.Length > 0)
                {
                    _lastSpeech = DateTime.UtcNow;
                    Heard?.Invoke(this, text);
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                var partial = VoskResult.Partial(_recognizer.PartialResult());
                if (partial.Length > 0) _lastSpeech = DateTime.UtcNow;
                if (partial != Partial)
                {
                    Partial = partial;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        if (DateTime.UtcNow - _lastSpeech > SilenceLimit) Stop();
    }

    /// <summary>The last words said before stopping still count.</summary>
    private void Finish()
    {
        lock (_gate)
        {
            if (_recognizer is not null)
            {
                var text = VoskResult.Text(_recognizer.FinalResult());
                if (text.Length > 0) Heard?.Invoke(this, text);
            }
            Cleanup();
        }
        IsListening = false;
        Partial = "";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Cleanup()
    {
        _mic?.Dispose();
        _mic = null;
        _recognizer?.Dispose();
        _recognizer = null;
        _resampler = null;
    }

    /// <summary>
    /// «Stillbar --voice-check»: everything but the microphone. Downloads the model if needed, loads it, feeds a
    /// second of silence through the same resampler and recogniser. Returns a line for the log and the console.
    /// </summary>
    public static async Task<string> SelfCheckAsync(IProgress<string> status)
    {
        if (!VoiceModel.IsComplete(ModelDir)) await DownloadModelAsync(status);
        Vosk.Vosk.SetLogLevel(-1);
        var started = DateTime.UtcNow;
        using var model = new Model(ModelDir);
        var loaded = DateTime.UtcNow - started;
        using var recognizer = new VoskRecognizer(model, SpeechResampler.TargetRate);
        var resampler = new SpeechResampler(48000, 2, isFloat: true);
        var silence = new byte[48000 * 2 * 4]; // one second, 48 kHz stereo float
        var pcm = resampler.Convert(silence, silence.Length);
        recognizer.AcceptWaveform(pcm, pcm.Length);
        var heard = VoskResult.Text(recognizer.FinalResult());
        long size = new DirectoryInfo(ModelDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return $"voice check ok: model {size / 1024 / 1024} MB loaded in {loaded.TotalSeconds:0.0} s, 1 s of silence → \"{heard}\" ({pcm.Length / 2} samples at 16 kHz)";
    }

    private static async Task DownloadModelAsync(IProgress<string> status)
    {
        Directory.CreateDirectory(ModelsDir);
        var zip = Path.Combine(ModelsDir, VoiceModel.Name + ".zip.part");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Stillbar/0.1");
            using var response = await http.GetAsync(VoiceModel.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var from = await response.Content.ReadAsStreamAsync();
            await using var to = File.Create(zip);
            var buffer = new byte[81920];
            long done = 0;
            int lastPercent = -1, n;
            while ((n = await from.ReadAsync(buffer)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                int percent = total > 0 ? (int)(done * 100 / total) : -1;
                if (percent != lastPercent && percent % 5 == 0)
                {
                    lastPercent = percent;
                    status.Report($"Скачиваю модель распознавания… {percent} %");
                }
            }
        }
        status.Report("Распаковываю модель…");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, ModelsDir, overwriteFiles: true));
        File.Delete(zip);
        if (!VoiceModel.IsComplete(ModelDir)) throw new InvalidDataException("the archive did not contain the model");
        Log.Info("voice", $"model ready in {ModelDir}");
    }
}

/// <summary>For --snapshot: listening, with a few words heard.</summary>
public sealed class SampleDictation : IDictation
{
    public bool IsListening => true;
    public string Partial => "встречаемся завтра в десять";
    public event EventHandler? Changed { add { } remove { } }
    public event EventHandler<string>? Heard { add { } remove { } }
    public Task<string?> StartAsync(IProgress<string> status) => Task.FromResult<string?>(null);
    public void Stop() { }
}