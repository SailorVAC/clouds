using DC.Compute;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DC.App;

public partial class MainWindow : Window
{
    private const int TaskCount = 100;
    private readonly (string Name, IPowSolver Solver)[] _implementations =
    [
        ("Baseline", new PowSolver()),
        ("Parallel workers", new ParallelPowSolver()),
    ];
    private readonly TaskRow[] _rows = Enumerable.Range(0, TaskCount).Select(id => new TaskRow(id)).ToArray();
    private readonly Stopwatch _liveElapsed = new();
    private readonly DispatcherTimer _timer;
    private readonly LabChart[] _charts;
    private readonly List<RecordedRun> _runHistory = [];
    private RunVisuals? _visuals;
    private List<PowSolution> _submittedSolutions = [];
    private CancellationTokenSource? _cancellation;
    private Task<PowResult>? _runningTask;
    private Guid _runId;
    private string? _sessionSeed;
    private int _sessionDifficulty;
    private int _completed;
    private long _attempts;
    private bool _isRunning;
    private bool _closing;
    private bool _allowClose;
    private string? _lastCompletedRun;

    public MainWindow()
    {
        InitializeComponent();
        _charts = [CompletionChart, ThroughputChart, CapacityChart, WorkerTimelineChart,
            PuzzleTimesChart, HistogramChart, SpeedupChart];
        ImplementationInput.ItemsSource = _implementations.Select(item => item.Name).ToArray();
        ImplementationInput.SelectedIndex = 0;
        ResultsGrid.ItemsSource = _rows;
        WorkersInput.Text = Math.Clamp(Environment.ProcessorCount, 1, 100).ToString(CultureInfo.InvariantCulture);
        ProcessorLabel.Text = $"Logical processors: {Environment.ProcessorCount}";
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            ElapsedLabel.Text = $"Elapsed: {_liveElapsed.Elapsed:hh\\:mm\\:ss\\.fff}";
            RefreshVisuals();
        };
        UpdateControls();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _closing) return;
        string seed = SeedInput.Text;
        if (seed.Length != 6 || seed.Any(character => character is < '0' or > '9'))
        {
            Message.Text = "Enter exactly six decimal seed digits (0–9), for example 000123.";
            return;
        }
        if (!int.TryParse(DifficultyInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int difficulty)
            || difficulty is < 1 or > 24)
        {
            Message.Text = "Difficulty must be a whole number from 1 through 24.";
            return;
        }
        if (!int.TryParse(WorkersInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int workers)
            || workers is < 1 or > 100)
        {
            Message.Text = "Parallel workers must be a whole number from 1 through 100.";
            return;
        }
        int implementationIndex = ImplementationInput.SelectedIndex;
        if (implementationIndex < 0 || implementationIndex >= _implementations.Length)
        {
            Message.Text = "Select a solver implementation.";
            return;
        }
        var implementation = _implementations[implementationIndex];
        PowTelemetry? telemetry = CollectMeasurementsBox.IsChecked == true ? new PowTelemetry(workers) : null;
        _visuals = new RunVisuals(new RunKey(implementation.Name, seed, difficulty, telemetry is not null),
            workers, telemetry, _runHistory)
        {
            HistogramByAttempts = HistogramMeasureInput.SelectedIndex == 1
        };

        _sessionSeed = seed;
        _sessionDifficulty = difficulty;
        _submittedSolutions = [];
        _completed = 0;
        _attempts = 0;
        Guid runId = _runId = Guid.NewGuid();
        foreach (var row in _rows)
        {
            row.Nonce = "—";
            row.Status = "Pending";
        }
        ValidationOutput.Text = "Validation: Not run";
        PreviousRunLabel.Text = _lastCompletedRun is null
            ? "Compare implementations with identical parameters; also measure each implementation with 1 worker."
            : $"Previous completed run: {_lastCompletedRun}";
        SessionLabel.Text = $"Original session: seed {seed} · difficulty {difficulty} bits · 100 challenges\nImplementation: {implementation.Name}";
        WorkerLabel.Text = $"Parallel workers: {workers}";
        CompletedLabel.Text = "Completed: 0 / 100";
        AttemptsLabel.Text = "SHA-256 attempts reported so far: 0";
        RunProgress.Value = 0;
        ElapsedLabel.Text = "Elapsed: 00:00:00.000";
        StatusLabel.Text = "Status: Computing";
        Message.Text = "Every run starts fresh. Implementations can examine different candidates; compare both time and hash throughput, then validate.";
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        CancellationToken cancellationToken = cancellation.Token;
        _isRunning = true;
        UpdateControls();
        _liveElapsed.Restart();
        _timer.Start();
        RefreshVisuals();

        var progress = new Progress<PowProgress>(update =>
        {
            if (update is null || _closing || !_isRunning || _runId != runId
                || update.TaskId is < 0 or >= TaskCount) return;
            var row = _rows[update.TaskId];
            if (update.Solution is { } solution && solution.TaskId == update.TaskId)
            {
                row.Nonce = solution.Nonce.ToString(CultureInfo.InvariantCulture);
                row.Status = "Completed";
                _submittedSolutions.Add(solution);
            }
            else if (row.Status == "Pending") row.Status = "Running";
            _completed = Math.Max(_completed, Math.Clamp(update.CompletedChallenges, 0, TaskCount));
            _attempts = Math.Max(_attempts, update.HashAttempts);
            CompletedLabel.Text = $"Completed: {_completed} / 100";
            RunProgress.Value = _completed;
            AttemptsLabel.Text = $"SHA-256 attempts reported so far: {_attempts:N0}";
        });

        PowResult? successfulResult = null;
        string outcome = "Failed";
        try
        {
            _runningTask = implementation.Solver.SolveAsync(seed, difficulty, workers, progress, cancellationToken, telemetry);
            PowResult result = await _runningTask;
            cancellationToken.ThrowIfCancellationRequested();
            _submittedSolutions = result.Solutions.ToList();
            foreach (var row in _rows)
            {
                row.Nonce = "—";
                row.Status = "Missing";
            }
            foreach (PowSolution? solution in _submittedSolutions)
            {
                if (solution is null || solution.TaskId is < 0 or >= TaskCount) continue;
                var row = _rows[solution.TaskId];
                if (row.Status != "Missing") row.Status = "Duplicate";
                else
                {
                    row.Nonce = solution.Nonce.ToString(CultureInfo.InvariantCulture);
                    row.Status = "Completed";
                }
            }
            _completed = _submittedSolutions.Count;
            CompletedLabel.Text = $"Returned solutions: {_completed} / 100";
            RunProgress.Value = Math.Clamp(_completed, 0, TaskCount);
            ElapsedLabel.Text = $"Computation time: {result.Elapsed:hh\\:mm\\:ss\\.fff}";
            string throughput = result.Elapsed.TotalSeconds > 0
                ? (result.HashAttempts / result.Elapsed.TotalSeconds).ToString("N0", CultureInfo.CurrentCulture)
                : "—";
            AttemptsLabel.Text = $"SHA-256 attempts: {result.HashAttempts:N0} · Hashes/second: {throughput}";
            StatusLabel.Text = "Status: Computation finished — validation required";
            Message.Text = "The search has finished. Click Validate All Results to check the returned nonces independently.";
            _lastCompletedRun = $"{implementation.Name}; seed {seed}, {difficulty} bits, {workers} worker(s), {result.Elapsed:hh\\:mm\\:ss\\.fff}, {result.HashAttempts:N0} hashes, {throughput} hashes/s";
            successfulResult = result;
            outcome = "Finished; validation pending";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "Cancelled";
            StatusLabel.Text = "Status: Cancelled — partial results only";
            Message.Text = "The run was cancelled. Partial results can be inspected and validated, but are not a complete submission.";
            foreach (var row in _rows)
                if (row.Status is "Pending" or "Running") row.Status = "Cancelled";
            ElapsedLabel.Text = $"Elapsed before cancellation: {_liveElapsed.Elapsed:hh\\:mm\\:ss\\.fff}";
        }
        catch (Exception error)
        {
            StatusLabel.Text = "Status: Failed — no successful run recorded";
            Message.Text = error is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.FirstOrDefault()?.Message ?? aggregate.Message
                : error.Message;
            foreach (var row in _rows)
                if (row.Status is "Pending" or "Running") row.Status = "Failed";
            ElapsedLabel.Text = $"Elapsed before failure: {_liveElapsed.Elapsed:hh\\:mm\\:ss\\.fff}";
        }
        finally
        {
            _timer.Stop();
            _liveElapsed.Stop();
            _visuals?.Stop(outcome, successfulResult);
            _isRunning = false;
            _runId = Guid.Empty;
            _runningTask = null;
            cancellation.Dispose();
            _cancellation = null;
            UpdateControls();
            RefreshVisuals();
        }
    }

    private void RefreshVisuals()
    {
        if (_visuals is not { } visuals) return;
        visuals.Sample();
        if (visuals.Latest is { Started: true } latest && latest.ElapsedSeconds > 0)
        {
            double occupancy = latest.Workers.Sum(worker => worker.AssignedSeconds)
                / (latest.ElapsedSeconds * visuals.RequestedWorkers);
            string cpu = latest.ProcessCpuSeconds is double seconds
                ? $"{seconds / latest.ElapsedSeconds:F2} logical CPU slots on average"
                : "unavailable";
            MetricsSummary.Text = $"Active lanes: {latest.Workers.Count(worker => worker.Active)} / {visuals.RequestedWorkers}"
                + $" · Assigned-time occupancy: {occupancy:P1} · Process CPU: {cpu}. These are not parallel efficiency.";
            if (_isRunning)
            {
                _attempts = Math.Max(_attempts, latest.HashAttempts);
                AttemptsLabel.Text = $"Measured SHA-256 attempts so far: {_attempts:N0}";
            }
        }
        else MetricsSummary.Text = visuals.Key.Instrumented
            ? (visuals.Finished ? $"{visuals.Outcome}. The solver supplied no search measurements."
                : "Waiting for search measurements. Charts sample at about 4 Hz.")
            : "Live measurement collection is disabled. Timing/scaling still records; detailed plots have no measured data.";

        RecordedRun[] matching = visuals.MatchingRuns.ToArray();
        ScalingContextLabel.Text = $"{visuals.Key.Implementation} · seed {visuals.Key.Seed} · {visuals.Key.Difficulty} bits"
            + $" · {(visuals.Key.Instrumented ? "measurements on" : "measurements off")} · {matching.Length} matching run(s)";
        double baseline = RunVisuals.Median(matching.Where(run => run.Workers == 1).Select(run => run.Seconds));
        double baselineRate = RunVisuals.Median(matching.Where(run => run.Workers == 1).Select(run => run.HashRate));
        RecordedRun[] atWorkers = matching.Where(run => run.Workers == visuals.RequestedWorkers).ToArray();
        if (baseline > 0 && baselineRate > 0 && atWorkers.Length > 0)
        {
            double speedup = baseline / RunVisuals.Median(atWorkers.Select(run => run.Seconds));
            double rateSpeedup = RunVisuals.Median(atWorkers.Select(run => run.HashRate)) / baselineRate;
            EfficiencyLabel.Text = $"Recorded medians at {visuals.RequestedWorkers} worker(s): time speedup {speedup:F2}×"
                + $", efficiency {speedup / visuals.RequestedWorkers:P1}; hashes/s scaling {rateSpeedup:F2}×. Validation remains separate.";
        }
        else EfficiencyLabel.Text = "Complete matching 1-worker and selected-worker runs to calculate measured scaling. No speedup is inferred from CPU activity.";
        foreach (LabChart chart in _charts) chart.Display(visuals);
    }

    private void HistogramMeasure_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_visuals is not { } visuals) return;
        visuals.HistogramByAttempts = HistogramMeasureInput.SelectedIndex == 1;
        HistogramChart.Display(visuals);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _closing) return;
        _runHistory.Clear();
        _lastCompletedRun = null;
        PreviousRunLabel.Text = "Recorded timing history was cleared.";
        RefreshVisuals();
        Message.Text = "Recorded timing history cleared. The current solutions and validator data are unchanged.";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning || _cancellation is null) return;
        _cancellation.Cancel();
        CancelButton.IsEnabled = false;
        StatusLabel.Text = "Status: Cancelling…";
        Message.Text = "Waiting for the active CPU searches to observe cancellation.";
    }

    // INSTRUCTOR VALIDATOR - DO NOT MODIFY
    private void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _closing) return;
        if (_sessionSeed is null)
        {
            ValidationOutput.Text = "Validation: FAILED\nNo recorded challenge session.";
            return;
        }

        string seed = _sessionSeed;
        int difficultyBits = _sessionDifficulty;
        const int expectedCount = 100;
        var solutions = _submittedSolutions;
        var errors = new List<string>();
        var occurrences = new int[expectedCount];
        var hashIsValid = new bool[expectedCount];
        int invalidSolutions = 0;
        int missingSolutions = 0;
        int duplicateTaskIds = 0;
        int validSolutions = 0;

        if (seed.Length != 6 || seed.Any(character => character is < '0' or > '9')
            || difficultyBits is < 1 or > 24)
        {
            ValidationOutput.Text = "Validation: FAILED\nThe recorded session parameters are invalid.";
            return;
        }
        if (solutions.Count != expectedCount)
            errors.Add($"Expected exactly 100 entries; received {solutions.Count}.");

        foreach (PowSolution? solution in solutions)
        {
            if (solution is null)
            {
                invalidSolutions++;
                errors.Add("Invalid solution: null entry.");
                continue;
            }
            int taskId = solution.TaskId;
            if (taskId is < 0 or >= expectedCount)
            {
                invalidSolutions++;
                errors.Add($"Invalid Task ID: {taskId}; expected 0 through 99.");
                continue;
            }
            occurrences[taskId]++;

            string nonceText = solution.Nonce.ToString(CultureInfo.InvariantCulture);
            if (!ulong.TryParse(nonceText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong nonce))
            {
                invalidSolutions++;
                errors.Add($"Task {taskId}: Nonce is not an unsigned 64-bit integer.");
                continue;
            }

            string message = "BSU-POW-1|" + seed + "|"
                + taskId.ToString(CultureInfo.InvariantCulture) + "|"
                + nonce.ToString(CultureInfo.InvariantCulture);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(message));
            bool hasRequiredBits = true;
            for (int bit = 0; bit < difficultyBits; bit++)
            {
                int byteIndex = bit / 8;
                int mask = 1 << (7 - bit % 8);
                if ((hash[byteIndex] & mask) != 0)
                {
                    hasRequiredBits = false;
                    break;
                }
            }
            if (hasRequiredBits) hashIsValid[taskId] = true;
            else
            {
                invalidSolutions++;
                errors.Add($"Task {taskId}: Invalid nonce (leading zero bits do not meet difficulty {difficultyBits}).");
            }
        }

        for (int taskId = 0; taskId < expectedCount; taskId++)
        {
            if (occurrences[taskId] == 0)
            {
                missingSolutions++;
                errors.Add($"Task {taskId}: Missing solution.");
                _rows[taskId].Status = "Missing";
            }
            else if (occurrences[taskId] > 1)
            {
                duplicateTaskIds++;
                errors.Add($"Task {taskId}: Duplicate Task ID ({occurrences[taskId]} entries).");
                _rows[taskId].Status = "Duplicate";
            }
            else if (hashIsValid[taskId])
            {
                validSolutions++;
                _rows[taskId].Status = "Valid";
            }
            else _rows[taskId].Status = "Invalid";
        }

        bool passed = solutions.Count == expectedCount && validSolutions == expectedCount
            && invalidSolutions == 0 && missingSolutions == 0 && duplicateTaskIds == 0;
        ValidationOutput.Text = $"Validation: {(passed ? "PASSED" : "FAILED")}\n"
            + $"Valid solutions: {validSolutions} / 100\n"
            + $"Invalid solutions: {invalidSolutions}\n"
            + $"Missing solutions: {missingSolutions}\n"
            + $"Duplicate Task IDs: {duplicateTaskIds}"
            + (errors.Count == 0 ? "" : "\n\n" + string.Join("\n", errors));
        Message.Text = passed
            ? $"All 100 nonces independently verified for seed {seed} at difficulty {difficultyBits}."
            : "Validation failed. All detected problems are listed; no solutions were replaced or recalculated.";
    }
    // END OF INSTRUCTOR VALIDATOR - DO NOT MODIFY

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning || _closing || _sessionSeed is null) return;
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                seed = _sessionSeed,
                difficultyBits = _sessionDifficulty,
                solutions = _submittedSolutions
            }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Clipboard.SetText(json);
            Message.Text = $"Copied {_submittedSolutions.Count} solution entries and the original session parameters to the clipboard.";
        }
        catch (Exception error) { Message.Text = $"Could not copy results: {error.Message}"; }
    }

    private void UpdateControls()
    {
        bool idle = !_isRunning && !_closing;
        InputFields.IsEnabled = StartButton.IsEnabled = idle;
        CollectMeasurementsBox.IsEnabled = ClearHistoryButton.IsEnabled = idle;
        CancelButton.IsEnabled = _isRunning && !_closing && _cancellation?.IsCancellationRequested == false;
        ValidateButton.IsEnabled = idle && _sessionSeed is not null;
        CopyButton.IsEnabled = idle && _sessionSeed is not null && _submittedSolutions.Count > 0;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        _cancellation?.Cancel();
        UpdateControls();
        try
        {
            if (_runningTask is not null) await _runningTask;
        }
        catch { }
        finally
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                _allowClose = true;
                Close();
            }));
        }
    }
}
