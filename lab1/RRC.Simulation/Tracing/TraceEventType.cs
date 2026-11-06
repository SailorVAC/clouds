namespace RRC.Simulation;

public enum TraceEventType
{
    RequestStarted,
    AttemptStarted,
    Timeout,
    RetryScheduled,
    ServerCompleted,
    Success,
    LateResponse,
    RequestFailed
}
