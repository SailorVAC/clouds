using SPS.Abstractions;

namespace SPS.Simulation;

public sealed partial class SimulationSession
{
    private sealed class Customer
    {
        public required RequestInfo Info;
        public required RequestProfile Profile;
        public double ActualWorkerWork;
        // Economic state is private and must never control service work or policy callbacks.
        public TimeSpan PatienceDeadline;
        public bool Arrived;
        public bool Terminal;
        public bool Success;
        public TimeSpan? TerminatedAt;
        public TimeSpan? Latency;
        public bool ProcessingEnded;
        public TimeSpan? ProcessingDeadline;
    }
    private sealed class Instance
    {
        public long Id;
        public int Ordinal;
        public InstanceRole Role;
        public InstanceState State;
        public required InstanceSpecification Spec;
        public double Speed;
        public TimeSpan AllocatedAt;
        public TimeSpan? StoppedAt;
        public bool Crashed;
        public EndpointMode Mode;
        public int Outstanding;
        // Current logical assignments include dispatches still in network transit.
        public int AssignedJobs;
        public bool ProbeOutstanding;
        public TimeSpan? LastHeartbeat;
        public readonly List<CallResultInfo> Results = new();
        public IReadOnlyList<CallResultInfo>? ResultsSnapshot;
        public readonly Queue<Work> Queue = new();
        public readonly HashSet<Work> Active = new();
    }
    private sealed class Fetch
    {
        public long Id;
        public required string Key;
        public required Customer Origin;
        public TimeSpan StartedAt;
        public readonly List<Customer> Customers = new();
        public bool Done;
    }
    private sealed class Rpc
    {
        public long Id;
        public required Customer Origin;
        public Fetch? Fetch;
        public int Attempt;
        public bool Done;
        public bool Waiting;
    }
    private sealed class Attempt
    {
        public required Rpc Rpc;
        // Null targets the fixed shared storage directly, without an API instance.
        public Instance? Instance;
        public int Number;
        public TimeSpan StartedAt;
        public bool Observed;
    }
    private sealed class Work
    {
        public Instance? Instance;
        public Attempt? Attempt;
        public Job? Job;
        public int Generation;
        public bool IsProbe;
        public Action<RequestOutcome>? ProbeReply;
        public TimeSpan? ObsoleteAt;
        public bool Active;
        public bool Finished;
        public TimeSpan EnqueuedAt;
        public TimeSpan StartedAt;
    }
    private sealed class Job
    {
        public long Id;
        public required Customer Customer;
        public int Generation;
        public int ExecutionStarts;
        public bool Done;
        public bool Queued = true;
        public TimeSpan QueuedAt;
        public Instance? Worker;
        public LinkedListNode<Job>? LiveNode;
        public TimeSpan? AssignedAt;
        public TimeSpan? LastHeartbeat;
    }
    private sealed class StorageTask
    {
        public required Work Work;
        public required Action Complete;
        public double Seconds;
        public TimeSpan EnqueuedAt;
    }
}



