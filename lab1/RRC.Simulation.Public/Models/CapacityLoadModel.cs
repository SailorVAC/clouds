using RRC.Abstractions;

namespace RRC.Simulation.Public;

public sealed class CapacityLoadModel : ILoadModel
{
    public int Capacity { get; }

    public CapacityLoadModel(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        Capacity = capacity;
    }

    public TimeSpan ApplyLoad(TimeSpan baseLatency, int inFlightAttempts)
    {
        if (baseLatency < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(baseLatency));
        if (inFlightAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(inFlightAttempts));
        decimal factor = Math.Max(1m, (decimal)inFlightAttempts / Capacity);
        decimal ticks = baseLatency.Ticks * factor;
        // Saturating means extreme overload completes beyond any practical simulation cutoff.
        return TimeSpan.FromTicks((long)Math.Min(ticks, long.MaxValue));
    }
}
