using SPS.Abstractions;

namespace SPS.Simulation;

public sealed partial class SimulationSession
{
    private bool RpcLive(Rpc rpc) => !rpc.Done && (rpc.Fetch != null
        ? rpc.Fetch.Customers.Any(c => !c.ProcessingEnded) : !rpc.Origin.ProcessingEnded);
    private Customer Representative(Rpc rpc) => rpc.Fetch?.Customers.FirstOrDefault(c => !c.ProcessingEnded) ?? rpc.Origin;
    private void CreateRpc(Customer customer, Fetch? fetch)
    {
        var rpc = new Rpc { Id = ++nextRpc, Origin = customer, Fetch = fetch, Waiting = true };
        rpcs.Add(rpc.Id, rpc);
        Route(rpc);
    }
    private void Route(Rpc rpc)
    {
        if (rpc.Fetch is { } fetch)
        {
            foreach (var customer in fetch.Customers) ExpireProcessingBudget(customer);
        }
        else ExpireProcessingBudget(rpc.Origin);
        if (!RpcLive(rpc)) { FinishRpc(rpc, false); return; }
        var representative = Representative(rpc);
        Instance? instance = null;
        if (rpc.Origin.Info.Kind != RequestKind.Metadata)
        {
            var eligible = instances.Where(i => i.Role == InstanceRole.Api && i.State == InstanceState.Ready && i.Mode == EndpointMode.AllowTraffic).ToArray();
            var selection = Decide("SelectEndpoint", () => policy.SelectEndpoint(new(Now, representative.Info, rpc.Attempt + 1,
                ReadOnly(eligible.Select(Endpoint))), policyRandom));
            if (selection == null) { rpc.Waiting = true; return; }
            if (!Enum.IsDefined(selection.Action)) Invalid("SelectEndpoint", $"request {representative.Info.Id}: invalid action");
            if (selection.Action == AssignmentAction.Reject)
            {
                if (selection.EndpointId != null) Invalid("SelectEndpoint", $"request {representative.Info.Id}: rejection cannot include an endpoint");
                RejectByPolicy(representative, "SelectEndpoint");
                // Other coalesced callers retain their processing opportunity.
                if (RpcLive(rpc)) rpc.Waiting = true;
                return;
            }
            instance = eligible.SingleOrDefault(i => i.Id == selection.EndpointId);
            if (instance == null) Invalid("SelectEndpoint", $"request {representative.Info.Id}: endpoint {selection.EndpointId} is not eligible");
        }
        var timeout = Decide("RpcPolicy.GetTimeout", () => rpcPolicy.GetTimeout(new(rpc.Id, rpc.Attempt + 1), policyRandom));
        if (timeout <= TimeSpan.Zero) Invalid("RpcPolicy.GetTimeout", $"request {representative.Info.Id}: timeout must be positive");
        rpc.Waiting = false;
        var attempt = new Attempt { Rpc = rpc, Instance = instance, Number = ++rpc.Attempt, StartedAt = Now };
        if (instance != null) instance.Outstanding++;
        attempts++;
        if (rpc.Attempt > 1) retries++;
        Notify(new(ObservationKind.RpcAttemptStarted, Now, RequestId: representative.Info.Id, EndpointId: instance?.Id));
        Emit("RpcAttemptStarted", rpc.Origin, instance, detail: CaptureEventDetails ? $"rpc={rpc.Id}; attempt={attempt.Number}; target={(instance == null ? "storage" : "api")}; timeoutMs={timeout.TotalMilliseconds:F3}" : null);
        Schedule(After(NetworkDelay()), () =>
        {
            if (!NetworkPasses(instance)) return;
            var work = new Work { Instance = instance, Attempt = attempt, EnqueuedAt = Now };
            if (instance == null) QueueReadStorage(work);
            else EnqueueWork(work);
        });
        Schedule(After(timeout), () => AttemptResult(attempt, RequestOutcome.Timeout, null), 70);
    }
    private void ExpireProcessingBudget(Customer customer)
    {
        if (!customer.ProcessingEnded && customer.ProcessingDeadline is { } deadline && Now >= deadline)
            Fail(customer, RequestFailureReason.ProcessingBudgetExceeded);
    }
    private void SendReply(Attempt attempt, RequestOutcome outcome, CacheEntryInfo? value)
    {
        if (NetworkPasses(attempt.Instance))
            Schedule(After(NetworkDelay()), () => AttemptResult(attempt, outcome, value));
        else Emit("ReplyLost", attempt.Rpc.Origin, attempt.Instance);
    }
    private void AttemptResult(Attempt attempt, RequestOutcome outcome, CacheEntryInfo? value)
    {
        if (attempt.Observed)
        {
            if (outcome != RequestOutcome.Timeout) { lateResponses++; Emit("LateResponse", attempt.Rpc.Origin, attempt.Instance); }
            return;
        }
        attempt.Observed = true;
        if (attempt.Instance != null) attempt.Instance.Outstanding--;
        if (outcome == RequestOutcome.Timeout) timeouts++;
        if (attempt.Instance != null) RememberResult(attempt.Instance, outcome, Now - attempt.StartedAt, false);
        var rpc = attempt.Rpc;
        var decision = Decide("RpcPolicy.OnResult", () => rpcPolicy.OnResult(new(rpc.Id, attempt.Number, outcome, Now - attempt.StartedAt), policyRandom));
        if (decision == null || decision.Delay < TimeSpan.Zero) Invalid("RpcPolicy.OnResult", $"rpc {rpc.Id}: invalid retry delay");
        Notify(new(ObservationKind.RpcAttemptCompleted, Now, RequestId: rpc.Origin.Info.Id,
            EndpointId: attempt.Instance?.Id, Outcome: outcome, Elapsed: Now - attempt.StartedAt));
        Emit("RpcAttemptCompleted", rpc.Origin, attempt.Instance, detail: CaptureEventDetails ? outcome.ToString() : null);
        if (attempt.Instance?.State == InstanceState.Ready) ReviewEndpoint(attempt.Instance);
        if (!RpcLive(rpc)) { FinishRpc(rpc, false); return; }
        if (outcome == RequestOutcome.Success)
        {
            if (rpc.Fetch != null && value != null) CompleteFetch(rpc.Fetch, value);
            FinishRpc(rpc, true);
        }
        else if (decision!.Retry)
            Schedule(After(decision.Delay), () => Route(rpc), 35);
        else FinishRpc(rpc, false, outcome);
    }
    private void FinishRpc(Rpc rpc, bool success, RequestOutcome? failureOutcome = null)
    {
        if (rpc.Done) return;
        rpc.Done = true;
        rpcs.Remove(rpc.Id);
        if (rpc.Fetch is { } fetch)
        {
            fetch.Done = true;
            fetches.Remove(fetch.Id);
            if (!success) foreach (var customer in fetch.Customers) Fail(customer, RequestFailureReason.FetchFailed, failureOutcome);
            Notify(new(ObservationKind.FetchCompleted, Now, RequestId: rpc.Origin.Info.Id, Key: fetch.Key,
                Outcome: success ? RequestOutcome.Success : failureOutcome ?? RequestOutcome.Unavailable));
        }
        // A lost registration acknowledgement cannot undo an already durable job.
        else if (!success && !registeredJobs.Contains(rpc.Origin.Info.Id)) Fail(rpc.Origin, RequestFailureReason.SubmissionFailed, failureOutcome);
    }
}
