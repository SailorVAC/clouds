namespace SPS.Abstractions;

public enum RequestKind { Metadata, Preview, Generate }

// Technical termination of service processing; never customer departure or payment status.
public enum RequestFailureReason
{
    ProcessingBudgetExceeded, PolicyRejected, StaleResponse, FetchFailed, SubmissionFailed, SimulationEnded
}

// Customer patience and earned revenue are deliberately absent from policy observations.
public sealed class RequestInfo
{
    public long Id { get; }
    public RequestKind Kind { get; }
    public TimeSpan CreatedAt { get; }
    public decimal Revenue { get; }
    public string ResourceKey { get; }
    public TimeSpan MaxDataAge { get; }
    public double EstimatedWorkSeconds { get; }

    public RequestInfo(long Id, RequestKind Kind, TimeSpan CreatedAt, decimal Revenue, string ResourceKey,
        TimeSpan MaxDataAge, double EstimatedWorkSeconds)
    {
        this.Id = Id;
        this.Kind = Kind;
        this.CreatedAt = CreatedAt;
        this.Revenue = Revenue;
        this.ResourceKey = ResourceKey;
        this.MaxDataAge = MaxDataAge;
        this.EstimatedWorkSeconds = EstimatedWorkSeconds;
    }
}
