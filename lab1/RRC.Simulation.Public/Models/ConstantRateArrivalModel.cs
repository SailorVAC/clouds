using RRC.Abstractions;

namespace RRC.Simulation.Public;

public sealed class ConstantRateArrivalModel : IArrivalModel
{
    public double RequestsPerSecond { get; }
    public TimeSpan Interval { get; }

    public ConstantRateArrivalModel(double requestsPerSecond)
    {
        double ticks = TimeSpan.TicksPerSecond / requestsPerSecond;
        if (!double.IsFinite(requestsPerSecond) || requestsPerSecond <= 0 || ticks < 1 || ticks >= long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestsPerSecond), "Rate must be positive and produce a representable interval of at least one tick.");
        RequestsPerSecond = requestsPerSecond;
        Interval = TimeSpan.FromTicks((long)Math.Round(ticks));
    }

    public TimeSpan GetNextDelay(TimeSpan currentTime, Random random) => Interval;
}
