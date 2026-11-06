using RRC.Simulation;

namespace RRC.Runner;

internal static class RunnerConsole
{
    public static bool IsInteractive(string[] args) =>
        args.Length == 0 && !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public static SimulationOptions? ReadOptions(string[] args)
    {
        var defaults = new SimulationOptions();
        if (IsInteractive(args))
        {
            Console.WriteLine("Press Enter to use the default value.");
            int seed = ReadInteger("Seed", defaults.Seed);
            int trace = ReadInteger("Requests to trace (0..100)", defaults.TraceRequestCount,
                0, SimulationOptions.MaximumTraceRequestCount);
            Console.WriteLine();
            return new SimulationOptions(seed, trace);
        }

        int seedValue = defaults.Seed;
        int traceValue = defaults.TraceRequestCount;
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            if (argument is "--help" or "-h")
            {
                Console.WriteLine($"Usage: dotnet run --project RRC.Runner -- [--seed <integer>] [--trace <0..{SimulationOptions.MaximumTraceRequestCount}>]");
                Console.WriteLine("Start without arguments to enter values interactively. Press Enter to accept defaults.");
                return null;
            }
            if (argument is not ("--seed" or "--trace"))
                throw new ArgumentException($"Unknown argument: {argument}. Use --help for usage.");
            if (++i >= args.Length || !int.TryParse(args[i], out int value))
                throw new ArgumentException($"{argument} requires an integer value.");
            if (argument == "--seed") seedValue = value;
            else if (value < 0 || value > SimulationOptions.MaximumTraceRequestCount)
                throw new ArgumentException($"--trace must be between 0 and {SimulationOptions.MaximumTraceRequestCount} requests.");
            else traceValue = value;
        }
        return new SimulationOptions(seedValue, traceValue);
    }

    public static void WaitForExit()
    {
        Console.WriteLine("\nPress Enter to close.");
        Console.ReadLine();
    }

    private static int ReadInteger(string label, int defaultValue, int minimum = int.MinValue, int maximum = int.MaxValue)
    {
        while (true)
        {
            Console.Write($"{label} [{defaultValue}]: ");
            string? input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input)) return defaultValue;
            if (int.TryParse(input, out int value) && value >= minimum && value <= maximum)
                return value;
            Console.WriteLine($"Enter an integer between {minimum} and {maximum}.");
        }
    }

    public static void Print(SimulationResult result)
    {
        var scenario = result.Scenario;
        Console.WriteLine($"Scenario: {scenario.Name}");
        Console.WriteLine($"Seed: {result.Options.Seed}");
        Console.WriteLine($"Request generation: {scenario.RequestGenerationDuration.TotalSeconds:0.###} s; simulation: {scenario.SimulationDuration.TotalSeconds:0.###} s");
        Console.WriteLine(scenario.Description);
        Console.WriteLine("\nPrimary metrics");
        var metrics = result.Metrics;
        Console.WriteLine($"Success rate:       {metrics.SuccessRate:P1}");
        Console.WriteLine($"Mean latency:       {Milliseconds(metrics.MeanLatency)}");
        Console.WriteLine($"p95 latency:        {Milliseconds(metrics.P95Latency)}");
        Console.WriteLine($"Attempts/request:   {metrics.AttemptsPerRequest:F3}");
        Console.WriteLine("\nDiagnostics");
        var diagnostics = result.Diagnostics;
        Console.WriteLine($"Requests:           {diagnostics.TotalLogicalRequests}");
        Console.WriteLine($"Successful:         {diagnostics.SuccessfulRequests}");
        Console.WriteLine($"Failed:             {diagnostics.FailedRequests}");
        Console.WriteLine($"Attempts:           {diagnostics.TotalAttempts}");
        Console.WriteLine($"Timeouts:           {diagnostics.TotalTimeouts}");
        Console.WriteLine($"Retries started:    {diagnostics.TotalRetries}");
        Console.WriteLine($"Late responses:     {diagnostics.LateResponses}");
        Console.WriteLine($"Max in-flight:      {diagnostics.MaximumInFlightAttempts}");
        Console.WriteLine($"Mean timeout:       {Milliseconds(diagnostics.MeanChosenTimeout)}");
        if (result.TraceEvents.Count == 0) return;
        Console.WriteLine("\nTrace");
        foreach (var item in result.TraceEvents)
        {
            string details = item.EventType switch
            {
                TraceEventType.RequestStarted => "REQUEST_STARTED",
                TraceEventType.AttemptStarted => $"ATTEMPT_STARTED timeout={Milliseconds(item.Details.Timeout)}, in-flight={item.Details.InFlightAttempts}",
                TraceEventType.Timeout => $"TIMEOUT after {Milliseconds(item.Details.Elapsed)}",
                TraceEventType.RetryScheduled => $"RETRY_SCHEDULED delay={Milliseconds(item.Details.RetryDelay)}",
                TraceEventType.ServerCompleted => $"SERVER_COMPLETED in-flight={item.Details.InFlightAttempts}",
                TraceEventType.Success => $"SUCCESS logical latency={Milliseconds(item.Details.Elapsed)}",
                TraceEventType.LateResponse => $"LATE_RESPONSE after {Milliseconds(item.Details.Elapsed)}",
                TraceEventType.RequestFailed => item.Details.SimulationEnded ? "REQUEST_FAILED simulation ended" : "REQUEST_FAILED no retry",
                _ => throw new ArgumentOutOfRangeException()
            };
            Console.WriteLine($"{item.Time.TotalSeconds,8:F3}s  request {item.RequestId} attempt {item.Attempt}  {details}");
        }
    }

    private static string Milliseconds(TimeSpan? value) => value.HasValue ? $"{value.Value.TotalMilliseconds:F2} ms" : "n/a";
}
