namespace SPS.Abstractions;

public sealed class JobInfo
{
    public long Id { get; }
    public RequestInfo Request { get; }
    public TimeSpan QueuedAt { get; }
    public int Attempt { get; }
    public long? WorkerId { get; }
    public TimeSpan? AssignedAt { get; }
    public TimeSpan? LastHeartbeat { get; }

    public JobInfo(long Id, RequestInfo Request, TimeSpan QueuedAt, int Attempt, long? WorkerId, TimeSpan? AssignedAt,
        TimeSpan? LastHeartbeat)
    {
        this.Id = Id;
        this.Request = Request;
        this.QueuedAt = QueuedAt;
        this.Attempt = Attempt;
        this.WorkerId = WorkerId;
        this.AssignedAt = AssignedAt;
        this.LastHeartbeat = LastHeartbeat;
    }
}

public sealed class JobSchedulingContext
{
    public TimeSpan Now { get; }
    public IReadOnlyList<JobInfo> QueuedJobs { get; }
    public IReadOnlyList<EndpointInfo> AvailableWorkers { get; }

    public JobSchedulingContext(TimeSpan Now, IReadOnlyList<JobInfo> QueuedJobs,
        IReadOnlyList<EndpointInfo> AvailableWorkers)
    {
        this.Now = Now;
        this.QueuedJobs = QueuedJobs;
        this.AvailableWorkers = AvailableWorkers;
    }
}

public sealed class JobRecoveryContext
{
    public TimeSpan Now { get; }
    public JobInfo Job { get; }

    public JobRecoveryContext(TimeSpan Now, JobInfo Job)
    {
        this.Now = Now;
        this.Job = Job;
    }
}

public sealed class JobAssignmentDecision
{
    public AssignmentAction Action { get; }
    public long JobId { get; }
    public long? WorkerId { get; }

    public JobAssignmentDecision(AssignmentAction Action, long JobId, long? WorkerId = null)
    {
        this.Action = Action;
        this.JobId = JobId;
        this.WorkerId = WorkerId;
    }

    public static JobAssignmentDecision Assign(long jobId, long workerId) => new(AssignmentAction.Assign, jobId, workerId);
    public static JobAssignmentDecision Reject(long jobId) => new(AssignmentAction.Reject, jobId);
}

public sealed class JobRecoveryDecision
{
    public bool Requeue { get; }
    public TimeSpan Delay { get; }

    public JobRecoveryDecision(bool Requeue, TimeSpan Delay)
    {
        this.Requeue = Requeue;
        this.Delay = Delay;
    }

    public static JobRecoveryDecision KeepWaiting() => new(false, TimeSpan.Zero);
    public static JobRecoveryDecision RequeueAfter(TimeSpan delay) => new(true, delay);
}
