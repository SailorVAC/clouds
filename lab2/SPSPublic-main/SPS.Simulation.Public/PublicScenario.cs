using SPS.Abstractions;
using SPS.Simulation;

namespace SPS.Simulation.Public;

public static class PublicScenario
{
    public static SystemSettings CreateSettings(bool advanced = false) => new(
        new(4, 32, 1, .12m, 1, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2)),
        new(1, 0, 1, .30m, 2, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)),
        advanced ? 8 : 6, advanced ? 8 : 5, advanced ? 20 : 12,
        advanced ? 196_608 : 262_144, 500, 2, 64, TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(250));
    public static IReadOnlyList<RequestProfile> CreateProfiles(bool advanced = false) => Array.AsReadOnly(new[]
    {
        new RequestProfile(RequestKind.Metadata, .05m, .5, 1.5, 0, .015, 1, 1, 2, 1024),
        new RequestProfile(RequestKind.Preview, .20m, 1, 3, .015, .060, 1, 1, 5, 16384),
        new RequestProfile(RequestKind.Generate, advanced ? 5m : 3.5m, 15, advanced ? 60 : 35,
            .010, .120, advanced ? 2 : 1.5, advanced ? 12 : 5, 1, 1024)
    });
    public static SimulationScenario Create() => new("Public integrated workload", TimeSpan.FromSeconds(180),
        CreateSettings(), CreateProfiles(), Array.AsReadOnly(new[]
        {
            new WorkloadPhase(0, 12, .05, .30, .85),
            new WorkloadPhase(40, 36, .08, .35, .96),
            new WorkloadPhase(70, 18, .06, .30, .50, 30),
            new WorkloadPhase(110, 24, .10, .20, .85, 50),
            new WorkloadPhase(150, 5, .03, .30, .80, 50)
        }), Array.AsReadOnly(new[]
        {
            new FaultInterval(45, 49, FaultKind.Crash, InstanceRole.Api, 1),
            new FaultInterval(98, 102, FaultKind.Crash, InstanceRole.Worker, 1)
        })) { ResourceKeyCount = 80, ResponseLossProbability = .005 };
}
