using SPS.Abstractions;
using SPS.Telemetry;

namespace SPS.Simulation;

public sealed class RequestKindMetrics
{
    public RequestKind Kind { get; }
    public long Requests { get; }
    public long Successful { get; }
    public long Failed { get; }
    public decimal PotentialRevenue { get; }
    public decimal EarnedRevenue { get; }
    public TimeSpan? MeanLatency { get; }
    public TimeSpan? P95Latency { get; }

    public RequestKindMetrics(RequestKind Kind, long Requests, long Successful, long Failed, decimal PotentialRevenue,
        decimal EarnedRevenue, TimeSpan? MeanLatency, TimeSpan? P95Latency)
    {
        this.Kind = Kind;
        this.Requests = Requests;
        this.Successful = Successful;
        this.Failed = Failed;
        this.PotentialRevenue = PotentialRevenue;
        this.EarnedRevenue = EarnedRevenue;
        this.MeanLatency = MeanLatency;
        this.P95Latency = P95Latency;
    }

    public decimal LostRevenue => PotentialRevenue - EarnedRevenue;
    public double OnTimeRate => Requests == 0 ? 0 : (double)Successful / Requests;
}

public sealed class SimulationDiagnostics
{
    public long Attempts { get; }
    public long Retries { get; }
    public long Timeouts { get; }
    public long Rejections { get; }
    public long PolicyRejections { get; }
    public long CacheHits { get; }
    public long CoalescedCustomers { get; }
    public long Fetches { get; }
    public long JobReassignments { get; }
    public long DuplicateJobExecutions { get; }
    public long ObsoletePublications { get; }
    public long LateResponses { get; }
    public long StaleResponses { get; }
    public long ExpiredRequests { get; }
    public TimeSpan WastedInstanceTime { get; }
    public TimeSpan TotalQueueTime { get; }
    public long QueueEntries { get; }
    public long ProcessedEvents { get; }
    public decimal ApiInstanceSeconds { get; }
    public decimal WorkerInstanceSeconds { get; }

    public SimulationDiagnostics(long Attempts, long Retries, long Timeouts, long Rejections, long PolicyRejections,
        long CacheHits, long CoalescedCustomers, long Fetches, long JobReassignments, long DuplicateJobExecutions,
        long ObsoletePublications, long LateResponses, long StaleResponses, long ExpiredRequests,
        TimeSpan WastedInstanceTime, TimeSpan TotalQueueTime, long QueueEntries, long ProcessedEvents,
        decimal ApiInstanceSeconds, decimal WorkerInstanceSeconds)
    {
        this.Attempts = Attempts;
        this.Retries = Retries;
        this.Timeouts = Timeouts;
        this.Rejections = Rejections;
        this.PolicyRejections = PolicyRejections;
        this.CacheHits = CacheHits;
        this.CoalescedCustomers = CoalescedCustomers;
        this.Fetches = Fetches;
        this.JobReassignments = JobReassignments;
        this.DuplicateJobExecutions = DuplicateJobExecutions;
        this.ObsoletePublications = ObsoletePublications;
        this.LateResponses = LateResponses;
        this.StaleResponses = StaleResponses;
        this.ExpiredRequests = ExpiredRequests;
        this.WastedInstanceTime = WastedInstanceTime;
        this.TotalQueueTime = TotalQueueTime;
        this.QueueEntries = QueueEntries;
        this.ProcessedEvents = ProcessedEvents;
        this.ApiInstanceSeconds = ApiInstanceSeconds;
        this.WorkerInstanceSeconds = WorkerInstanceSeconds;
    }
}

public sealed class InstanceLifetime
{
    public long Id { get; }
    public InstanceRole Role { get; }
    public TimeSpan AllocatedAt { get; }
    public TimeSpan StoppedAt { get; }
    public decimal Cost { get; }

    public InstanceLifetime(long Id, InstanceRole Role, TimeSpan AllocatedAt, TimeSpan StoppedAt, decimal Cost)
    {
        this.Id = Id;
        this.Role = Role;
        this.AllocatedAt = AllocatedAt;
        this.StoppedAt = StoppedAt;
        this.Cost = Cost;
    }
}

public sealed class SimulationResult
{
    public string Scenario { get; }
    public int Seed { get; }
    public TimeSpan Duration { get; }
    public EconomySnapshot Economy { get; }
    public IReadOnlyList<RequestKindMetrics> RequestTypes { get; }
    public SimulationDiagnostics Diagnostics { get; }
    public IReadOnlyList<InstanceLifetime> Instances { get; }
    public IReadOnlyList<SimulationEvent> Trace { get; }

    public SimulationResult(string Scenario, int Seed, TimeSpan Duration, EconomySnapshot Economy,
        IReadOnlyList<RequestKindMetrics> RequestTypes, SimulationDiagnostics Diagnostics,
        IReadOnlyList<InstanceLifetime> Instances, IReadOnlyList<SimulationEvent> Trace)
    {
        this.Scenario = Scenario;
        this.Seed = Seed;
        this.Duration = Duration;
        this.Economy = Economy;
        this.RequestTypes = RequestTypes;
        this.Diagnostics = Diagnostics;
        this.Instances = Instances;
        this.Trace = Trace;
    }
}

public sealed class SimulationRunner
{
    public SimulationResult Run(SimulationScenario scenario, ISystemPolicy policy, SimulationOptions? options = null,
        ISimulationObserver? observer = null)
    {
        var session = new SimulationSession(scenario, policy, options, observer);
        while (session.Step()) { }
        return session.GetResult();
    }
}
