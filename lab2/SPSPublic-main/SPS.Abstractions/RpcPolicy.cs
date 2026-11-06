namespace SPS.Abstractions;

public enum RequestOutcome { Success, Timeout, Rejected, Unavailable, Stale, PolicyRejected }

public sealed class RequestContext
{
    public long RequestId { get; }
    public int Attempt { get; }

    public RequestContext(long RequestId, int Attempt)
    {
        this.RequestId = RequestId;
        this.Attempt = Attempt;
    }
}

public sealed class RequestObservation
{
    public long RequestId { get; }
    public int Attempt { get; }
    public RequestOutcome Outcome { get; }
    public TimeSpan Elapsed { get; }

    public RequestObservation(long RequestId, int Attempt, RequestOutcome Outcome, TimeSpan Elapsed)
    {
        this.RequestId = RequestId;
        this.Attempt = Attempt;
        this.Outcome = Outcome;
        this.Elapsed = Elapsed;
    }
}

public sealed class RetryDecision
{
    public bool Retry { get; }
    public TimeSpan Delay { get; }

    public RetryDecision(bool Retry, TimeSpan Delay)
    {
        this.Retry = Retry;
        this.Delay = Delay;
    }

    public static RetryDecision NoRetry() => new(false, TimeSpan.Zero);
    public static RetryDecision RetryAfter(TimeSpan delay) => new(true, delay);
}

public interface IRequestPolicy
{
    TimeSpan GetTimeout(RequestContext context, Random random);
    RetryDecision OnResult(RequestObservation observation, Random random);
}
