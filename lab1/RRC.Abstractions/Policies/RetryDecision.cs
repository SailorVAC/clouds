namespace RRC.Abstractions;

public sealed class RetryDecision
{
    public bool Retry { get; }
    public TimeSpan Delay { get; }

    public RetryDecision(
        bool retry,
        TimeSpan delay)
    {
        Retry = retry;
        Delay = delay;
    }

    public static RetryDecision NoRetry() => new(false, TimeSpan.Zero);
    public static RetryDecision RetryAfter(TimeSpan delay) => new(true, delay);

    public override string ToString() => $"RetryDecision {{ Retry = {Retry}, Delay = {Delay} }}";
}
