namespace Stillbar.Core.Motion;

/// <summary>
/// Damped spring for the pill/panel morph. Stiffness 380 and damping 32 (tokens.json) give a quick move
/// with a slight overshoot. Steps with semi-implicit Euler in small sub-steps, so a slow frame cannot blow up.
/// </summary>
public struct Spring(double value)
{
    private const double MaxSubStep = 1.0 / 240;
    private const double Epsilon = 0.05;

    public double Value = value;
    public double Velocity = 0;
    public double Target = value;

    public readonly bool IsSettled => Math.Abs(Target - Value) < Epsilon && Math.Abs(Velocity) < Epsilon;

    /// <summary>Advances by <paramref name="dt"/> seconds. Returns true once it has come to rest on the target.</summary>
    public bool Step(double dt, double stiffness, double damping)
    {
        dt = Math.Clamp(dt, 0, 1.0 / 30);
        while (dt > 0)
        {
            double h = Math.Min(dt, MaxSubStep);
            double accel = stiffness * (Target - Value) - damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }

        if (IsSettled)
        {
            Value = Target;
            Velocity = 0;
            return true;
        }
        return false;
    }

    public void Snap(double v)
    {
        Value = Target = v;
        Velocity = 0;
    }
}
