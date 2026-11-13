using SPS.Abstractions;

namespace SPS.Student;

public sealed class MySystemPolicy : ISystemPolicy
{
    private readonly Dictionary<string, double> popularity = new();
    public IRequestPolicy RpcPolicy { get; } = new Rpc();

    public CacheReadDecision OnCacheRead(CacheReadContext c, Random random)
    {
        popularity.TryGetValue(c.Request.ResourceKey, out double hits);
        popularity[c.Request.ResourceKey] = Math.Min(100, hits + 1);
        if (c.Entry != null && c.Entry.ExpiresAt > c.Now &&
            c.Now - c.Entry.SourceObservedAt + TimeSpan.FromMilliseconds(20) <= c.Request.MaxDataAge)
            return CacheReadDecision.ServeCached();
        var fetch = c.PendingFetches.OrderByDescending(f => f.StartedAt).FirstOrDefault();
        if (fetch != null && c.Now - fetch.StartedAt < c.Request.MaxDataAge / 2)
            return CacheReadDecision.JoinFetch(fetch.Id);
        return CacheReadDecision.StartFetch();
    }

    public CacheWriteDecision OnCacheFill(CacheFillContext c, Random random)
    {
        if (c.Value.SizeBytes > c.CapacityBytes) return CacheWriteDecision.Skip();
        long size = c.Value.SizeBytes + c.Entries.Where(e => e.Key != c.Value.Key).Sum(e => e.SizeBytes);
        var evict = new List<string>();
        foreach (var entry in c.Entries.Where(e => e.Key != c.Value.Key)
                     .OrderBy(e => popularity.GetValueOrDefault(e.Key) / Math.Max(1, e.SizeBytes)))
        {
            if (size <= c.CapacityBytes) break;
            size -= entry.SizeBytes;
            evict.Add(entry.Key);
        }
        return CacheWriteDecision.Retain(TimeSpan.FromSeconds(30), evict.ToArray());
    }

    public EndpointSelectionDecision? SelectEndpoint(RoutingContext c, Random random)
    {
        var endpoint = c.Endpoints.Where(e => e.State == InstanceState.Ready && e.Mode == EndpointMode.AllowTraffic)
            .OrderBy(e => (e.OutstandingCalls + 1.0) / Math.Max(0.01, e.Speed * e.Slots)).FirstOrDefault();
        return endpoint == null ? null : EndpointSelectionDecision.Assign(endpoint.Id);
    }

    public EndpointDecision ReviewEndpoint(EndpointContext c, Random random) => EndpointDecision.Allow();

    public JobAssignmentDecision? SelectJobAssignment(JobSchedulingContext c, Random random)
    {
        var worker = c.AvailableWorkers.OrderByDescending(w => w.Speed).FirstOrDefault();
        var job = c.QueuedJobs.OrderByDescending(j => (double)j.Request.Revenue /
            Math.Max(0.1, j.Request.EstimatedWorkSeconds)).FirstOrDefault();
        return worker == null || job == null ? null : JobAssignmentDecision.Assign(job.Id, worker.Id);
    }

    public JobRecoveryDecision ReviewJob(JobRecoveryContext c, Random random)
    {
        var last = c.Job.LastHeartbeat ?? c.Job.AssignedAt;
        return last.HasValue && c.Now - last.Value > TimeSpan.FromSeconds(8)
            ? JobRecoveryDecision.RequeueAfter(TimeSpan.Zero) : JobRecoveryDecision.KeepWaiting();
    }

    public CapacityPlan ReviewCapacity(CapacityContext c, Random random)
    {
        int apis = Math.Min(2, c.Settings.MaximumApis);
        int workers = Math.Min(2, c.Settings.MaximumWorkers);
        while (apis * c.Settings.Api.ComputeUnits + workers * c.Settings.Worker.ComputeUnits > c.Settings.ComputeUnitLimit)
        {
            if (workers > 0) workers--; else if (apis > 0) apis--; else break;
        }
        return new CapacityPlan(apis, workers);
    }

    public void Observe(SystemObservation observation) { }

    private sealed class Rpc : IRequestPolicy
    {
        public TimeSpan GetTimeout(RequestContext c, Random random) => TimeSpan.FromMilliseconds(400 * Math.Pow(2, Math.Min(2, Math.Max(0, c.Attempt - 1))));
        public RetryDecision OnResult(RequestObservation o, Random random) =>
            o.Attempt < 3 && o.Outcome is RequestOutcome.Timeout or RequestOutcome.Unavailable or RequestOutcome.Rejected
                ? RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(30 + random.Next(40))) : RetryDecision.NoRetry();
    }
}
