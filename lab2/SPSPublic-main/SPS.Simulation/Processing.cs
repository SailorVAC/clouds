using SPS.Abstractions;

namespace SPS.Simulation;

public sealed partial class SimulationSession
{
    private bool WorkUseful(Work work) => work.IsProbe || (work.Job != null
        ? !work.Job.Done && !work.Job.Customer.ProcessingEnded && work.Generation == work.Job.Generation
        : work.Attempt != null && RpcLive(work.Attempt.Rpc));
    private void RemoveRejectedQueuedWork(Customer customer)
    {
        bool Remove(Work work) => !WorkUseful(work) && (work.Job?.Customer == customer ||
            work.Attempt?.Rpc.Origin == customer || work.Attempt?.Rpc.Fetch?.Customers.Contains(customer) == true);
        foreach (var instance in instances)
        {
            int count = instance.Queue.Count;
            for (int i = 0; i < count; i++)
            {
                var work = instance.Queue.Dequeue();
                if (!Remove(work)) { instance.Queue.Enqueue(work); continue; }
                work.Finished = true;
                Emit("QueuedWorkRemoved", customer, instance, work.Job, "policy rejection");
            }
        }
        int storageCount = storageQueue.Count;
        for (int i = 0; i < storageCount; i++)
        {
            var task = storageQueue.Dequeue();
            if (!Remove(task.Work)) { storageQueue.Enqueue(task); continue; }
            Emit("QueuedWorkRemoved", customer, task.Work.Instance, task.Work.Job, "policy rejection; storage");
            // CPU has already finished for an instance waiting on storage; release its slot.
            FinishWork(task.Work);
        }
    }
    private void EnqueueWork(Work work)
    {
        var instance = work.Instance ?? throw new InvalidOperationException("Direct storage work cannot enter an instance queue.");
        if (instance.Crashed) return;
        if (instance.State != InstanceState.Ready) { RejectWork(work, RequestOutcome.Unavailable); return; }
        if (!WorkUseful(work)) return;
        work.EnqueuedAt = Now;
        if (instance.Active.Count < instance.Spec.Slots) StartWork(work);
        else if (instance.Role == InstanceRole.Worker || instance.Queue.Count >= instance.Spec.QueueLimit)
            RejectWork(work, RequestOutcome.Rejected);
        else instance.Queue.Enqueue(work);
    }
    private void RejectWork(Work work, RequestOutcome outcome)
    {
        rejections++;
        Emit("WorkRejected", work.Attempt?.Rpc.Origin ?? work.Job?.Customer, work.Instance, work.Job, CaptureEventDetails ? outcome.ToString() : null);
        if (work.IsProbe) work.ProbeReply?.Invoke(outcome);
        else if (work.Attempt != null) SendReply(work.Attempt, outcome, null);
        work.Finished = true;
    }
    private void Pump(Instance instance)
    {
        if (completed || instance.Crashed || instance.State is InstanceState.Starting or InstanceState.Stopped) return;
        while (instance.Active.Count < instance.Spec.Slots && instance.Queue.TryDequeue(out var work))
        {
            if (WorkUseful(work)) StartWork(work); else work.Finished = true;
        }
        TryFinishDraining(instance);
    }
    private void StartWork(Work work)
    {
        if (work.Finished) return;
        var instance = work.Instance ?? throw new InvalidOperationException("Direct storage work cannot occupy a compute slot.");
        work.Active = true;
        work.StartedAt = Now;
        instance.Active.Add(work);
        if (work.Job != null && work.Job.ExecutionStarts++ > 0) duplicates++;
        queueTicks += (Now - work.EnqueuedAt).Ticks;
        queueEntries++;
        var customer = work.Attempt?.Rpc.Origin ?? work.Job?.Customer;
        double seconds = work.IsProbe ? .004 : work.Job != null ? work.Job.Customer.ActualWorkerWork : work.Attempt!.Rpc.Origin.Profile.ApiWorkSeconds;
        // Work amount is tied to the customer, so retries do not redraw an easier job.
        seconds *= SlowFactor(instance) / instance.Speed;
        Emit("WorkStarted", customer, instance, work.Job, CaptureEventDetails && work.Job != null ? $"generation={work.Generation}" : null);
        Schedule(After(Seconds(Math.Max(.000001, seconds))), () => ComputeCompleted(work));
    }
    private void ComputeCompleted(Work work)
    {
        if (work.Finished) return;
        if (work.IsProbe) { work.ProbeReply?.Invoke(RequestOutcome.Success); FinishWork(work); return; }
        if (work.Job != null)
        {
            QueueStorage(work, work.Job.Customer.Profile.StorageWorkSeconds, () => PublishJob(work));
            return;
        }
        var rpc = work.Attempt!.Rpc;
        if (rpc.Origin.Info.Kind == RequestKind.Generate)
        {
            if (!rpc.Origin.ProcessingEnded && !registeredJobs.Contains(rpc.Origin.Info.Id))
            {
                if (liveJobs.Count >= scenario.Settings.MaximumJobs)
                { rejections++; Emit("JobQueueRejected", rpc.Origin, work.Instance); SendReply(work.Attempt, RequestOutcome.Rejected, null); FinishWork(work); return; }
                registeredJobs.Add(rpc.Origin.Info.Id);
                var job = new Job { Id = rpc.Origin.Info.Id, Customer = rpc.Origin, QueuedAt = Now };
                jobs.Add(job.Id, job);
                job.LiveNode = liveJobs.AddLast(job);
                Emit("JobRegistered", rpc.Origin);
            }
            SendReply(work.Attempt, RequestOutcome.Success, null);
            FinishWork(work);
            DispatchJobs();
        }
        else QueueReadStorage(work);
    }
    private void QueueReadStorage(Work work)
    {
        var rpc = work.Attempt!.Rpc;
        QueueStorage(work, rpc.Origin.Profile.StorageWorkSeconds, () =>
        {
            var profile = rpc.Origin.Profile;
            var value = new CacheEntryInfo(rpc.Origin.Info.ResourceKey,
                sourceVersions.GetValueOrDefault(rpc.Origin.Info.ResourceKey, 1), profile.SizeBytes, Now, Now);
            if (work.Instance == null) Emit("DirectStorageReadCompleted", rpc.Origin, detail: CaptureEventDetails ? $"rpc={rpc.Id}; version={value.Version}; sourceAt={Now.TotalSeconds:F6}" : null);
            SendReply(work.Attempt, RequestOutcome.Success, value);
            FinishWork(work);
        });
    }
    private void QueueStorage(Work work, double seconds, Action complete)
    {
        if (storageQueue.Count >= scenario.Settings.StorageQueueLimit)
        {
            if (work.Attempt != null) SendReply(work.Attempt, RequestOutcome.Rejected, null);
            rejections++;
            FinishWork(work);
            return;
        }
        storageQueue.Enqueue(new() { Work = work, Seconds = seconds, Complete = complete, EnqueuedAt = Now });
        if (work.Instance == null) Emit("DirectStorageReadQueued", work.Attempt!.Rpc.Origin);
        PumpStorage();
    }
    private void PumpStorage()
    {
        if (completed) return;
        while (storageActive < scenario.Settings.StorageSlots && storageQueue.TryDequeue(out var task))
        {
            if (task.Work.Finished) continue;
            if (task.Work.Job != null ? task.Work.Job.Customer.ProcessingEnded : !WorkUseful(task.Work)) { FinishWork(task.Work); continue; }
            queueTicks += (Now - task.EnqueuedAt).Ticks;
            queueEntries++;
            storageActive++;
            if (task.Work.Instance == null) Emit("DirectStorageReadStarted", task.Work.Attempt!.Rpc.Origin);
            double factor = StorageSlowFactor();
            Schedule(After(Seconds(task.Seconds * factor)), () =>
            {
                storageActive--;
                if (!task.Work.Finished) task.Complete();
                PumpStorage();
            });
        }
    }
    private void FinishWork(Work work)
    {
        if (work.Finished) return;
        work.Finished = true;
        if (work.Active)
        {
            TimeSpan usefulUntil = Now;
            if (work.Job != null) usefulUntil = work.Job.Customer.TerminatedAt ?? Now;
            else if (work.Attempt?.Rpc.Fetch is { } fetch)
                usefulUntil = fetch.Customers.Select(c => c.TerminatedAt ?? Now).DefaultIfEmpty(Now).Max();
            else if (work.Attempt != null) usefulUntil = work.Attempt.Rpc.Origin.TerminatedAt ?? Now;
            if (work.ObsoleteAt is { } obsolete && obsolete < usefulUntil) usefulUntil = obsolete;
            if (usefulUntil < Now) wastedTicks += (Now - (usefulUntil > work.StartedAt ? usefulUntil : work.StartedAt)).Ticks;
            work.Instance!.Active.Remove(work);
            Emit("WorkFinished", work.Attempt?.Rpc.Origin ?? work.Job?.Customer, work.Instance, work.Job, CaptureEventDetails && work.Job != null ? $"generation={work.Generation}" : null);
        }
        if (work.Instance != null) Pump(work.Instance);
    }
}




