namespace SPS.Abstractions;

public enum CacheReadAction { ServeCached, JoinFetch, StartFetch }

public sealed class CacheEntryInfo
{
    public string Key { get; }
    public long Version { get; }
    public long SizeBytes { get; }
    public TimeSpan SourceObservedAt { get; }
    public TimeSpan ExpiresAt { get; }

    public CacheEntryInfo(string Key, long Version, long SizeBytes, TimeSpan SourceObservedAt, TimeSpan ExpiresAt)
    {
        this.Key = Key;
        this.Version = Version;
        this.SizeBytes = SizeBytes;
        this.SourceObservedAt = SourceObservedAt;
        this.ExpiresAt = ExpiresAt;
    }
}

// WaitingCustomers counts open service requests, including customers who silently left.
public sealed class FetchInfo
{
    public long Id { get; }
    public string Key { get; }
    public TimeSpan StartedAt { get; }
    public int WaitingCustomers { get; }

    public FetchInfo(long Id, string Key, TimeSpan StartedAt, int WaitingCustomers)
    {
        this.Id = Id;
        this.Key = Key;
        this.StartedAt = StartedAt;
        this.WaitingCustomers = WaitingCustomers;
    }
}

public sealed class CacheReadContext
{
    public TimeSpan Now { get; }
    public RequestInfo Request { get; }
    public CacheEntryInfo? Entry { get; }
    public IReadOnlyList<FetchInfo> PendingFetches { get; }

    public CacheReadContext(TimeSpan Now, RequestInfo Request, CacheEntryInfo? Entry,
        IReadOnlyList<FetchInfo> PendingFetches)
    {
        this.Now = Now;
        this.Request = Request;
        this.Entry = Entry;
        this.PendingFetches = PendingFetches;
    }
}

public sealed class CacheFillContext
{
    public TimeSpan Now { get; }
    public CacheEntryInfo Value { get; }
    public IReadOnlyList<CacheEntryInfo> Entries { get; }
    public long CapacityBytes { get; }

    public CacheFillContext(TimeSpan Now, CacheEntryInfo Value, IReadOnlyList<CacheEntryInfo> Entries,
        long CapacityBytes)
    {
        this.Now = Now;
        this.Value = Value;
        this.Entries = Entries;
        this.CapacityBytes = CapacityBytes;
    }
}

public sealed class CacheReadDecision
{
    public CacheReadAction Action { get; }
    public long? FetchId { get; }

    public CacheReadDecision(CacheReadAction Action, long? FetchId = null)
    {
        this.Action = Action;
        this.FetchId = FetchId;
    }

    public static CacheReadDecision ServeCached() => new(CacheReadAction.ServeCached);
    public static CacheReadDecision JoinFetch(long fetchId) => new(CacheReadAction.JoinFetch, fetchId);
    public static CacheReadDecision StartFetch() => new(CacheReadAction.StartFetch);
}

public sealed class CacheWriteDecision
{
    public bool Store { get; }
    public TimeSpan Ttl { get; }
    public IReadOnlyList<string> KeysToEvict { get; }

    public CacheWriteDecision(bool Store, TimeSpan Ttl, IReadOnlyList<string> KeysToEvict)
    {
        this.Store = Store;
        this.Ttl = Ttl;
        this.KeysToEvict = KeysToEvict;
    }

    public static CacheWriteDecision Skip() => new(false, TimeSpan.Zero, Array.Empty<string>());
    public static CacheWriteDecision Retain(TimeSpan ttl, params string[] evictions) => new(true, ttl, Array.AsReadOnly(evictions));
}
