using RRC.Abstractions;

namespace RRC.Simulation;

public sealed class SimulationScenario
{
    public string Name { get; }
    public TimeSpan RequestGenerationDuration { get; }
    public TimeSpan SimulationDuration { get; }
    public IArrivalModel ArrivalModel { get; }
    public ILatencyModel LatencyModel { get; }
    public ILoadModel LoadModel { get; }
    public string Description { get; }

    public SimulationScenario(
        string name,
        TimeSpan requestGenerationDuration,
        TimeSpan simulationDuration,
        IArrivalModel arrivalModel,
        ILatencyModel latencyModel,
        ILoadModel loadModel,
        string description)
    {
        Name = name;
        RequestGenerationDuration = requestGenerationDuration;
        SimulationDuration = simulationDuration;
        ArrivalModel = arrivalModel;
        LatencyModel = latencyModel;
        LoadModel = loadModel;
        Description = description;
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentNullException.ThrowIfNull(ArrivalModel);
        ArgumentNullException.ThrowIfNull(LatencyModel);
        ArgumentNullException.ThrowIfNull(LoadModel);
        if (RequestGenerationDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestGenerationDuration), "Generation duration cannot be negative.");
        if (SimulationDuration <= RequestGenerationDuration)
            throw new ArgumentOutOfRangeException(nameof(SimulationDuration), "Simulation duration must exceed request generation duration.");
    }
}
