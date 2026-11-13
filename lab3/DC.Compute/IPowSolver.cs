namespace DC.Compute;

public interface IPowSolver
{
    Task<PowResult> SolveAsync(string seed, int difficultyBits, int workerCount,
        IProgress<PowProgress>? progress, CancellationToken cancellationToken, PowTelemetry? telemetry = null);
}
