namespace SPS.Abstractions;

public enum AssignmentAction { Assign, Reject }

public enum ObservationKind
{
    // Request outcomes describe service processing, not payment or hidden customer departure.
    SimulationStarted, RequestArrived, RequestSucceeded, RequestFailed,
    RpcAttemptStarted, RpcAttemptCompleted, ProbeCompleted, WorkerHeartbeatReceived,
    JobAssigned, JobRequeued, JobCompleted, CacheEntryRemoved, FetchCompleted,
    InstanceStarting, InstanceReady, InstanceDraining, InstanceStopped
}

// RequestFailed always supplies Elapsed (since arrival) and FailureReason, even after hidden abandonment.
public sealed class SystemObservation
{
    public ObservationKind Kind { get; }
    public TimeSpan Now { get; }
    public long? RequestId { get; }
    public long? EndpointId { get; }
    public long? JobId { get; }
    public RequestOutcome? Outcome { get; }
    public TimeSpan? Elapsed { get; }
    public string? Key { get; }
    public SystemSettings? Settings { get; }
    public RequestFailureReason? FailureReason { get; }

    public SystemObservation(ObservationKind Kind, TimeSpan Now, long? RequestId = null, long? EndpointId = null,
        long? JobId = null, RequestOutcome? Outcome = null, TimeSpan? Elapsed = null, string? Key = null,
        SystemSettings? Settings = null, RequestFailureReason? FailureReason = null)
    {
        this.Kind = Kind;
        this.Now = Now;
        this.RequestId = RequestId;
        this.EndpointId = EndpointId;
        this.JobId = JobId;
        this.Outcome = Outcome;
        this.Elapsed = Elapsed;
        this.Key = Key;
        this.Settings = Settings;
        this.FailureReason = FailureReason;
    }
}

public interface ISystemPolicy
{
    IRequestPolicy RpcPolicy { get; }
    // Called once on arrival. Null leaves processing unlimited until the scenario ends.
    // This is a service decision, unrelated to hidden customer patience.
    TimeSpan? GetProcessingBudget(RequestInfo request, Random random) => null;
    EndpointSelectionDecision? SelectEndpoint(RoutingContext context, Random random);
    EndpointDecision ReviewEndpoint(EndpointContext context, Random random);
    CacheReadDecision OnCacheRead(CacheReadContext context, Random random);
    CacheWriteDecision OnCacheFill(CacheFillContext context, Random random);
    JobAssignmentDecision? SelectJobAssignment(JobSchedulingContext context, Random random);
    JobRecoveryDecision ReviewJob(JobRecoveryContext context, Random random);
    CapacityPlan ReviewCapacity(CapacityContext context, Random random);
    void Observe(SystemObservation observation);
}
