namespace RRC.Simulation.Public;

public static class PublicScenario
{
    public static SimulationScenario Create()
    {
        var arrivals = new ConstantRateArrivalModel(100);
        var latency = new SimpleRandomLatencyModel(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150));
        var load = new CapacityLoadModel(20);
        string description = FormattableString.Invariant($"Arrivals: {arrivals.RequestsPerSecond:0.###} requests/s\nBase latency: uniform {latency.Minimum.TotalMilliseconds:0.###}..{latency.Maximum.TotalMilliseconds:0.###} ms\nCapacity: {load.Capacity}; latency multiplier: max(1, in-flight / capacity)");

        return new SimulationScenario(
            "Public workload",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(40),
            arrivals,
            latency,
            load,
            description);
    }
}
