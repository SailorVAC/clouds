using SPS.Abstractions;

namespace SPS.Simulation;

public sealed partial class SimulationSession
{
    private EndpointInfo Endpoint(Instance instance) => new(instance.Id, instance.Role, instance.State,
        instance.Speed, instance.Spec.Slots, instance.Outstanding,
        instance.AssignedJobs, instance.Mode,
        instance.LastHeartbeat, instance.ProbeOutstanding);
    private JobInfo JobView(Job job) => new(job.Id, job.Customer.Info, job.QueuedAt, job.Generation,
        job.Worker?.Id, job.AssignedAt, job.LastHeartbeat);
    private bool HasFault(Instance instance, FaultKind kind)
    {
        double now = Now.TotalSeconds;
        foreach (var fault in scenario.Faults)
            if (fault.Kind == kind && fault.Role == instance.Role && fault.RoleOrdinal == instance.Ordinal &&
                now >= fault.StartSeconds && now < fault.EndSeconds) return true;
        return false;
    }
    private double SlowFactor(Instance instance)
    {
        double factor = 1, now = Now.TotalSeconds;
        foreach (var fault in scenario.Faults)
            if (fault.Kind == FaultKind.Slowdown && fault.Role == instance.Role && fault.RoleOrdinal == instance.Ordinal &&
                now >= fault.StartSeconds && now < fault.EndSeconds) factor = Math.Max(factor, fault.Factor);
        return factor;
    }
    private double StorageSlowFactor()
    {
        double factor = 1, now = Now.TotalSeconds;
        foreach (var fault in scenario.Faults)
            if (fault.Kind == FaultKind.StorageSlowdown && now >= fault.StartSeconds && now < fault.EndSeconds)
                factor = Math.Max(factor, fault.Factor);
        return factor;
    }
    private TimeSpan NetworkDelay() => Seconds(scenario.NetworkMinSeconds +
        networkRandom.NextDouble() * (scenario.NetworkMaxSeconds - scenario.NetworkMinSeconds));
    private bool NetworkPasses(Instance? instance) => instance == null
        ? networkRandom.NextDouble() >= scenario.ResponseLossProbability
        : !instance.Crashed && !HasFault(instance, FaultKind.NetworkIsolation) &&
            networkRandom.NextDouble() >= (HasFault(instance, FaultKind.NetworkLoss) ? .45 : scenario.ResponseLossProbability);
    private void ApplyFaultChange(FaultInterval fault)
    {
        Emit("FaultBoundary", detail: CaptureEventDetails ? $"{fault.Kind}; {fault.Role}[{fault.RoleOrdinal}]" : null);
        foreach (var instance in instances.Where(i => i.State != InstanceState.Stopped).ToArray())
        {
            bool crashed = HasFault(instance, FaultKind.Crash);
            if (crashed && !instance.Crashed)
            {
                instance.Crashed = true;
                foreach (var work in instance.Active.ToArray()) FinishWork(work);
                while (instance.Queue.TryDequeue(out var work)) work.Finished = true;
                TryFinishDraining(instance);
            }
            else if (!crashed && instance.Crashed) { instance.Crashed = false; Pump(instance); }
        }
    }
    private Instance CreateInstance(InstanceRole role, bool initiallyReady = false)
    {
        int ordinal = 0;
        while (instances.Any(i => i.Role == role && i.State != InstanceState.Stopped && i.Ordinal == ordinal)) ordinal++;
        var spec = role == InstanceRole.Api ? scenario.Settings.Api : scenario.Settings.Worker;
        var instance = new Instance { Id = ++nextInstance, Ordinal = ordinal, Role = role, Spec = spec,
            State = initiallyReady ? InstanceState.Ready : InstanceState.Starting, AllocatedAt = Now,
            Mode = EndpointMode.AllowTraffic, Speed = spec.Speed * new[] { 1.0, .8, 1.2 }[ordinal % 3] };
        instances.Add(instance);
        if (role == InstanceRole.Api) allocatedApis++; else allocatedWorkers++;
        instance.Crashed = HasFault(instance, FaultKind.Crash);
        Notify(new(ObservationKind.InstanceStarting, Now, EndpointId: instance.Id));
        Emit("InstanceStarting", instance: instance, detail: CaptureEventDetails ? role.ToString() : null);
        void Ready()
        {
            if (instance.State != InstanceState.Starting && !initiallyReady) return;
            instance.State = InstanceState.Ready;
            Notify(new(ObservationKind.InstanceReady, Now, EndpointId: instance.Id));
            Emit("InstanceReady", instance: instance);
        }
        if (initiallyReady) Ready(); else Schedule(After(spec.StartupDelay), Ready);
        return instance;
    }
    private void Review()
    {
        foreach (var instance in instances.Where(i => i.State is InstanceState.Ready or InstanceState.Draining).ToArray())
        {
            ReviewEndpoint(instance);
            Pump(instance);
        }
        foreach (var rpc in rpcs.Values.Where(r => r.Waiting && !r.Done).ToArray()) Route(rpc);
        foreach (var job in liveJobs.Where(j => !j.Queued).ToArray())
        {
            if (job.Customer.ProcessingEnded) { CompleteJob(job); continue; }
            var decision = Decide("ReviewJob", () => policy.ReviewJob(new(Now, JobView(job)), policyRandom));
            if (decision == null || decision.Delay < TimeSpan.Zero) Invalid("ReviewJob", $"job {job.Id}: invalid delay");
            if (decision!.Requeue) Requeue(job, decision.Delay);
        }
        DispatchJobs();
        PumpStorage();
        if (scenario.Settings.ReviewInterval <= finalTime - Now)
            Schedule(Now + scenario.Settings.ReviewInterval, Review, 40);
    }
    private void ReviewEndpoint(Instance instance)
    {
        var decision = Decide("ReviewEndpoint", () => policy.ReviewEndpoint(new(Now, Endpoint(instance), instance.ResultsSnapshot ??= ReadOnly(instance.Results)), policyRandom));
        if (decision == null || !Enum.IsDefined(decision.Mode)) Invalid("ReviewEndpoint", $"instance {instance.Id}: invalid mode");
        instance.Mode = decision!.Mode;
        if (instance.State == InstanceState.Ready && instance.Mode == EndpointMode.Probe && !instance.ProbeOutstanding)
            StartProbe(instance);
    }
    private void StartProbe(Instance instance)
    {
        instance.ProbeOutstanding = true;
        bool observed = false;
        var started = Now;
        void Result(RequestOutcome outcome)
        {
            if (observed) return;
            observed = true;
            instance.ProbeOutstanding = false;
            RememberResult(instance, outcome, Now - started, true);
            Notify(new(ObservationKind.ProbeCompleted, Now, EndpointId: instance.Id, Outcome: outcome, Elapsed: Now - started));
            Emit("ProbeCompleted", instance: instance, detail: CaptureEventDetails ? outcome.ToString() : null);
            // The next scheduled review decides whether to reopen ordinary traffic.
        }
        var work = new Work { Instance = instance, IsProbe = true, EnqueuedAt = Now,
            ProbeReply = outcome => { if (NetworkPasses(instance)) Schedule(After(NetworkDelay()), () => Result(outcome)); } };
        Schedule(After(NetworkDelay()), () => { if (NetworkPasses(instance)) EnqueueWork(work); });
        Schedule(After(scenario.Settings.ProbeTimeout), () => Result(RequestOutcome.Timeout), 70);
        Emit("ProbeStarted", instance: instance);
    }
    private void RememberResult(Instance instance, RequestOutcome outcome, TimeSpan elapsed, bool probe)
    {
        instance.Results.Add(new(Now, outcome, elapsed, probe));
        instance.ResultsSnapshot = null;
        if (instance.Results.Count > 64) instance.Results.RemoveAt(0);
    }
    private void ReviewCapacity()
    {
        var context = new CapacityContext(Now, scenario.Settings,
            ReadOnly(instances.Where(i => i.State != InstanceState.Stopped).Select(Endpoint)),
            rpcs.Values.Count(r => r.Origin.Info.Kind != RequestKind.Metadata),
            ReadOnly(liveJobs.Where(j => j.Queued).Select(JobView)), apiCost, workerCost);
        var plan = Decide("ReviewCapacity", () => policy.ReviewCapacity(context, policyRandom));
        if (plan == null || plan.DesiredApiCount < 0 || plan.DesiredApiCount > scenario.Settings.MaximumApis ||
            plan.DesiredWorkerCount < 0 || plan.DesiredWorkerCount > scenario.Settings.MaximumWorkers ||
            (long)plan.DesiredApiCount * scenario.Settings.Api.ComputeUnits +
            (long)plan.DesiredWorkerCount * scenario.Settings.Worker.ComputeUnits > scenario.Settings.ComputeUnitLimit)
            Invalid("ReviewCapacity", "counts exceed role or shared compute limits");
        foreach (long id in plan!.RetireInstanceIds ?? Array.Empty<long>())
        {
            var instance = instances.SingleOrDefault(i => i.Id == id && i.State != InstanceState.Stopped);
            if (instance == null) Invalid("ReviewCapacity", $"unknown allocated instance {id}");
            Drain(instance!);
        }
        foreach (var role in Enum.GetValues<InstanceRole>())
        {
            int desired = role == InstanceRole.Api ? plan.DesiredApiCount : plan.DesiredWorkerCount;
            var owned = instances.Where(i => i.Role == role && i.State is InstanceState.Ready or InstanceState.Starting).ToList();
            foreach (var victim in owned.OrderBy(i => i.Mode == EndpointMode.AllowTraffic ? 1 : 0)
                .ThenBy(i => i.Outstanding + Endpoint(i).AssignedJobs).ThenBy(i => i.Id).Take(Math.Max(0, owned.Count - desired))) Drain(victim);
            int count = instances.Count(i => i.Role == role && i.State is InstanceState.Ready or InstanceState.Starting);
            var spec = role == InstanceRole.Api ? scenario.Settings.Api : scenario.Settings.Worker;
            while (count < desired && instances.Where(i => i.State != InstanceState.Stopped).Sum(i => i.Spec.ComputeUnits) + spec.ComputeUnits <= scenario.Settings.ComputeUnitLimit &&
                instances.Count(i => i.Role == role && i.State != InstanceState.Stopped) < (role == InstanceRole.Api ? scenario.Settings.MaximumApis : scenario.Settings.MaximumWorkers))
            { CreateInstance(role); count++; }
        }
        if (scenario.Settings.CapacityReviewInterval <= finalTime - Now)
            Schedule(Now + scenario.Settings.CapacityReviewInterval, ReviewCapacity, 45);
    }
    private void Drain(Instance instance)
    {
        if (instance.State is InstanceState.Draining or InstanceState.Stopped) return;
        instance.State = InstanceState.Draining;
        Notify(new(ObservationKind.InstanceDraining, Now, EndpointId: instance.Id));
        Emit("InstanceDraining", instance: instance);
        Schedule(After(instance.Spec.DrainGrace), () => { if (instance.State == InstanceState.Draining) StopInstance(instance); });
        TryFinishDraining(instance);
    }
    private void TryFinishDraining(Instance instance)
    {
        if (instance.State == InstanceState.Draining && instance.Active.Count == 0 && instance.Queue.Count == 0)
            StopInstance(instance);
    }
    private void StopInstance(Instance instance)
    {
        if (instance.State == InstanceState.Stopped) return;
        instance.State = InstanceState.Stopped;
        instance.StoppedAt = Now;
        if (instance.Role == InstanceRole.Api) allocatedApis--; else allocatedWorkers--;
        foreach (var work in instance.Active.ToArray()) FinishWork(work);
        while (instance.Queue.TryDequeue(out var work)) work.Finished = true;
        Notify(new(ObservationKind.InstanceStopped, Now, EndpointId: instance.Id));
        Emit("InstanceStopped", instance: instance);
    }
    private void Heartbeats()
    {
        foreach (var instance in instances.Where(i => i.Role == InstanceRole.Worker && i.State is InstanceState.Ready or InstanceState.Draining).ToArray())
        {
            if (instance.Crashed || HasFault(instance, FaultKind.HeartbeatIsolation) || !NetworkPasses(instance)) continue;
            var reports = instance.Active.Where(w => w.Job != null).Select(w => (Job: w.Job!, w.Generation)).ToArray();
            Schedule(After(NetworkDelay()), () =>
            {
                if (instance.State == InstanceState.Stopped) return;
                instance.LastHeartbeat = Now;
                Notify(new(ObservationKind.WorkerHeartbeatReceived, Now, EndpointId: instance.Id));
                foreach (var report in reports)
                    if (!report.Job.Done && report.Job.Worker == instance && report.Job.Generation == report.Generation)
                        report.Job.LastHeartbeat = Now;
            });
        }
        if (scenario.Settings.HeartbeatInterval <= finalTime - Now)
            Schedule(Now + scenario.Settings.HeartbeatInterval, Heartbeats, 25);
    }
}

