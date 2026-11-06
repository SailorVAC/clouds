using SPS.Abstractions;

namespace SPS.Simulation;

public sealed partial class SimulationSession
{
    private void ReadCache(Customer customer)
    {
        cache.TryGetValue(customer.Info.ResourceKey, out var entry);
        if (entry != null && entry.ExpiresAt <= Now) { RemoveCache(entry.Key); entry = null; }
        var pending = fetches.Values.Where(f => !f.Done && f.Key == customer.Info.ResourceKey && f.Customers.Any(c => !c.ProcessingEnded)).ToArray();
        var decision = Decide("OnCacheRead", () => policy.OnCacheRead(new(Now, customer.Info, entry,
            ReadOnly(pending.Select(f => new FetchInfo(f.Id, f.Key, f.StartedAt, f.Customers.Count(c => !c.ProcessingEnded))))), policyRandom));
        if (decision == null || !Enum.IsDefined(decision.Action)) Invalid("OnCacheRead", $"request {customer.Info.Id}: invalid action");
        switch (decision!.Action)
        {
            case CacheReadAction.ServeCached:
                if (entry == null) Invalid("OnCacheRead", $"request {customer.Info.Id}: no cached entry exists");
                cacheHits++;
                Emit("CacheHit", customer, detail: customer.Info.ResourceKey);
                DeliverValue(customer, entry!);
                break;
            case CacheReadAction.JoinFetch:
                var fetch = pending.SingleOrDefault(f => f.Id == decision.FetchId);
                if (fetch == null) Invalid("OnCacheRead", $"request {customer.Info.Id}: incompatible fetch {decision.FetchId}");
                fetch!.Customers.Add(customer);
                coalesced++;
                Emit("FetchJoined", customer, detail: CaptureEventDetails ? fetch.Id.ToString() : null);
                break;
            case CacheReadAction.StartFetch:
                var created = new Fetch { Id = ++nextFetch, Key = customer.Info.ResourceKey, Origin = customer, StartedAt = Now };
                created.Customers.Add(customer);
                fetches.Add(created.Id, created);
                fetchCount++;
                Emit("FetchStarted", customer, detail: CaptureEventDetails ? created.Id.ToString() : null);
                CreateRpc(customer, created);
                break;
        }
    }
    private void CompleteFetch(Fetch fetch, CacheEntryInfo value)
    {
        var decision = Decide("OnCacheFill", () => policy.OnCacheFill(new(Now, value,
            ReadOnly(cache.Values), scenario.Settings.CacheCapacityBytes), policyRandom));
        if (decision == null || decision.KeysToEvict == null) Invalid("OnCacheFill", $"fetch {fetch.Id}: missing decision/evictions");
        if (decision!.Store)
        {
            if (decision.Ttl <= TimeSpan.Zero) Invalid("OnCacheFill", "retained entries require a positive TTL");
            var evictions = decision.KeysToEvict.Distinct(StringComparer.Ordinal).ToArray();
            if (evictions.Any(key => !cache.ContainsKey(key))) Invalid("OnCacheFill", "eviction references an absent entry");
            long retained = cache.Values.Where(c => c.Key != value.Key && !evictions.Contains(c.Key)).Sum(c => c.SizeBytes);
            if (value.SizeBytes > scenario.Settings.CacheCapacityBytes - retained) Invalid("OnCacheFill", "cache capacity exceeded");
            foreach (string key in evictions) RemoveCache(key);
            var expiresAt = decision.Ttl > TimeSpan.MaxValue - Now ? TimeSpan.MaxValue : Now + decision.Ttl;
            var stored = new CacheEntryInfo(value.Key, value.Version, value.SizeBytes, value.SourceObservedAt, expiresAt);
            cache[value.Key] = stored;
            Schedule(stored.ExpiresAt, () => { if (cache.TryGetValue(stored.Key, out var current) && ReferenceEquals(current, stored)) RemoveCache(stored.Key); }, 15);
            Emit("CacheStored", fetch.Origin, detail: CaptureEventDetails ? $"{stored.Key}; version={stored.Version}; sourceAt={stored.SourceObservedAt.TotalSeconds:F3}" : null);
        }
        foreach (var customer in fetch.Customers.Where(c => !c.ProcessingEnded)) DeliverValue(customer, value);
    }
    private void DeliverValue(Customer customer, CacheEntryInfo value)
    {
        Schedule(After(TimeSpan.FromMilliseconds(2)), () =>
        {
            if (customer.ProcessingEnded) return;
            if (Now - value.SourceObservedAt > customer.Info.MaxDataAge)
            {
                staleResponses++;
                Emit("StaleResponse", customer, detail: value.Key);
                Fail(customer, RequestFailureReason.StaleResponse, RequestOutcome.Stale);
            }
            else Succeed(customer);
        });
    }
    private void RemoveCache(string key)
    {
        if (!cache.Remove(key)) return;
        Notify(new(ObservationKind.CacheEntryRemoved, Now, Key: key));
        Emit("CacheEntryRemoved", detail: key);
    }
}
