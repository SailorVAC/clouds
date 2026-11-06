namespace SPS.Abstractions;

public sealed class InstanceSpecification
{
    public int Slots { get; }
    public int QueueLimit { get; }
    public double Speed { get; }
    public decimal PricePerSecond { get; }
    public int ComputeUnits { get; }
    public TimeSpan StartupDelay { get; }
    public TimeSpan DrainGrace { get; }

    public InstanceSpecification(int Slots, int QueueLimit, double Speed, decimal PricePerSecond, int ComputeUnits,
        TimeSpan StartupDelay, TimeSpan DrainGrace)
    {
        this.Slots = Slots;
        this.QueueLimit = QueueLimit;
        this.Speed = Speed;
        this.PricePerSecond = PricePerSecond;
        this.ComputeUnits = ComputeUnits;
        this.StartupDelay = StartupDelay;
        this.DrainGrace = DrainGrace;
    }
}

public sealed class SystemSettings
{
    public InstanceSpecification Api { get; }
    public InstanceSpecification Worker { get; }
    public int MaximumApis { get; }
    public int MaximumWorkers { get; }
    public int ComputeUnitLimit { get; }
    public long CacheCapacityBytes { get; }
    public int MaximumJobs { get; }
    public int StorageSlots { get; }
    public int StorageQueueLimit { get; }
    public TimeSpan ReviewInterval { get; }
    public TimeSpan CapacityReviewInterval { get; }
    public TimeSpan HeartbeatInterval { get; }
    public TimeSpan ProbeTimeout { get; }

    public SystemSettings(InstanceSpecification Api, InstanceSpecification Worker, int MaximumApis,
        int MaximumWorkers, int ComputeUnitLimit, long CacheCapacityBytes, int MaximumJobs, int StorageSlots,
        int StorageQueueLimit, TimeSpan ReviewInterval, TimeSpan CapacityReviewInterval, TimeSpan HeartbeatInterval,
        TimeSpan ProbeTimeout)
    {
        this.Api = Api;
        this.Worker = Worker;
        this.MaximumApis = MaximumApis;
        this.MaximumWorkers = MaximumWorkers;
        this.ComputeUnitLimit = ComputeUnitLimit;
        this.CacheCapacityBytes = CacheCapacityBytes;
        this.MaximumJobs = MaximumJobs;
        this.StorageSlots = StorageSlots;
        this.StorageQueueLimit = StorageQueueLimit;
        this.ReviewInterval = ReviewInterval;
        this.CapacityReviewInterval = CapacityReviewInterval;
        this.HeartbeatInterval = HeartbeatInterval;
        this.ProbeTimeout = ProbeTimeout;
    }
}

// PendingCalls counts API-bound logical RPCs; direct Metadata storage calls are excluded.
public sealed class CapacityContext
{
    public TimeSpan Now { get; }
    public SystemSettings Settings { get; }
    public IReadOnlyList<EndpointInfo> Instances { get; }
    public int PendingCalls { get; }
    public IReadOnlyList<JobInfo> QueuedJobs { get; }
    public decimal ApiCost { get; }
    public decimal WorkerCost { get; }

    public CapacityContext(TimeSpan Now, SystemSettings Settings, IReadOnlyList<EndpointInfo> Instances,
        int PendingCalls, IReadOnlyList<JobInfo> QueuedJobs, decimal ApiCost, decimal WorkerCost)
    {
        this.Now = Now;
        this.Settings = Settings;
        this.Instances = Instances;
        this.PendingCalls = PendingCalls;
        this.QueuedJobs = QueuedJobs;
        this.ApiCost = ApiCost;
        this.WorkerCost = WorkerCost;
    }
}

public sealed class CapacityPlan
{
    public int DesiredApiCount { get; }
    public int DesiredWorkerCount { get; }
    public IReadOnlyList<long>? RetireInstanceIds { get; }

    public CapacityPlan(int DesiredApiCount, int DesiredWorkerCount, IReadOnlyList<long>? RetireInstanceIds = null)
    {
        this.DesiredApiCount = DesiredApiCount;
        this.DesiredWorkerCount = DesiredWorkerCount;
        this.RetireInstanceIds = RetireInstanceIds;
    }
}
