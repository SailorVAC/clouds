using RRC.Abstractions;

namespace RRC.Simulation.Public;

public sealed class SimpleRandomLatencyModel : ILatencyModel
{
    public TimeSpan Minimum { get; }
    public TimeSpan Maximum { get; }

    public SimpleRandomLatencyModel(TimeSpan minimum, TimeSpan maximum)
    {
        if (minimum < TimeSpan.Zero || maximum < minimum)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Latency bounds must satisfy 0 <= minimum <= maximum.");
        Minimum = minimum;
        Maximum = maximum;
    }

    public TimeSpan GetBaseLatency(TimeSpan currentTime, Random random) =>
        TimeSpan.FromTicks(Minimum.Ticks + (long)((decimal)random.NextDouble() * (Maximum.Ticks - Minimum.Ticks)));
}
