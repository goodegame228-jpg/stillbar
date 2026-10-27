using Stillbar.Core.Voice;

namespace Stillbar.Tests;

public class VoiceTests
{
    [Fact]
    public void Reads_vosk_results()
    {
        Assert.Equal("привет мир", VoskResult.Text("""{ "text" : "привет мир" }"""));
        Assert.Equal("при", VoskResult.Partial("""{ "partial" : "при" }"""));
        Assert.Equal("", VoskResult.Text("""{ "partial" : "при" }"""));
        Assert.Equal("", VoskResult.Text("не json"));
    }

    [Fact]
    public void First_phrase_gets_a_capital_and_a_space()
    {
        var (text, ends) = Phrase.Format("привет как дела", startsSentence: true);
        Assert.Equal("Привет как дела ", text);
        Assert.False(ends);
    }

    [Fact]
    public void Middle_of_a_sentence_stays_lower_case() =>
        Assert.Equal("и ещё кое-что ", Phrase.Format("и ещё кое-что", startsSentence: false).Text);

    [Fact]
    public void Spoken_punctuation_becomes_marks_and_ends_the_sentence()
    {
        var (text, ends) = Phrase.Format("встречаемся завтра точка ты придёшь вопросительный знак", true);
        Assert.Equal("Встречаемся завтра. Ты придёшь? ", text);
        Assert.True(ends);
    }

    [Fact]
    public void Comma_sticks_to_the_word() =>
        Assert.Equal("Да, конечно ", Phrase.Format("да запятая конечно", true).Text);

    [Fact]
    public void New_line_has_no_trailing_space()
    {
        var (text, ends) = Phrase.Format("список новая строка", true);
        Assert.Equal("Список\n", text);
        Assert.True(ends);
    }

    [Fact]
    public void Whole_words_only() => Assert.Equal("В точках ", Phrase.Format("в точках", true).Text);

    [Fact]
    public void Nothing_heard_types_nothing() => Assert.Equal(("", true), Phrase.Format("  ", true));

    [Fact]
    public void Model_folder_is_complete_only_with_its_parts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stillbar-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "am"));
            Assert.False(VoiceModel.IsComplete(dir));
            File.WriteAllText(Path.Combine(dir, "am", "final.mdl"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "graph"));
            Assert.True(VoiceModel.IsComplete(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static byte[] FloatStereo(int frames, Func<int, float> sample)
    {
        var b = new byte[frames * 2 * 4];
        for (int i = 0; i < frames; i++)
        {
            BitConverter.GetBytes(sample(i)).CopyTo(b, i * 8);
            BitConverter.GetBytes(sample(i)).CopyTo(b, i * 8 + 4);
        }
        return b;
    }

    [Fact]
    public void Resampler_turns_48k_stereo_float_into_16k_mono_pcm()
    {
        var r = new SpeechResampler(48000, 2, isFloat: true);
        // One second, fed in uneven buffers, comes out as one second at 16 kHz (± a sample at the edges).
        int total = 0;
        foreach (int frames in new[] { 4800, 7333, 11111, 24756 })
            total += r.Convert(FloatStereo(frames, _ => 0.5f), frames * 8).Length / 2;
        Assert.InRange(total, 15999, 16001);
    }

    [Fact]
    public void Resampler_keeps_the_level_and_clips_safely()
    {
        var r = new SpeechResampler(16000, 2, isFloat: true);
        var pcm = r.Convert(FloatStereo(100, _ => 0.5f), 800);
        Assert.InRange(BitConverter.ToInt16(pcm, 100), 16380, 16386);
        var loud = new SpeechResampler(16000, 2, isFloat: true).Convert(FloatStereo(10, _ => 4f), 80);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(loud, 10));
    }

    [Fact]
    public void Resampler_reads_16_bit_input_too()
    {
        var input = new byte[200];
        for (int i = 0; i < 100; i++) BitConverter.GetBytes((short)8000).CopyTo(input, i * 2);
        var pcm = new SpeechResampler(16000, 1, isFloat: false).Convert(input, input.Length);
        Assert.InRange(BitConverter.ToInt16(pcm, 50), 7990, 8010);
    }
}