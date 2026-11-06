using SPS.Abstractions;

namespace SPS.Simulation;

// Visible request attributes may vary without changing hidden customer patience or physical work.
public sealed class RequestVariant
{
    public decimal Revenue { get; }
    public double MaxDataAgeSeconds { get; }
    public double Weight { get; }

    public RequestVariant(decimal Revenue, double MaxDataAgeSeconds, double Weight = 1)
    {
        this.Revenue = Revenue;
        this.MaxDataAgeSeconds = MaxDataAgeSeconds;
        this.Weight = Weight;
    }
}

public sealed class RequestProfile
{
    public RequestKind Kind { get; }
    public decimal Revenue { get; }
    public double MinPatienceSeconds { get; }
    public double MaxPatienceSeconds { get; }
    public double ApiWorkSeconds { get; }
    public double StorageWorkSeconds { get; }
    public double MinWorkerSeconds { get; }
    public double MaxWorkerSeconds { get; }
    public double MaxDataAgeSeconds { get; }
    public long SizeBytes { get; }
    // An empty list preserves the original fixed price and freshness requirement.
    public IReadOnlyList<RequestVariant> Variants { get; init; } = Array.Empty<RequestVariant>();

    public RequestProfile(RequestKind Kind, decimal Revenue, double MinPatienceSeconds, double MaxPatienceSeconds,
        double ApiWorkSeconds, double StorageWorkSeconds, double MinWorkerSeconds, double MaxWorkerSeconds,
        double MaxDataAgeSeconds, long SizeBytes)
    {
        this.Kind = Kind;
        this.Revenue = Revenue;
        this.MinPatienceSeconds = MinPatienceSeconds;
        this.MaxPatienceSeconds = MaxPatienceSeconds;
        this.ApiWorkSeconds = ApiWorkSeconds;
        this.StorageWorkSeconds = StorageWorkSeconds;
        this.MinWorkerSeconds = MinWorkerSeconds;
        this.MaxWorkerSeconds = MaxWorkerSeconds;
        this.MaxDataAgeSeconds = MaxDataAgeSeconds;
        this.SizeBytes = SizeBytes;
    }
}

public sealed class WorkloadPhase
{
    public double StartSeconds { get; }
    public double RequestsPerSecond { get; }
    public double JobFraction { get; }
    public double PreviewFraction { get; }
    public double HotKeyFraction { get; }
    public int KeyOffset { get; }
    public int HotKeyCount { get; }

    public WorkloadPhase(double StartSeconds, double RequestsPerSecond, double JobFraction, double PreviewFraction,
        double HotKeyFraction, int KeyOffset = 0, int HotKeyCount = 4)
    {
        this.StartSeconds = StartSeconds;
        this.RequestsPerSecond = RequestsPerSecond;
        this.JobFraction = JobFraction;
        this.PreviewFraction = PreviewFraction;
        this.HotKeyFraction = HotKeyFraction;
        this.KeyOffset = KeyOffset;
        this.HotKeyCount = HotKeyCount;
    }
}

public enum FaultKind { Crash, NetworkIsolation, HeartbeatIsolation, Slowdown, NetworkLoss, StorageSlowdown }

public sealed class FaultInterval
{
    public double StartSeconds { get; }
    public double EndSeconds { get; }
    public FaultKind Kind { get; }
    public InstanceRole Role { get; }
    public int RoleOrdinal { get; }
    public double Factor { get; }

    public FaultInterval(double StartSeconds, double EndSeconds, FaultKind Kind, InstanceRole Role = InstanceRole.Api,
        int RoleOrdinal = 0, double Factor = 4)
    {
        this.StartSeconds = StartSeconds;
        this.EndSeconds = EndSeconds;
        this.Kind = Kind;
        this.Role = Role;
        this.RoleOrdinal = RoleOrdinal;
        this.Factor = Factor;
    }
}

public sealed class SimulationScenario
{
    public string Name { get; }
    public TimeSpan GenerationDuration { get; }
    public SystemSettings Settings { get; }
    public IReadOnlyList<RequestProfile> Profiles { get; }
    public IReadOnlyList<WorkloadPhase> Phases { get; }
    public IReadOnlyList<FaultInterval> Faults { get; }

    public SimulationScenario(string Name, TimeSpan GenerationDuration, SystemSettings Settings,
        IReadOnlyList<RequestProfile> Profiles, IReadOnlyList<WorkloadPhase> Phases,
        IReadOnlyList<FaultInterval> Faults)
    {
        this.Name = Name;
        this.GenerationDuration = GenerationDuration;
        this.Settings = Settings;
        this.Profiles = Profiles;
        this.Phases = Phases;
        this.Faults = Faults;
    }

    public int ResourceKeyCount { get; init; } = 100;
    public TimeSpan SourceUpdateInterval { get; init; } = TimeSpan.FromSeconds(1);
    public double NetworkMinSeconds { get; init; } = .004;
    public double NetworkMaxSeconds { get; init; } = .025;
    public double ResponseLossProbability { get; init; } = .005;
    public TimeSpan FinalTime => GenerationDuration + TimeSpan.FromSeconds(Profiles.Max(p => p.MaxPatienceSeconds));
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || GenerationDuration <= TimeSpan.Zero || ResourceKeyCount < 1 ||
            SourceUpdateInterval <= TimeSpan.Zero || Phases.Count == 0 || Phases[0].StartSeconds != 0 ||
            Profiles.Count != 3 || Profiles.Select(p => p.Kind).Distinct().Count() != 3)
            throw new ArgumentException("Invalid scenario identity, workload, or request profiles.");
        if (!double.IsFinite(NetworkMinSeconds) || !double.IsFinite(NetworkMaxSeconds) || NetworkMinSeconds < 0 ||
            NetworkMaxSeconds < NetworkMinSeconds || !double.IsFinite(ResponseLossProbability) || ResponseLossProbability is < 0 or > 1)
            throw new ArgumentException("Invalid network settings.");
        foreach (var p in Profiles)
        {
            if (!Enum.IsDefined(p.Kind) || p.Revenue < 0 || !double.IsFinite(p.MinPatienceSeconds) || !double.IsFinite(p.MaxPatienceSeconds) ||
                p.MinPatienceSeconds <= 0 || p.MaxPatienceSeconds < p.MinPatienceSeconds || !double.IsFinite(p.ApiWorkSeconds) ||
                p.ApiWorkSeconds < 0 || p.Kind != RequestKind.Metadata && p.ApiWorkSeconds == 0 ||
                !double.IsFinite(p.StorageWorkSeconds) || p.StorageWorkSeconds <= 0 ||
                !double.IsFinite(p.MinWorkerSeconds) || !double.IsFinite(p.MaxWorkerSeconds) ||
                p.MinWorkerSeconds <= 0 || p.MaxWorkerSeconds < p.MinWorkerSeconds ||
                !double.IsFinite(p.MaxDataAgeSeconds) || p.MaxDataAgeSeconds <= 0 ||
                p.MaxDataAgeSeconds > TimeSpan.MaxValue.TotalSeconds || p.SizeBytes <= 0)
                throw new ArgumentException("Invalid request profile.");
            if (p.Variants == null || p.Variants.Any(v => v == null || v.Revenue < 0 ||
                !double.IsFinite(v.MaxDataAgeSeconds) || v.MaxDataAgeSeconds <= 0 ||
                v.MaxDataAgeSeconds > TimeSpan.MaxValue.TotalSeconds || !double.IsFinite(v.Weight) || v.Weight <= 0) ||
                !double.IsFinite(p.Variants.Sum(v => v.Weight)))
                throw new ArgumentException("Invalid request variants: prices must be nonnegative, freshness and weights finite and positive.");
        }
        double previous = -1;
        foreach (var p in Phases)
        {
            if (!double.IsFinite(p.StartSeconds) || p.StartSeconds <= previous || p.StartSeconds >= GenerationDuration.TotalSeconds ||
                !double.IsFinite(p.RequestsPerSecond) || p.RequestsPerSecond <= 0 || p.RequestsPerSecond > 100_000 ||
                p.JobFraction is < 0 or > 1 || p.PreviewFraction is < 0 or > 1 || p.JobFraction + p.PreviewFraction > 1 ||
                !double.IsFinite(p.JobFraction) || !double.IsFinite(p.PreviewFraction) ||
                !double.IsFinite(p.HotKeyFraction) || p.HotKeyFraction is < 0 or > 1 ||
                p.HotKeyCount < 1 || p.HotKeyCount > ResourceKeyCount || p.KeyOffset < 0)
                throw new ArgumentException("Invalid workload phase.");
            previous = p.StartSeconds;
        }
        foreach (var f in Faults)
            if (!double.IsFinite(f.StartSeconds) || !double.IsFinite(f.EndSeconds) || f.StartSeconds < 0 || f.EndSeconds <= f.StartSeconds ||
                !double.IsFinite(f.Factor) || f.Factor < 1 || f.RoleOrdinal < 0 || !Enum.IsDefined(f.Kind) || !Enum.IsDefined(f.Role))
                throw new ArgumentException("Invalid fault interval.");
        var s = Settings;
        if (s.MaximumApis < 2 || s.MaximumWorkers < 2 || s.ComputeUnitLimit <= 0 || s.CacheCapacityBytes <= 0 ||
            s.MaximumJobs <= 0 || s.StorageSlots <= 0 || s.StorageQueueLimit <= 0 || s.ReviewInterval <= TimeSpan.Zero ||
            s.CapacityReviewInterval <= TimeSpan.Zero || s.HeartbeatInterval <= TimeSpan.Zero || s.ProbeTimeout <= TimeSpan.Zero)
            throw new ArgumentException("Invalid system limits.");
        foreach (var p in new[] { s.Api, s.Worker })
            if (p.Slots <= 0 || p.QueueLimit < 0 || !double.IsFinite(p.Speed) || p.Speed <= 0 || p.PricePerSecond < 0 ||
                p.ComputeUnits <= 0 || p.StartupDelay <= TimeSpan.Zero || p.DrainGrace <= TimeSpan.Zero)
                throw new ArgumentException("Invalid instance specification.");
        if (2 * (s.Api.ComputeUnits + s.Worker.ComputeUnits) > s.ComputeUnitLimit)
            throw new ArgumentException("Initial two APIs and two workers exceed the resource limit.");
    }
}

public sealed class SimulationOptions
{
    public int Seed { get; }
    public int TraceRequestCount { get; }
    public long MaximumEventCount { get; }
    public TimeSpan? SnapshotInterval { get; }

    public SimulationOptions(int Seed = 42, int TraceRequestCount = 0, long MaximumEventCount = 10_000_000,
        TimeSpan? SnapshotInterval = null)
    {
        this.Seed = Seed;
        this.TraceRequestCount = TraceRequestCount;
        this.MaximumEventCount = MaximumEventCount;
        this.SnapshotInterval = SnapshotInterval;
    }
}
