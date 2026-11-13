namespace DC.Compute;

public sealed record PowSolution(int TaskId, ulong Nonce);

public sealed record PowProgress(int TaskId, PowSolution? Solution,
    int CompletedChallenges, long HashAttempts);

public sealed record PowResult(IReadOnlyList<PowSolution> Solutions, TimeSpan Elapsed,
    int CompletedChallenges, int WorkerCount, long HashAttempts);
