namespace RRC.Abstractions;

public interface IArrivalModel
{
    TimeSpan GetNextDelay(TimeSpan currentTime, Random random);
}
