namespace RRC.Simulation;

public sealed class TraceDetails
{
    public TimeSpan? Timeout { get; }
    public TimeSpan? Elapsed { get; }
    public TimeSpan? RetryDelay { get; }
    public int? InFlightAttempts { get; }
    public bool SimulationEnded { get; }

    public TraceDetails(
        TimeSpan? timeout = null,
        TimeSpan? elapsed = null,
        TimeSpan? retryDelay = null,
        int? inFlightAttempts = null,
        bool simulationEnded = false)
    {
        Timeout = timeout;
        Elapsed = elapsed;
        RetryDelay = retryDelay;
        InFlightAttempts = inFlightAttempts;
        SimulationEnded = simulationEnded;
    }
}
