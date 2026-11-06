namespace RRC.Simulation;

public sealed class SimulationResult
{
    public SimulationScenario Scenario { get; }
    public SimulationOptions Options { get; }
    public SimulationMetrics Metrics { get; }
    public SimulationDiagnostics Diagnostics { get; }
    public IReadOnlyList<SimulationTraceEvent> TraceEvents { get; }

    public SimulationResult(
        SimulationScenario scenario,
        SimulationOptions options,
        SimulationMetrics metrics,
        SimulationDiagnostics diagnostics,
        IReadOnlyList<SimulationTraceEvent> traceEvents)
    {
        Scenario = scenario;
        Options = options;
        Metrics = metrics;
        Diagnostics = diagnostics;
        TraceEvents = traceEvents;
    }
}
