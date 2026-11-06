namespace RRC.Simulation;

public sealed class SimulationOptions
{
    public int Seed { get; }
    public int TraceRequestCount { get; }
    public long MaximumEventCount { get; }

    public SimulationOptions(
        int seed = 42,
        int traceRequestCount = 0,
        long maximumEventCount = 10_000_000)
    {
        Seed = seed;
        TraceRequestCount = traceRequestCount;
        MaximumEventCount = maximumEventCount;
    }

    public const int MaximumTraceRequestCount = 100;
}
