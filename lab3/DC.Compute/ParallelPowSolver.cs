using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DC.Compute;

// A fixed set of workers dynamically claims whole puzzles. Each worker reuses
// its hash object and buffers; no synchronization occurs in the hot loop.
public sealed class ParallelPowSolver : IPowSolver
{
    public Task<PowResult> SolveAsync(string seed, int difficultyBits, int workerCount,
        IProgress<PowProgress>? progress, CancellationToken cancellationToken, PowTelemetry? telemetry = null)
    {
        if (seed is null || seed.Length != 6 || seed.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Seed must contain six digits.", nameof(seed));
        if (difficultyBits is < 1 or > 24) throw new ArgumentOutOfRangeException(nameof(difficultyBits));
        if (workerCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(workerCount));
        return RunAsync(seed, difficultyBits, workerCount, progress, cancellationToken, telemetry);
    }

    private static async Task<PowResult> RunAsync(string seed, int bits, int workers,
        IProgress<PowProgress>? progress, CancellationToken token, PowTelemetry? telemetry)
    {
        var solutions = new PowSolution[100];
        int next = -1, completed = 0;
        long attempts = 0;
        var clock = Stopwatch.StartNew();
        telemetry?.Start();
        try
        {
            var tasks = Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() =>
            {
                using var sha = SHA256.Create();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    int id = Interlocked.Increment(ref next);
                    if (id >= 100) break;
                    int lane = telemetry?.BeginWork(id, worker) ?? -1;
                    try
                    {
                        progress?.Report(new PowProgress(id, null, Volatile.Read(ref completed), Interlocked.Read(ref attempts)));
                        var (nonce, count) = Search(sha, seed, id, bits, token, telemetry, lane);
                        var solution = new PowSolution(id, nonce);
                        solutions[id] = solution;
                        telemetry?.Solved(id);
                        long total = Interlocked.Add(ref attempts, count);
                        int done = Interlocked.Increment(ref completed);
                        progress?.Report(new PowProgress(id, solution, done, total));
                    }
                    finally { telemetry?.EndWork(lane); }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
            clock.Stop();
            return new PowResult(Array.AsReadOnly(solutions), clock.Elapsed, completed, workers, attempts);
        }
        finally { telemetry?.Finish(); }
    }

    private static (ulong, long) Search(SHA256 sha, string seed, int id, int bits,
        CancellationToken token, PowTelemetry? telemetry, int lane)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(FormattableString.Invariant($"BSU-POW-1|{seed}|{id}|"));
        Span<byte> input = stackalloc byte[prefix.Length + 20];
        prefix.CopyTo(input);
        Span<byte> hash = stackalloc byte[32];
        long count = 0, reported = 0;
        try
        {
            for (ulong nonce = 0; ; nonce++)
            {
                if ((nonce & 1023) == 0) token.ThrowIfCancellationRequested();
                Utf8Formatter.TryFormat(nonce, input[prefix.Length..], out int length);
                if (!sha.TryComputeHash(input[..(prefix.Length + length)], hash, out _))
                    throw new InvalidOperationException("SHA-256 buffer is too small.");
                count++;
                if ((count & 8191) == 0 && telemetry != null)
                {
                    telemetry.AddAttempts(lane, id, count - reported);
                    reported = count;
                }
                int bytes = bits / 8, remaining = bits % 8, i = 0;
                while (i < bytes && hash[i] == 0) i++;
                if (i == bytes && (remaining == 0 || (hash[bytes] >> (8 - remaining)) == 0))
                    return (nonce, count);
                if (nonce == ulong.MaxValue) throw new InvalidOperationException("Nonce space exhausted.");
            }
        }
        finally { telemetry?.AddAttempts(lane, id, count - reported); }
    }
}
