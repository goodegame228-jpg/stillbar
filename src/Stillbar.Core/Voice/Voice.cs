using System.Globalization;
using System.Text.Json;

namespace Stillbar.Core.Voice;

/// <summary>The offline speech model: Vosk's small Russian model (Apache 2.0), about 45 MB, downloaded on first use.</summary>
public static class VoiceModel
{
    public const string Name = "vosk-model-small-ru-0.22";
    public const string Url = "https://alphacephei.com/vosk/models/" + Name + ".zip";

    /// <summary>A model folder is usable once its acoustic model and graph are there (a half-extracted one is not).</summary>
    public static bool IsComplete(string folder) =>
        File.Exists(Path.Combine(folder, "am", "final.mdl")) && Directory.Exists(Path.Combine(folder, "graph"));
}

/// <summary>Vosk answers in JSON: {"partial": "..."} while you speak, {"text": "..."} when a phrase ends.</summary>
public static class VoskResult
{
    public static string Text(string json) => Read(json, "text");

    public static string Partial(string json) => Read(json, "partial");

    private static string Read(string json, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
        }
        catch (JsonException) { return ""; }
    }
}

/// <summary>
/// The small model hears words, not sentences: no capitals, no punctuation. Each phrase starts with a capital
/// when it begins a sentence and is followed by a space, so dictating several phrases reads naturally.
/// A few spoken commands become punctuation («точка», «запятая», «вопросительный знак», «новая строка»).
/// </summary>
public static class Phrase
{
    private static readonly (string Words, string Mark)[] Spoken =
    [
        ("вопросительный знак", "?"), ("восклицательный знак", "!"), ("новая строка", "\n"), ("новый абзац", "\n\n"),
        ("точка с запятой", ";"), ("двоеточие", ":"), ("запятая", ","), ("точка", "."),
    ];

    /// <summary>The text to type for one recognised phrase, and whether the next phrase starts a sentence.</summary>
    public static (string Text, bool SentenceEnds) Format(string recognised, bool startsSentence)
    {
        var s = recognised.Trim();
        if (s.Length == 0) return ("", startsSentence);
        foreach (var (words, mark) in Spoken)
            s = ReplaceWord(s, words, mark);
        s = s.Replace(" ,", ",").Replace(" .", ".").Replace(" ?", "?").Replace(" !", "!").Replace(" :", ":").Replace(" ;", ";")
             .Replace(" \n", "\n").Replace("\n ", "\n").Trim(' ');
        if (s.Length == 0) return ("", startsSentence);
        if (startsSentence) s = char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];
        // Capitals after a sentence mark inside the phrase too.
        var chars = s.ToCharArray();
        for (int i = 1; i < chars.Length; i++)
            if (char.IsLetter(chars[i]) && IsAfterSentenceEnd(chars, i)) chars[i] = char.ToUpper(chars[i], CultureInfo.CurrentCulture);
        s = new string(chars);
        bool ends = s[^1] is '.' or '?' or '!' or '\n';
        return (s.EndsWith('\n') ? s : s + " ", ends);
    }

    private static string ReplaceWord(string s, string words, string mark)
    {
        // Whole words only: «точка» but not «точках».
        var parts = s.Split(' ').ToList();
        var w = words.Split(' ');
        for (int i = 0; i + w.Length <= parts.Count; i++)
        {
            if (!parts.Skip(i).Take(w.Length).SequenceEqual(w)) continue;
            parts.RemoveRange(i, w.Length);
            parts.Insert(i, mark);
        }
        return string.Join(' ', parts);
    }

    private static bool IsAfterSentenceEnd(char[] c, int i)
    {
        int j = i - 1;
        while (j >= 0 && c[j] == ' ') j--;
        return j >= 0 && c[j] is '.' or '?' or '!' or '\n';
    }
}

/// <summary>
/// Microphone audio as the recogniser wants it: 16 kHz, mono, 16-bit. Windows gives 32-bit float (or 16-bit)
/// at the device rate, often stereo. Channels are averaged; the rate is changed by linear interpolation, which
/// is plenty for speech. Keeps its position between buffers so there are no clicks at the joins.
/// </summary>
public sealed class SpeechResampler(int sourceRate, int channels, bool isFloat)
{
    public const int TargetRate = 16000;
    private readonly double _step = (double)sourceRate / TargetRate;
    private double _pos;          // position in the source, relative to the start of the current buffer
    private float _previous;      // the last sample of the previous buffer, for interpolating across the join

    public byte[] Convert(byte[] buffer, int count)
    {
        int bytesPerSample = isFloat ? 4 : 2;
        int frames = count / (bytesPerSample * channels);
        if (frames == 0) return [];
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int at = (f * channels + c) * bytesPerSample;
                sum += isFloat ? BitConverter.ToSingle(buffer, at) : BitConverter.ToInt16(buffer, at) / 32768f;
            }
            mono[f] = sum / channels;
        }

        var output = new List<short>((int)(frames / _step) + 2);
        while (_pos < frames)
        {
            int i = (int)Math.Floor(_pos);
            double frac = _pos - i;
            float a = i - 1 < 0 ? _previous : mono[i - 1], b = mono[i];
            // _pos counts from one sample before the buffer (the previous one), so i-1 / i bracket the point.
            float v = (float)(a + (b - a) * frac);
            output.Add((short)Math.Clamp(v * 32767f, short.MinValue, short.MaxValue));
            _pos += _step;
        }
        _pos -= frames;
        _previous = mono[^1];

        var bytes = new byte[output.Count * 2];
        Buffer.BlockCopy(output.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }
}