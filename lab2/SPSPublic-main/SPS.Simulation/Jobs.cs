using SPS.Abstractions;

namespace SPS.Simulation;

public sealed partial class SimulationSession
{
    private void DispatchJobs()
    {
        if (completed) return;
        for (int guard = 0; guard < scenario.Settings.MaximumJobs; guard++)
        {
            var workers = instances.Where(i => i.Role == InstanceRole.Worker && i.State == InstanceState.Ready &&
                i.Mode == EndpointMode.AllowTraffic && i.AssignedJobs < i.Spec.Slots).ToArray();
            // Most reviews have no free worker; do not construct a scheduling context in that case.
            if (workers.Length == 0) return;
            var pending = liveJobs.Where(j => j.Queued && j.QueuedAt <= Now && !j.Customer.ProcessingEnded).OrderBy(j => j.Id).ToArray();
            if (pending.Length == 0) return;
            var decision = Decide("SelectJobAssignment", () => policy.SelectJobAssignment(new(Now, ReadOnly(pending.Select(JobView)), ReadOnly(workers.Select(Endpoint))), policyRandom));
            if (decision == null) return;
            if (!Enum.IsDefined(decision.Action)) Invalid("SelectJobAssignment", $"job {decision.JobId}: invalid action");
            var job = pending.SingleOrDefault(j => j.Id == decision.JobId);
            if (job == null) Invalid("SelectJobAssignment", $"job {decision.JobId} is not eligible");
            if (decision.Action == AssignmentAction.Reject)
            {
                if (decision.WorkerId != null) Invalid("SelectJobAssignment", $"job {decision.JobId}: rejection cannot include a worker");
                RejectByPolicy(job.Customer, "SelectJobAssignment");
                continue;
            }
            var worker = workers.SingleOrDefault(i => i.Id == decision.WorkerId);
            if (worker == null) Invalid("SelectJobAssignment", $"job {decision.JobId} / worker {decision.WorkerId} is not eligible");
            job!.Queued = false;
            job.Worker = worker!;
            worker!.AssignedJobs++;
            job.AssignedAt = Now;
            job.LastHeartbeat = null;
            if (job.Generation == 0) job.Generation = 1;
            queueTicks += (Now - job.QueuedAt).Ticks;
            queueEntries++;
            var work = new Work { Instance = worker, Job = job, Generation = job.Generation, EnqueuedAt = Now };
            Notify(new(ObservationKind.JobAssigned, Now, RequestId: job.Customer.Info.Id, EndpointId: worker.Id, JobId: job.Id));
            Emit("JobAssigned", job.Customer, worker, job, CaptureEventDetails ? $"generation={job.Generation}" : null);
            Schedule(After(NetworkDelay()), () =>
            {
                if (!job.Done && !job.Queued && job.Generation == work.Generation && NetworkPasses(worker)) EnqueueWork(work);
            });
        }
    }
    private static void ReleaseAssignment(Job job)
    {
        // Old physical attempts can continue after this logical reservation is released.
        if (job.Worker == null) return;
        job.Worker.AssignedJobs--;
        job.Worker = null;
    }
    private void CompleteJob(Job job)
    {
        if (job.Done) return;
        job.Done = true;
        ReleaseAssignment(job);
        liveJobs.Remove(job.LiveNode!);
        job.LiveNode = null;
    }
    private void Requeue(Job job, TimeSpan delay)
    {
        foreach (var work in instances.SelectMany(i => i.Active).Where(w => w.Job == job)) work.ObsoleteAt ??= Now;
        job.Generation++;
        job.Queued = true;
        ReleaseAssignment(job);
        job.AssignedAt = job.LastHeartbeat = null;
        job.QueuedAt = After(delay);
        reassignments++;
        Notify(new(ObservationKind.JobRequeued, Now, RequestId: job.Customer.Info.Id, JobId: job.Id));
        Emit("JobRequeued", job.Customer, job: job, detail: CaptureEventDetails ? $"generation={job.Generation}" : null);
        Schedule(job.QueuedAt, DispatchJobs, 35);
    }
    private void PublishJob(Work work)
    {
        var job = work.Job!;
        if (job.Generation != work.Generation || job.Done || job.Customer.ProcessingEnded)
        {
            obsoletePublications++;
            Emit("ObsoletePublicationRejected", job.Customer, work.Instance, job, CaptureEventDetails ? $"generation={work.Generation}" : null);
        }
        else
        {
            // Attempt output keys are immutable; only this current generation can publish the logical pointer.
            CompleteJob(job);
            Notify(new(ObservationKind.JobCompleted, Now, RequestId: job.Customer.Info.Id, EndpointId: work.Instance!.Id, JobId: job.Id));
            Emit("JobPublished", job.Customer, work.Instance, job, CaptureEventDetails ? $"jobs/{job.Id}/attempt-{work.Generation}" : null);
            NotifyJob(job);
        }
        FinishWork(work);
        DispatchJobs();
    }
    private void NotifyJob(Job job)
    {
        if (job.Customer.ProcessingEnded || completed) return;
        // Notification is sent by the durable service, so a worker crash after publication cannot lose the result.
        if (networkRandom.NextDouble() >= scenario.ResponseLossProbability)
            Schedule(After(NetworkDelay()), () => Succeed(job.Customer));
        else Schedule(After(scenario.Settings.ReviewInterval), () => NotifyJob(job));
    }
}

