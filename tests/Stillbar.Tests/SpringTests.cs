using Stillbar.Core.Motion;

namespace Stillbar.Tests;

public class SpringTests
{
    private const double K = 380, C = 32;

    [Fact]
    public void Settles_on_target_within_a_second()
    {
        var s = new Spring(210) { Target = 720 };
        double t = 0;
        while (!s.Step(1.0 / 60, K, C) && t < 2) t += 1.0 / 60;
        Assert.True(t < 1.0, $"took {t:0.00}s");
        Assert.Equal(720, s.Value);
    }

    [Fact]
    public void Overshoot_stays_small()
    {
        var s = new Spring(210) { Target = 720 };
        double max = 0;
        for (int i = 0; i < 120; i++) { s.Step(1.0 / 60, K, C); max = Math.Max(max, s.Value); }
        Assert.InRange(max - 720, 0, 720 * 0.05);
    }

    [Fact]
    public void A_long_frame_does_not_explode()
    {
        var s = new Spring(0) { Target = 100 };
        s.Step(0.5, K, C);
        Assert.InRange(s.Value, -50, 200);
    }
}
