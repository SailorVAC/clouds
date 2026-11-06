namespace RRC.Simulation;

public sealed class SimulationTraceEvent
{
    public TimeSpan Time { get; }
    public long RequestId { get; }
    public int Attempt { get; }
    public TraceEventType EventType { get; }
    public TraceDetails Details { get; }

    public SimulationTraceEvent(
        TimeSpan time,
        long requestId,
        int attempt,
        TraceEventType eventType,
        TraceDetails details)
    {
        Time = time;
        RequestId = requestId;
        Attempt = attempt;
        EventType = eventType;
        Details = details;
    }
}
