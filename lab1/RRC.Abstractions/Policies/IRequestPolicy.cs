namespace RRC.Abstractions;

public interface IRequestPolicy
{
    // Both methods receive the same seeded random source for the entire run.
    TimeSpan GetTimeout(RequestContext context, Random random);
    RetryDecision OnResult(RequestObservation observation, Random random);
}
