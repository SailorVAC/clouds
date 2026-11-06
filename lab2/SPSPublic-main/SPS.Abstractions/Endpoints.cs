namespace SPS.Abstractions;

public enum InstanceRole { Api, Worker }

public enum InstanceState { Starting, Ready, Draining, Stopped }

public enum EndpointMode { AllowTraffic, BlockTraffic, Probe }

public sealed class CallResultInfo
{
    public TimeSpan Time { get; }
    public RequestOutcome Outcome { get; }
    public TimeSpan Elapsed { get; }
    public bool IsProbe { get; }

    public CallResultInfo(TimeSpan Time, RequestOutcome Outcome, TimeSpan Elapsed, bool IsProbe)
    {
        this.Time = Time;
        this.Outcome = Outcome;
        this.Elapsed = Elapsed;
        this.IsProbe = IsProbe;
    }
}

// OutstandingCalls counts caller-visible calls, not hidden server work after a timeout.
public sealed class EndpointInfo
{
    public long Id { get; }
    public InstanceRole Role { get; }
    public InstanceState State { get; }
    public double Speed { get; }
    public int Slots { get; }
    public int OutstandingCalls { get; }
    public int AssignedJobs { get; }
    public EndpointMode Mode { get; }
    public TimeSpan? LastHeartbeat { get; }
    public bool ProbeOutstanding { get; }

    public EndpointInfo(long Id, InstanceRole Role, InstanceState State, double Speed, int Slots,
        int OutstandingCalls, int AssignedJobs, EndpointMode Mode, TimeSpan? LastHeartbeat, bool ProbeOutstanding)
    {
        this.Id = Id;
        this.Role = Role;
        this.State = State;
        this.Speed = Speed;
        this.Slots = Slots;
        this.OutstandingCalls = OutstandingCalls;
        this.AssignedJobs = AssignedJobs;
        this.Mode = Mode;
        this.LastHeartbeat = LastHeartbeat;
        this.ProbeOutstanding = ProbeOutstanding;
    }
}

public sealed class RoutingContext
{
    public TimeSpan Now { get; }
    public RequestInfo Request { get; }
    public int Attempt { get; }
    public IReadOnlyList<EndpointInfo> Endpoints { get; }

    public RoutingContext(TimeSpan Now, RequestInfo Request, int Attempt, IReadOnlyList<EndpointInfo> Endpoints)
    {
        this.Now = Now;
        this.Request = Request;
        this.Attempt = Attempt;
        this.Endpoints = Endpoints;
    }
}

public sealed class EndpointContext
{
    public TimeSpan Now { get; }
    public EndpointInfo Endpoint { get; }
    public IReadOnlyList<CallResultInfo> RecentResults { get; }

    public EndpointContext(TimeSpan Now, EndpointInfo Endpoint, IReadOnlyList<CallResultInfo> RecentResults)
    {
        this.Now = Now;
        this.Endpoint = Endpoint;
        this.RecentResults = RecentResults;
    }
}

public sealed class EndpointDecision
{
    public EndpointMode Mode { get; }

    public EndpointDecision(EndpointMode Mode)
    {
        this.Mode = Mode;
    }

    public static EndpointDecision Allow() => new(EndpointMode.AllowTraffic);
    public static EndpointDecision Block() => new(EndpointMode.BlockTraffic);
    public static EndpointDecision Probe() => new(EndpointMode.Probe);
}

// Null decisions mean wait. Rejection closes the context's request without selecting an API.
public sealed class EndpointSelectionDecision
{
    public AssignmentAction Action { get; }
    public long? EndpointId { get; }

    public EndpointSelectionDecision(AssignmentAction Action, long? EndpointId = null)
    {
        this.Action = Action;
        this.EndpointId = EndpointId;
    }

    public static EndpointSelectionDecision Assign(long endpointId) => new(AssignmentAction.Assign, endpointId);
    public static EndpointSelectionDecision Reject() => new(AssignmentAction.Reject);
}
