namespace RRC.Simulation;

public sealed class SimulationDiagnostics
{
    public long TotalLogicalRequests { get; }
    public long SuccessfulRequests { get; }
    public long FailedRequests { get; }
    public long TotalAttempts { get; }
    public long TotalTimeouts { get; }
    public long TotalRetries { get; }
    public long LateResponses { get; }
    public int MaximumInFlightAttempts { get; }
    public TimeSpan? MeanChosenTimeout { get; }

    public SimulationDiagnostics(
        long totalLogicalRequests,
        long successfulRequests,
        long failedRequests,
        long totalAttempts,
        long totalTimeouts,
        long totalRetries,
        long lateResponses,
        int maximumInFlightAttempts,
        TimeSpan? meanChosenTimeout)
    {
        TotalLogicalRequests = totalLogicalRequests;
        SuccessfulRequests = successfulRequests;
        FailedRequests = failedRequests;
        TotalAttempts = totalAttempts;
        TotalTimeouts = totalTimeouts;
        TotalRetries = totalRetries;
        LateResponses = lateResponses;
        MaximumInFlightAttempts = maximumInFlightAttempts;
        MeanChosenTimeout = meanChosenTimeout;
    }
}
