namespace RRC.Simulation;

public sealed class SimulationMetrics
{
    public double SuccessRate { get; }
    public TimeSpan? MeanLatency { get; }
    public TimeSpan? P95Latency { get; }
    public double AttemptsPerRequest { get; }

    public SimulationMetrics(
        double successRate,
        TimeSpan? meanLatency,
        TimeSpan? p95Latency,
        double attemptsPerRequest)
    {
        SuccessRate = successRate;
        MeanLatency = meanLatency;
        P95Latency = p95Latency;
        AttemptsPerRequest = attemptsPerRequest;
    }
}
