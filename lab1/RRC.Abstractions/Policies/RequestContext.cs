namespace RRC.Abstractions;

public sealed class RequestContext
{
    public long RequestId { get; }
    public int Attempt { get; }

    public RequestContext(
        long requestId,
        int attempt)
    {
        RequestId = requestId;
        Attempt = attempt;
    }
}
