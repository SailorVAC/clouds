using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DC.Compute;

public sealed class PowSolver : IPowSolver
{
    public const int TaskCount = 100;
    public const int MaximumDifficultyBits = 24;
    public const int MaximumWorkers = TaskCount;

    public Task<PowResult> SolveAsync(string seed, int difficultyBits, int workerCount,
        IProgress<PowProgress>? progress, CancellationToken cancellationToken, PowTelemetry? telemetry = null)
    {
        if (seed is null || seed.Length != 6 || seed.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("The seed must contain exactly six decimal digits (0–9).", nameof(seed));
        if (difficultyBits is < 1 or > MaximumDifficultyBits)
            throw new ArgumentOutOfRangeException(nameof(difficultyBits), "Difficulty must be between 1 and 24 bits.");
        if (workerCount is < 1 or > MaximumWorkers)
            throw new ArgumentOutOfRangeException(nameof(workerCount), "Worker count must be between 1 and 100.");

        return Task.Run(() =>
        {
            var solutions = new PowSolution[TaskCount];
            int completed = 0;
            long totalAttempts = 0;
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = workerCount,
                CancellationToken = cancellationToken
            };
            var elapsed = Stopwatch.StartNew();
            telemetry?.Start();
            try
            {
                Parallel.For(0, TaskCount, options, taskId =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int lane = telemetry?.BeginWork(taskId) ?? -1;
                    try
                    {
                        progress?.Report(new PowProgress(taskId, null,
                            Volatile.Read(ref completed), Interlocked.Read(ref totalAttempts)));

                        var (nonce, attempts) = SolveChallenge(seed, taskId, difficultyBits, cancellationToken, telemetry, lane);
                        cancellationToken.ThrowIfCancellationRequested();
                        var solution = new PowSolution(taskId, nonce);
                        solutions[taskId] = solution;
                        telemetry?.Solved(taskId);
                        long recordedAttempts = Interlocked.Add(ref totalAttempts, attempts);
                        int recordedCompleted = Interlocked.Increment(ref completed);
                        progress?.Report(new PowProgress(taskId, solution, recordedCompleted, recordedAttempts));
                    }
                    finally { telemetry?.EndWork(lane); }
                });
                elapsed.Stop();
                cancellationToken.ThrowIfCancellationRequested();
                return new PowResult(Array.AsReadOnly(solutions), elapsed.Elapsed,
                    completed, workerCount, totalAttempts);
            }
            finally { telemetry?.Finish(); }
        }, cancellationToken);
    }

    private static (ulong Nonce, long Attempts) SolveChallenge(string seed, int taskId,
        int difficultyBits, CancellationToken cancellationToken, PowTelemetry? telemetry, int lane)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(FormattableString.Invariant($"BSU-POW-1|{seed}|{taskId}|"));
        Span<byte> message = stackalloc byte[prefix.Length + 20];
        prefix.AsSpan().CopyTo(message);
        Span<byte> hash = stackalloc byte[32];
        long attempts = 0;
        long reported = 0;
        try
        {
            for (ulong nonce = 0; ; nonce++)
            {
                if ((nonce & 255UL) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!Utf8Formatter.TryFormat(nonce, message[prefix.Length..], out int digits))
                    throw new InvalidOperationException("Could not encode the nonce.");
                SHA256.HashData(message[..(prefix.Length + digits)], hash);
                attempts = checked(attempts + 1);
                if (telemetry is not null && (attempts & 8191) == 0)
                {
                    telemetry.AddAttempts(lane, taskId, attempts - reported);
                    reported = attempts;
                }
                if (HasLeadingZeroBits(hash, difficultyBits)) return (nonce, attempts);
                if (nonce == ulong.MaxValue)
                    throw new InvalidOperationException($"Task {taskId} exhausted all unsigned 64-bit nonces.");
            }
        }
        finally { telemetry?.AddAttempts(lane, taskId, attempts - reported); }
    }

    private static bool HasLeadingZeroBits(ReadOnlySpan<byte> hash, int difficultyBits)
    {
        int completeBytes = difficultyBits / 8;
        for (int index = 0; index < completeBytes; index++)
            if (hash[index] != 0) return false;
        int remainingBits = difficultyBits % 8;
        return remainingBits == 0 || (hash[completeBytes] >> (8 - remainingBits)) == 0;
    }
}
