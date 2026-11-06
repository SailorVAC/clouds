namespace RRC.Abstractions;

public interface ILatencyModel
{
    TimeSpan GetBaseLatency(TimeSpan currentTime, Random random);
}
