namespace RRC.Abstractions;

public interface ILoadModel
{
    // The count includes the attempt now entering server processing.
    TimeSpan ApplyLoad(TimeSpan baseLatency, int inFlightAttempts);
}
