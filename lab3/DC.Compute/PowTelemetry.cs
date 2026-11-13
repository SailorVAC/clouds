using System.Diagnostics;
using System.ComponentModel;

namespace DC.Compute;

public sealed record PowWorkerSample(int WorkerId, int ThreadId, int TaskId,
    bool Active, long HashAttempts, double AssignedSeconds);

public sealed record PowPuzzleSample(int TaskId, bool Started, bool Solved,
    long HashAttempts, double ElapsedSeconds, double AssignedSeconds);

public sealed record PowTelemetrySnapshot(double ElapsedSeconds, bool Started, bool Finished, double? ProcessCpuSeconds,
    IReadOnlyList<PowWorkerSample> Workers, IReadOnlyList<PowPuzzleSample> Puzzles)
{
    public long HashAttempts => Workers.Sum(worker => worker.HashAttempts);
    public int Completed => Puzzles.Count(puzzle => puzzle.Solved);
}

public sealed class PowTelemetry
{
    private sealed class Worker
    {
        public int ThreadId;
        public int TaskId = -1;
        public long ActiveSince;
        public long AssignedTicks;
        public long Attempts;
    }

    private sealed class Puzzle
    {
        public long StartedAt;
        public long SolvedAt;
        public long AssignedTicks;
        public long Attempts;
    }

    private readonly object _gate = new();
    private readonly Worker[] _workers;
    private readonly Puzzle[] _puzzles = Enumerable.Range(0, 100).Select(_ => new Puzzle()).ToArray();
    private long _startedAt;
    private long _finishedAt;
    private double? _cpuAtStart;
    private double? _cpuAtFinish;

    public PowTelemetry(int workerCount)
    {
        if (workerCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(workerCount));
        _workers = Enumerable.Range(0, workerCount).Select(_ => new Worker()).ToArray();
    }

    public void Start()
    {
        lock (_gate)
        {
            _cpuAtStart = ReadProcessCpu();
            _startedAt = Stopwatch.GetTimestamp();
        }
    }

    public int BeginWork(int taskId, int preferredWorker = -1)
    {
        lock (_gate)
        {
            if (_startedAt == 0 || _finishedAt != 0 || taskId is < 0 or >= 100) return -1;
            int threadId = Environment.CurrentManagedThreadId;
            int lane = preferredWorker;
            if (lane < 0)
            {
                lane = Array.FindIndex(_workers, candidate => candidate.TaskId < 0 && candidate.ThreadId == threadId);
                if (lane < 0) lane = Array.FindIndex(_workers, candidate => candidate.TaskId < 0);
            }
            if (lane < 0 || lane >= _workers.Length || _workers[lane].TaskId >= 0) return -1;
            long now = Stopwatch.GetTimestamp();
            Worker worker = _workers[lane];
            worker.ThreadId = threadId;
            worker.TaskId = taskId;
            worker.ActiveSince = now;
            if (_puzzles[taskId].StartedAt == 0) _puzzles[taskId].StartedAt = now;
            return lane;
        }
    }

    public void AddAttempts(int workerId, int taskId, long count)
    {
        if (workerId < 0 || workerId >= _workers.Length || taskId is < 0 or >= 100 || count <= 0) return;
        Interlocked.Add(ref _workers[workerId].Attempts, count);
        Interlocked.Add(ref _puzzles[taskId].Attempts, count);
    }

    public void EndWork(int workerId)
    {
        lock (_gate)
        {
            if (workerId >= 0 && workerId < _workers.Length)
                EndWorkCore(_workers[workerId], Stopwatch.GetTimestamp());
        }
    }

    private void EndWorkCore(Worker worker, long now)
    {
        if (worker.TaskId < 0) return;
        long ticks = now - worker.ActiveSince;
        worker.AssignedTicks += ticks;
        _puzzles[worker.TaskId].AssignedTicks += ticks;
        worker.TaskId = -1;
        worker.ActiveSince = 0;
    }

    public void Solved(int taskId)
    {
        lock (_gate)
            if (taskId is >= 0 and < 100 && _puzzles[taskId].SolvedAt == 0)
                _puzzles[taskId].SolvedAt = Stopwatch.GetTimestamp();
    }

    public void Finish()
    {
        lock (_gate)
        {
            if (_startedAt == 0 || _finishedAt != 0) return;
            _finishedAt = Stopwatch.GetTimestamp();
            _cpuAtFinish = ReadProcessCpu();
            foreach (Worker worker in _workers) EndWorkCore(worker, _finishedAt);
        }
    }

    public PowTelemetrySnapshot Snapshot()
    {
        lock (_gate)
        {
            long now = _finishedAt == 0 ? Stopwatch.GetTimestamp() : _finishedAt;
            var assigned = _puzzles.Select(puzzle => puzzle.AssignedTicks).ToArray();
            var workers = new PowWorkerSample[_workers.Length];
            for (int index = 0; index < _workers.Length; index++)
            {
                Worker worker = _workers[index];
                long activeTicks = worker.TaskId >= 0 ? now - worker.ActiveSince : 0;
                if (worker.TaskId >= 0) assigned[worker.TaskId] += activeTicks;
                workers[index] = new PowWorkerSample(index, worker.ThreadId, worker.TaskId,
                    worker.TaskId >= 0, Interlocked.Read(ref worker.Attempts), Seconds(worker.AssignedTicks + activeTicks));
            }
            var puzzles = _puzzles.Select((puzzle, index) => new PowPuzzleSample(index,
                puzzle.StartedAt != 0, puzzle.SolvedAt != 0, Interlocked.Read(ref puzzle.Attempts),
                puzzle.StartedAt == 0 ? 0 : Seconds((puzzle.SolvedAt == 0 ? now : puzzle.SolvedAt) - puzzle.StartedAt),
                Seconds(assigned[index]))).ToArray();
            return new PowTelemetrySnapshot(_startedAt == 0 ? 0 : Seconds(now - _startedAt),
                _startedAt != 0, _finishedAt != 0,
                _startedAt == 0 ? null : (_finishedAt == 0 ? ReadProcessCpu() : _cpuAtFinish) - _cpuAtStart,
                workers, puzzles);
        }
    }

    private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;

    private static double? ReadProcessCpu()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime.TotalSeconds;
        }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }
}
