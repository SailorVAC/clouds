using System.Globalization;
using RRC.Runner;
using RRC.Simulation;
using RRC.Simulation.Public;
using RRC.Student;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
try
{
    //Selevt the policy here:
    RRC.Abstractions.IRequestPolicy requestPolicy = new AdaptiveRequestPolicy();

    var options = RunnerConsole.ReadOptions(args);
    if (options is null) return 0;
    var result = new SimulationRunner().Run(PublicScenario.Create(), requestPolicy, options);
    RunnerConsole.Print(result);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Simulation failed: {exception.Message}");
    return 1;
}
finally
{
    if (RunnerConsole.IsInteractive(args)) RunnerConsole.WaitForExit();
}
