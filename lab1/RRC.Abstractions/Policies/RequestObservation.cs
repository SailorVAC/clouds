namespace RRC.Abstractions;

public sealed class RequestObservation
{
    public long RequestId { get; }
    public int Attempt { get; }
    public RequestOutcome Outcome { get; }
    public TimeSpan Elapsed { get; }

    public RequestObservation(
        long requestId,
        int attempt,
        RequestOutcome outcome,
        TimeSpan elapsed)
    {
        RequestId = requestId;
        Attempt = attempt;
        Outcome = outcome;
        Elapsed = elapsed;
    }
}
