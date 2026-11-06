using System.Text.Json;
using System.Text.Json.Serialization;
using SPS.Abstractions;

namespace SPS.Telemetry;

public sealed class RunMetadata
{
    public int SchemaVersion { get; }
    public string Scenario { get; }
    public int Seed { get; }
    public string Policy { get; }
    public string EngineVersion { get; }
    public TimeSpan GenerationDuration { get; }
    public TimeSpan FinalTime { get; }
    public SystemSettings Settings { get; }

    public RunMetadata(int SchemaVersion, string Scenario, int Seed, string Policy, string EngineVersion,
        TimeSpan GenerationDuration, TimeSpan FinalTime, SystemSettings Settings)
    {
        this.SchemaVersion = SchemaVersion;
        this.Scenario = Scenario;
        this.Seed = Seed;
        this.Policy = Policy;
        this.EngineVersion = EngineVersion;
        this.GenerationDuration = GenerationDuration;
        this.FinalTime = FinalTime;
        this.Settings = Settings;
    }
}

public sealed class SimulationEvent
{
    public long Sequence { get; }
    public TimeSpan Time { get; }
    public string Kind { get; }
    public long? RequestId { get; }
    public long? InstanceId { get; }
    public long? JobId { get; }
    public string? Detail { get; }

    public SimulationEvent(long Sequence, TimeSpan Time, string Kind, long? RequestId = null, long? InstanceId = null,
        long? JobId = null, string? Detail = null)
    {
        this.Sequence = Sequence;
        this.Time = Time;
        this.Kind = Kind;
        this.RequestId = RequestId;
        this.InstanceId = InstanceId;
        this.JobId = JobId;
        this.Detail = Detail;
    }
}

public sealed class InstanceSnapshot
{
    public long Id { get; }
    public InstanceRole Role { get; }
    public InstanceState State { get; }
    public bool ActuallyCrashed { get; }
    public bool NetworkIsolated { get; }
    public EndpointMode TrafficMode { get; }
    public int BusySlots { get; }
    public int Waiting { get; }
    public int CapacitySlots { get; }
    public double Speed { get; }
    public decimal PricePerSecond { get; }
    public TimeSpan AllocatedAt { get; }
    public TimeSpan? StoppedAt { get; }
    public decimal Cost { get; }

    public InstanceSnapshot(long Id, InstanceRole Role, InstanceState State, bool ActuallyCrashed,
        bool NetworkIsolated, EndpointMode TrafficMode, int BusySlots, int Waiting, int CapacitySlots, double Speed,
        decimal PricePerSecond, TimeSpan AllocatedAt, TimeSpan? StoppedAt, decimal Cost)
    {
        this.Id = Id;
        this.Role = Role;
        this.State = State;
        this.ActuallyCrashed = ActuallyCrashed;
        this.NetworkIsolated = NetworkIsolated;
        this.TrafficMode = TrafficMode;
        this.BusySlots = BusySlots;
        this.Waiting = Waiting;
        this.CapacitySlots = CapacitySlots;
        this.Speed = Speed;
        this.PricePerSecond = PricePerSecond;
        this.AllocatedAt = AllocatedAt;
        this.StoppedAt = StoppedAt;
        this.Cost = Cost;
    }
}

public sealed class QueueSnapshot
{
    public string Name { get; }
    public int Waiting { get; }
    public int Active { get; }

    public QueueSnapshot(string Name, int Waiting, int Active)
    {
        this.Name = Name;
        this.Waiting = Waiting;
        this.Active = Active;
    }
}

public sealed class CacheSnapshot
{
    public int Entries { get; }
    public long UsedBytes { get; }
    public long CapacityBytes { get; }
    public int PendingFetches { get; }

    public CacheSnapshot(int Entries, long UsedBytes, long CapacityBytes, int PendingFetches)
    {
        this.Entries = Entries;
        this.UsedBytes = UsedBytes;
        this.CapacityBytes = CapacityBytes;
        this.PendingFetches = PendingFetches;
    }
}

public sealed class EconomySnapshot
{
    public decimal Revenue { get; }
    public decimal ApiCost { get; }
    public decimal WorkerCost { get; }

    public EconomySnapshot(decimal Revenue, decimal ApiCost, decimal WorkerCost)
    {
        this.Revenue = Revenue;
        this.ApiCost = ApiCost;
        this.WorkerCost = WorkerCost;
    }

    public decimal Profit => Revenue - ApiCost - WorkerCost;
}

public sealed class SimulationSnapshot
{
    public TimeSpan Time { get; }
    public IReadOnlyList<InstanceSnapshot> Instances { get; }
    public IReadOnlyList<QueueSnapshot> Queues { get; }
    public CacheSnapshot Cache { get; }
    public EconomySnapshot Economy { get; }
    public long Requests { get; }
    public long Successful { get; }
    public long Failed { get; }

    public SimulationSnapshot(TimeSpan Time, IReadOnlyList<InstanceSnapshot> Instances,
        IReadOnlyList<QueueSnapshot> Queues, CacheSnapshot Cache, EconomySnapshot Economy, long Requests,
        long Successful, long Failed)
    {
        this.Time = Time;
        this.Instances = Instances;
        this.Queues = Queues;
        this.Cache = Cache;
        this.Economy = Economy;
        this.Requests = Requests;
        this.Successful = Successful;
        this.Failed = Failed;
    }
}

public interface ISimulationObserver
{
    void OnStarted(RunMetadata metadata);
    void OnEvent(SimulationEvent simulationEvent);
    void OnSnapshot(SimulationSnapshot snapshot);
}

// Recording is synchronous and optional. It never advances simulation time.
public sealed class TraceRecorder : ISimulationObserver, IDisposable
{
    private readonly StreamWriter writer;
    private long recordedBytes;
    private const long MaximumRecordingBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public TraceRecorder(string path)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SPS.sln")) &&
            !File.Exists(Path.Combine(root.FullName, "SPS.Student.sln"))) root = root.Parent;
        if (root == null) throw new IOException("Cannot locate the SPS solution directory for safe recording.");
        string outputRoot = Path.Combine(root.FullName, "output");
        string absolutePath = Path.GetFullPath(path, root.FullName);
        string relative = Path.GetRelativePath(outputRoot, absolutePath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) ||
            !string.Equals(Path.GetExtension(absolutePath), ".jsonl", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Recordings must be .jsonl files inside the SPS/output directory.");
        // Refuse junctions/symlinks in the output path rather than following them outside SPS.
        string current = root.FullName;
        foreach (string segment in Path.GetRelativePath(root.FullName, Path.GetDirectoryName(absolutePath)!)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recording directories must not be symbolic links or junctions.");
            Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recording directories must not be symbolic links or junctions.");
        }
        writer = new StreamWriter(new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(false));
    }
    private void Write<T>(string type, T data)
    {
        string line = JsonSerializer.Serialize(new Envelope<T>(type, data), Options);
        long bytes = System.Text.Encoding.UTF8.GetByteCount(line) + System.Text.Encoding.UTF8.GetByteCount(Environment.NewLine);
        if (bytes > MaximumRecordingBytes - recordedBytes) throw new IOException("Recording exceeds the 256 MiB limit.");
        writer.WriteLine(line);
        recordedBytes += bytes;
    }
    public void OnStarted(RunMetadata metadata) => Write("metadata", metadata);
    public void OnEvent(SimulationEvent simulationEvent) => Write("event", simulationEvent);
    public void OnSnapshot(SimulationSnapshot snapshot) => Write("snapshot", snapshot);
    public void Dispose() => writer.Dispose();
    private sealed class Envelope<T>
    {
        public string Type { get; }
        public T Data { get; }

        public Envelope(string Type, T Data)
        {
            this.Type = Type;
            this.Data = Data;
        }
    }
}


