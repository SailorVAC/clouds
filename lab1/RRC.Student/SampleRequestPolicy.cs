using RRC.Abstractions;

namespace RRC.Student;

public sealed class SampleRequestPolicy : IRequestPolicy
{
    public TimeSpan GetTimeout(RequestContext context, Random random) => TimeSpan.FromMilliseconds(120);

    public RetryDecision OnResult(RequestObservation observation, Random random)
    {
        if (observation.Outcome == RequestOutcome.Timeout && observation.Attempt == 1)
            return RetryDecision.RetryAfter(TimeSpan.FromMilliseconds(100));

        return RetryDecision.NoRetry();
    }
}
