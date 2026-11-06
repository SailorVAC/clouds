using SPS.Abstractions;
using SPS.Telemetry;

namespace SPS.Simulation;

public sealed partial class SimulationSession
{
    private readonly SimulationScenario scenario;
    private readonly TimeSpan finalTime;
    private readonly ISystemPolicy policy;
    private readonly IRequestPolicy rpcPolicy;
    private readonly SimulationOptions options;
    private readonly ISimulationObserver? observer;
    private readonly Random policyRandom, networkRandom;
    private readonly PriorityQueue<Action, (long Time, int Priority, long Sequence)> events = new();
    private readonly List<Customer> customers = new();
    private readonly List<Instance> instances = new();
    private readonly Dictionary<long, Rpc> rpcs = new();
    private readonly Dictionary<long, Fetch> fetches = new();
    private readonly Dictionary<long, Job> jobs = new();
    // Preserve registration order without scanning completed jobs or reusing dictionary slots.
    private readonly LinkedList<Job> liveJobs = new();
    private readonly HashSet<long> registeredJobs = new();
    private readonly Dictionary<string, CacheEntryInfo> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> sourceVersions = new(StringComparer.Ordinal);
    private readonly Queue<StorageTask> storageQueue = new();
    private readonly List<SimulationEvent> trace = new();
    private long sequence, telemetrySequence, nextInstance, nextRpc, nextFetch;
    private long attempts, retries, timeouts, rejections, policyRejections, cacheHits, coalesced, fetchCount,
        reassignments, duplicates, obsoletePublications, lateResponses, staleResponses, expired,
        wastedTicks, queueTicks, queueEntries, processedEvents;
    private int storageActive;
    private decimal revenue, apiAllocatedTicks, workerAllocatedTicks;
    private int allocatedApis, allocatedWorkers;
    private decimal apiSeconds => apiAllocatedTicks / TimeSpan.TicksPerSecond;
    private decimal workerSeconds => workerAllocatedTicks / TimeSpan.TicksPerSecond;
    private decimal apiCost => apiSeconds * scenario.Settings.Api.PricePerSecond;
    private decimal workerCost => workerSeconds * scenario.Settings.Worker.PricePerSecond;
    private bool CaptureEventDetails => observer != null || options.TraceRequestCount > 0;
    private bool completed;
    public TimeSpan Now { get; private set; }
    public bool IsCompleted => completed;

    public SimulationSession(SimulationScenario scenario, ISystemPolicy policy, SimulationOptions? options = null,
        ISimulationObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(policy);
        scenario.Validate();
        this.scenario = scenario;
        finalTime = scenario.FinalTime;
        this.policy = policy;
        rpcPolicy = policy.RpcPolicy ?? throw new ArgumentException("RpcPolicy must not be null.");
        this.options = options ?? new();
        if (this.options.TraceRequestCount is < 0 or > 1000 || this.options.MaximumEventCount <= 0 ||
            this.options.SnapshotInterval is { } interval && interval <= TimeSpan.Zero)
            throw new ArgumentException("Invalid trace, snapshot, or event-count option.");
        this.observer = observer;
        policyRandom = new Random(unchecked(this.options.Seed ^ 0x6a09e667));
        networkRandom = new Random(unchecked(this.options.Seed ^ 0x5f3759df));
        observer?.OnStarted(new(2, scenario.Name, this.options.Seed, (policy.GetType().FullName ?? "unknown") + "@" + policy.GetType().Assembly.ManifestModule.ModuleVersionId,
            "SPS/2.4@" + typeof(SimulationSession).Assembly.ManifestModule.ModuleVersionId, scenario.GenerationDuration, finalTime, scenario.Settings));
        Notify(new(ObservationKind.SimulationStarted, Now, Settings: scenario.Settings));
        for (int i = 0; i < 2; i++) CreateInstance(InstanceRole.Api, true);
        for (int i = 0; i < 2; i++) CreateInstance(InstanceRole.Worker, true);
        GenerateWorkload();
        ScheduleSourceUpdates();
        foreach (var fault in scenario.Faults)
        {
            Schedule(Seconds(fault.StartSeconds), () => ApplyFaultChange(fault), 5);
            Schedule(Seconds(fault.EndSeconds), () => ApplyFaultChange(fault), 5);
        }
        Schedule(TimeSpan.Zero, Review, 40);
        Schedule(TimeSpan.Zero, ReviewCapacity, 45);
        Schedule(scenario.Settings.HeartbeatInterval, Heartbeats, 25);
        if (observer != null) Schedule(TimeSpan.Zero, SnapshotTick, 90);
        Schedule(finalTime, CompleteSession, 100);
    }
    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);
    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> source) => Array.AsReadOnly(source.ToArray());
    private void Schedule(TimeSpan at, Action action, int priority = 20)
    {
        if (at < Now) throw new InvalidOperationException("Cannot schedule an event in the past.");
        if (at > finalTime || completed) return;
        events.Enqueue(action, (at.Ticks, priority, ++sequence));
    }
    private TimeSpan After(TimeSpan delay) => delay > TimeSpan.MaxValue - Now ? TimeSpan.MaxValue : Now + delay;
    public bool Step()
    {
        if (completed || !events.TryDequeue(out var action, out var priority)) return false;
        if (priority.Priority != 90 && ++processedEvents > options.MaximumEventCount)
            throw new InvalidOperationException($"Event limit exceeded at {Now}. Check policy retry/recovery behavior.");
        MoveTo(TimeSpan.FromTicks(priority.Time));
        action();
        return true;
    }
    public void AdvanceTo(TimeSpan time)
    {
        if (time < Now || time > finalTime) throw new ArgumentOutOfRangeException(nameof(time));
        while (!completed && events.TryPeek(out _, out var next) && next.Time <= time.Ticks) Step();
        if (!completed) MoveTo(time);
    }
    private void MoveTo(TimeSpan time)
    {
        // Accumulate exact instance-ticks. Startup, outages, and draining remain billable.
        long elapsedTicks = (time - Now).Ticks;
        if (elapsedTicks != 0)
        {
            apiAllocatedTicks += (decimal)elapsedTicks * allocatedApis;
            workerAllocatedTicks += (decimal)elapsedTicks * allocatedWorkers;
            Now = time;
        }
    }
    private decimal InstanceCost(Instance instance) =>
        (decimal)((instance.StoppedAt ?? Now) - instance.AllocatedAt).Ticks /
        TimeSpan.TicksPerSecond * instance.Spec.PricePerSecond;
    private T Decide<T>(string method, Func<T> decision)
    {
        try { return decision(); }
        catch (Exception e) { throw new InvalidOperationException($"Policy {method} failed at {Now.TotalSeconds:F3}s: {e.Message}", e); }
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Invalid(string method, string reason) => throw new InvalidOperationException($"Invalid {method} decision at {Now.TotalSeconds:F3}s: {reason}");
    private void Notify(SystemObservation observation) => Decide("Observe", () => { policy.Observe(observation); return true; });
    private void Emit(string kind, Customer? customer = null, Instance? instance = null, Job? job = null, string? detail = null)
    {
        // Keep sequence numbers identical even when this event is not being captured.
        long eventSequence = ++telemetrySequence;
        bool traced = customer != null && customer.Info.Id < options.TraceRequestCount;
        if (observer == null && !traced) return;
        var item = new SimulationEvent(eventSequence, Now, kind, customer?.Info.Id, instance?.Id, job?.Id, detail);
        if (traced) trace.Add(item);
        observer?.OnEvent(item);
    }
    private void GenerateWorkload()
    {
        var random = new Random(options.Seed);
        // Request variants never consume the arrival/key/work/patience random stream.
        var variantRandom = new Random(unchecked(options.Seed ^ 0x3c6ef372));
        long id = 0;
        for (int p = 0; p < scenario.Phases.Count; p++)
        {
            var phase = scenario.Phases[p];
            double end = p + 1 < scenario.Phases.Count ? scenario.Phases[p + 1].StartSeconds : scenario.GenerationDuration.TotalSeconds;
            double time = phase.StartSeconds;
            while (time < end)
            {
                double kindDraw = random.NextDouble();
                var kind = kindDraw < phase.JobFraction ? RequestKind.Generate :
                    kindDraw < phase.JobFraction + phase.PreviewFraction ? RequestKind.Preview : RequestKind.Metadata;
                var profile = scenario.Profiles.Single(x => x.Kind == kind);
                int key = random.NextDouble() < phase.HotKeyFraction ? random.Next(phase.HotKeyCount) : random.Next(scenario.ResourceKeyCount);
                key = (key + phase.KeyOffset) % scenario.ResourceKeyCount;
                var createdAt = Seconds(time);
                double patience = profile.MinPatienceSeconds + random.NextDouble() * (profile.MaxPatienceSeconds - profile.MinPatienceSeconds);
                double work = profile.MinWorkerSeconds + random.NextDouble() * (profile.MaxWorkerSeconds - profile.MinWorkerSeconds);
                var variant = ChooseVariant(profile, variantRandom);
                var request = new Customer
                {
                    Info = new(id++, kind, createdAt, variant?.Revenue ?? profile.Revenue,
                        $"{kind}:{key}", Seconds(variant?.MaxDataAgeSeconds ?? profile.MaxDataAgeSeconds), work * (.75 + random.NextDouble() * .5)),
                    Profile = profile, ActualWorkerWork = work, PatienceDeadline = createdAt + Seconds(patience)
                };
                customers.Add(request);
                Schedule(createdAt, () => Arrive(request), 30);
                Schedule(request.PatienceDeadline, () => Abandon(request), 80);
                time += -Math.Log(1 - random.NextDouble()) / phase.RequestsPerSecond;
            }
        }
    }
    private static RequestVariant? ChooseVariant(RequestProfile profile, Random random)
    {
        if (profile.Variants.Count == 0) return null;
        double remaining = random.NextDouble() * profile.Variants.Sum(v => v.Weight);
        foreach (var variant in profile.Variants)
        {
            if (remaining < variant.Weight) return variant;
            remaining -= variant.Weight;
        }
        return profile.Variants[^1];
    }
    private void ScheduleSourceUpdates()
    {
        var random = new Random(unchecked(options.Seed ^ 0x1b873593));
        for (var at = scenario.SourceUpdateInterval; at < finalTime; at += scenario.SourceUpdateInterval)
        {
            int key = random.Next(scenario.ResourceKeyCount);
            Schedule(at, () =>
            {
                foreach (var kind in new[] { RequestKind.Metadata, RequestKind.Preview })
                {
                    string resource = $"{kind}:{key}";
                    sourceVersions[resource] = sourceVersions.GetValueOrDefault(resource, 1) + 1;
                }
                Emit("SourceUpdated", detail: CaptureEventDetails ? key.ToString() : null);
            }, 10);
        }
    }
    private void Arrive(Customer customer)
    {
        customer.Arrived = true;
        Notify(new(ObservationKind.RequestArrived, Now, RequestId: customer.Info.Id));
        Emit("RequestArrived", customer, detail: CaptureEventDetails ? FormattableString.Invariant($"{customer.Info.Kind}; patienceDeadline={customer.PatienceDeadline.TotalSeconds:F6}; revenue={customer.Info.Revenue}; key={customer.Info.ResourceKey}; maxAge={customer.Info.MaxDataAge.TotalSeconds:F3}") : null);
        var budget = Decide("GetProcessingBudget", () => policy.GetProcessingBudget(customer.Info, policyRandom));
        if (budget is { } duration)
        {
            if (duration <= TimeSpan.Zero) Invalid("GetProcessingBudget", $"request {customer.Info.Id}: budget must be positive or null");
            customer.ProcessingDeadline = After(duration);
            Schedule(customer.ProcessingDeadline.Value, () => Fail(customer, RequestFailureReason.ProcessingBudgetExceeded), 75);
            Emit("ProcessingBudgetSet", customer, detail: CaptureEventDetails ? $"deadline={customer.ProcessingDeadline.Value.TotalSeconds:F6}" : null);
        }
        if (customer.Info.Kind == RequestKind.Generate) CreateRpc(customer, null);
        else ReadCache(customer);
    }
    private void Abandon(Customer customer)
    {
        // Hidden accounting only: no service cancellation, feedback, or change in visible contexts.
        if (customer.Terminal) return;
        customer.Terminal = true;
        customer.TerminatedAt = Now;
        expired++;
        Emit("CustomerAbandoned", customer);
    }
    private void Succeed(Customer customer)
    {
        if (customer.ProcessingEnded) return;
        customer.ProcessingEnded = true;
        var elapsed = Now - customer.Info.CreatedAt;
        Notify(new(ObservationKind.RequestSucceeded, Now, RequestId: customer.Info.Id, Elapsed: elapsed));
        Emit("RequestProcessingSucceeded", customer);
        if (customer.Terminal) return;
        if (Now > customer.PatienceDeadline) { Abandon(customer); return; }
        customer.Terminal = customer.Success = true;
        customer.TerminatedAt = Now;
        customer.Latency = elapsed;
        revenue += customer.Info.Revenue;
        Emit("RequestSucceeded", customer, detail: CaptureEventDetails ? customer.Info.Revenue.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }
    private void RejectByPolicy(Customer customer, string method)
    {
        if (customer.ProcessingEnded) return;
        policyRejections++;
        Emit("RequestRejectedByPolicy", customer, job: jobs.GetValueOrDefault(customer.Info.Id), detail: method);
        Fail(customer, RequestFailureReason.PolicyRejected, RequestOutcome.PolicyRejected, "policy rejection: " + method);
        RemoveRejectedQueuedWork(customer);
    }
    private void Fail(Customer customer, RequestFailureReason reason, RequestOutcome? outcome = null, string? detail = null)
    {
        if (customer.ProcessingEnded) return;
        customer.ProcessingEnded = true;
        if (jobs.TryGetValue(customer.Info.Id, out var job)) { CompleteJob(job); job.Generation++; }
        var elapsed = Now - customer.Info.CreatedAt;
        Notify(new(ObservationKind.RequestFailed, Now, RequestId: customer.Info.Id, Outcome: outcome,
            Elapsed: elapsed, FailureReason: reason));
        // Keep diagnostic event details stable for existing recordings and visualizer consumers.
        string diagnosticReason = detail ?? (reason switch
        {
            RequestFailureReason.ProcessingBudgetExceeded => "processing budget",
            RequestFailureReason.PolicyRejected => "policy rejection",
            RequestFailureReason.StaleResponse => "stale response",
            RequestFailureReason.FetchFailed => "fetch failed",
            RequestFailureReason.SubmissionFailed => "submission failed",
            RequestFailureReason.SimulationEnded => "simulation ended",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        });
        Emit("RequestProcessingFailed", customer, detail: diagnosticReason);
        if (!customer.Terminal)
        {
            if (Now >= customer.PatienceDeadline) Abandon(customer);
            else
            {
                customer.Terminal = true;
                customer.TerminatedAt = Now;
                Emit("RequestFailed", customer, detail: diagnosticReason);
            }
        }
        foreach (var rpc in rpcs.Values.Where(r => !RpcLive(r)).ToArray()) FinishRpc(rpc, false, outcome);
    }
    private void SnapshotTick()
    {
        observer?.OnSnapshot(GetSnapshot());
        var interval = options.SnapshotInterval ?? TimeSpan.FromSeconds(1);
        if (interval <= finalTime - Now) Schedule(Now + interval, SnapshotTick, 90);
    }
    public SimulationSnapshot GetSnapshot() => new(Now,
        ReadOnly(instances.Select(i => new InstanceSnapshot(i.Id, i.Role, i.State, i.Crashed,
            HasFault(i, FaultKind.NetworkIsolation), i.Mode, i.Active.Count, i.Queue.Count,
            i.Spec.Slots, i.Speed, i.Spec.PricePerSecond, i.AllocatedAt, i.StoppedAt, InstanceCost(i)))),
        ReadOnly(new[] { new QueueSnapshot("storage", storageQueue.Count, storageActive),
            new QueueSnapshot("jobs", liveJobs.Count(j => j.Queued), liveJobs.Count(j => !j.Queued)) }),
        new(cache.Count, cache.Values.Sum(c => c.SizeBytes), scenario.Settings.CacheCapacityBytes, fetches.Count),
        new(revenue, apiCost, workerCost), customers.LongCount(c => c.Arrived),
        customers.LongCount(c => c.Success), customers.LongCount(c => c.Terminal && !c.Success));
    private void CompleteSession()
    {
        foreach (var customer in customers) Fail(customer, RequestFailureReason.SimulationEnded);
        completed = true;
        foreach (var instance in instances.Where(i => i.State != InstanceState.Stopped).ToArray()) StopInstance(instance);
        observer?.OnSnapshot(GetSnapshot());
    }
    public SimulationResult GetResult()
    {
        if (!completed) throw new InvalidOperationException("The session has not completed.");
        var groups = Enum.GetValues<RequestKind>().Select(kind =>
        {
            var all = customers.Where(c => c.Info.Kind == kind).ToArray();
            var latencies = all.Where(c => c.Success).Select(c => c.Latency!.Value.Ticks).Order().ToArray();
            return new RequestKindMetrics(kind, all.LongLength, latencies.LongLength, all.LongLength - latencies.LongLength,
                all.Sum(c => c.Info.Revenue), all.Where(c => c.Success).Sum(c => c.Info.Revenue),
                latencies.Length == 0 ? null : TimeSpan.FromTicks((long)(latencies.Sum(t => (decimal)t) / latencies.Length)),
                latencies.Length == 0 ? null : TimeSpan.FromTicks(latencies[(int)Math.Ceiling(latencies.Length * .95) - 1]));
        });
        return new(scenario.Name, options.Seed, Now, new(revenue, apiCost, workerCost), ReadOnly(groups),
            new(attempts, retries, timeouts, rejections, policyRejections, cacheHits, coalesced, fetchCount, reassignments,
                duplicates, obsoletePublications, lateResponses, staleResponses, expired, TimeSpan.FromTicks(wastedTicks),
                TimeSpan.FromTicks(queueTicks), queueEntries, processedEvents, apiSeconds, workerSeconds),
            ReadOnly(instances.Select(i => new InstanceLifetime(i.Id, i.Role, i.AllocatedAt, i.StoppedAt ?? Now, InstanceCost(i)))),
            ReadOnly(trace));
    }
}



