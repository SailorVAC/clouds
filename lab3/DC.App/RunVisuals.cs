using DC.Compute;

namespace DC.App;

internal sealed record TraceSample(double Seconds, long Attempts, int Completed,
    double[] AssignedSeconds, double? CpuSeconds);

internal readonly record struct PlotPoint(double X, double Y);
internal sealed record RunKey(string Implementation, string Seed, int Difficulty, bool Instrumented);
internal sealed record RecordedRun(Guid Id, RunKey Key, int Workers, double Seconds,
    long Attempts, IReadOnlyList<PlotPoint> Completion)
{
    public double HashRate => Seconds > 0 ? Attempts / Seconds : 0;
}

internal sealed class RunVisuals
{
    private readonly PowTelemetry? _telemetry;
    private double _samplingInterval = 0.25;
    public Guid Id { get; } = Guid.NewGuid();
    public RunKey Key { get; }
    public int RequestedWorkers { get; }
    public int LogicalProcessors { get; } = Environment.ProcessorCount;
    public List<TraceSample> Samples { get; } = [];
    public List<RecordedRun> History { get; }
    public PowTelemetrySnapshot? Latest { get; private set; }
    public bool HistogramByAttempts { get; set; }
    public bool Finished { get; private set; }
    public string Outcome { get; private set; } = "Running";

    public RunVisuals(RunKey key, int workers, PowTelemetry? telemetry, List<RecordedRun> history)
    {
        Key = key;
        RequestedWorkers = workers;
        _telemetry = telemetry;
        History = history;
        Samples.Add(new TraceSample(0, 0, 0, new double[workers], 0));
    }

    public IEnumerable<RecordedRun> MatchingRuns => History.Where(run => run.Key == Key);
    public RecordedRun? Reference => MatchingRuns.LastOrDefault(run => run.Workers == 1 && run.Id != Id);

    public void Sample(bool force = false)
    {
        if (_telemetry is null) return;
        Latest = _telemetry.Snapshot();
        if (!Latest.Started) return;
        TraceSample last = Samples[^1];
        if (!force && Latest.ElapsedSeconds - last.Seconds < _samplingInterval) return;
        var sample = new TraceSample(Latest.ElapsedSeconds, Latest.HashAttempts, Latest.Completed,
            Latest.Workers.Select(worker => worker.AssignedSeconds).ToArray(), Latest.ProcessCpuSeconds);
        if (sample.Seconds <= last.Seconds) Samples[^1] = sample;
        else Samples.Add(sample);
        if (Samples.Count > 600)
        {
            for (int index = Samples.Count - 2; index > 0; index -= 2) Samples.RemoveAt(index);
            _samplingInterval *= 2;
        }
    }

    public void Stop(string outcome, PowResult? result = null)
    {
        Sample(force: true);
        Finished = true;
        Outcome = outcome;
        if (result is null || result.Elapsed.TotalSeconds <= 0 || result.Solutions.Count != 100) return;
        double scale = Latest is { ElapsedSeconds: > 0 }
            ? result.Elapsed.TotalSeconds / Latest.ElapsedSeconds : 1;
        var completion = Samples.Select(sample => new PlotPoint(sample.Seconds * scale, sample.Completed)).ToList();
        if (_telemetry is null) completion = [];
        History.Add(new RecordedRun(Id, Key, RequestedWorkers, result.Elapsed.TotalSeconds,
            result.HashAttempts, completion.AsReadOnly()));
        if (History.Count > 30) History.RemoveAt(0);
    }

    public static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
