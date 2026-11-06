using SPS.Runner;
using System.Globalization;
using SPS.Abstractions;
using SPS.Simulation;
using SPS.Simulation.Public;
using SPS.Student;
using SPS.Telemetry;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
static ISystemPolicy CreateStudentPolicy() => new SampleSystemPolicy();
int seed = 42, traceCount = 0, stepMs = 0;
string? recording = null;
bool interactive = args.Length == 0 && !Console.IsInputRedirected;
try
{
    if (args.Length == 1 && args[0] is "--help" or "-h")
    {
        Console.WriteLine("Usage: SPS.Runner [--seed N] [--trace N] [--record path.jsonl] [--step-ms N]");
        return 0;
    }
    for (int i = 0; i < args.Length; i++)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}.");
        string option = args[i], value = args[++i];
        switch (option)
        {
            case "--seed": seed = int.Parse(value); break;
            case "--trace": traceCount = int.Parse(value); break;
            case "--record": recording = value; break;
            case "--step-ms": stepMs = int.Parse(value); if (stepMs <= 0) throw new ArgumentException("Step interval must be positive."); break;
            default: throw new ArgumentException($"Unknown option {option}.");
        }
    }
    if (interactive)
    {
        Console.Write("Seed [42]: ");
        var input = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(input)) seed = int.Parse(input);
        Console.Write("Trace request count [0]: ");
        input = Console.ReadLine();
        if (!string.IsNullOrWhiteSpace(input)) traceCount = int.Parse(input);
    }
    using var recorder = recording == null ? null : new TraceRecorder(recording);
    var scenario = PublicScenario.Create();
    var options = new SimulationOptions(seed, traceCount);
    SimulationResult result;
    if (stepMs == 0) result = new SimulationRunner().Run(scenario, CreateStudentPolicy(), options, recorder);
    else
    {
        var session = new SimulationSession(scenario, CreateStudentPolicy(), options, recorder);
        while (!session.IsCompleted)
            session.AdvanceTo(session.Now + TimeSpan.FromMilliseconds(stepMs) < scenario.FinalTime
                ? session.Now + TimeSpan.FromMilliseconds(stepMs) : scenario.FinalTime);
        result = session.GetResult();
    }
    ConsoleReport.Print(result);
    return 0;
}
catch (Exception e) { Console.Error.WriteLine($"Simulation error: {e.Message}"); return 1; }
finally { if (interactive) { Console.Write("Press Enter to close."); Console.ReadLine(); } }

