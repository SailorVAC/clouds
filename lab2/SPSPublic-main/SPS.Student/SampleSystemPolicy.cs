using SPS.Abstractions;

namespace SPS.Student;

// Minimal contract example. Replace these decisions with your own strategy.
public sealed class SampleSystemPolicy : ISystemPolicy
{
    public IRequestPolicy RpcPolicy { get; } = new SampleRequestPolicy();

    public EndpointSelectionDecision? SelectEndpoint(RoutingContext context, Random random)
    {
        if (context.Endpoints.Count == 0) return null;
        return EndpointSelectionDecision.Assign(context.Endpoints[0].Id);
    }

    public EndpointDecision ReviewEndpoint(EndpointContext context, Random random) => EndpointDecision.Allow();

    public CacheReadDecision OnCacheRead(CacheReadContext context, Random random) => CacheReadDecision.StartFetch();

    public CacheWriteDecision OnCacheFill(CacheFillContext context, Random random) => CacheWriteDecision.Skip();

    public JobAssignmentDecision? SelectJobAssignment(JobSchedulingContext context, Random random)
    {
        if (context.QueuedJobs.Count == 0 || context.AvailableWorkers.Count == 0) return null;
        return JobAssignmentDecision.Assign(context.QueuedJobs[0].Id, context.AvailableWorkers[0].Id);
    }

    public JobRecoveryDecision ReviewJob(JobRecoveryContext context, Random random) => JobRecoveryDecision.KeepWaiting();

    public CapacityPlan ReviewCapacity(CapacityContext context, Random random) => new(2, 2);

    public void Observe(SystemObservation observation) { }

    private sealed class SampleRequestPolicy : IRequestPolicy
    {
        public TimeSpan GetTimeout(RequestContext context, Random random) => TimeSpan.FromMilliseconds(120);

        public RetryDecision OnResult(RequestObservation observation, Random random) => RetryDecision.NoRetry();
    }
}
