using RRC.Abstractions;

namespace RRC.Simulation;

public sealed class SimulationRunner
{
    public SimulationResult Run(SimulationScenario scenario, IRequestPolicy policy, SimulationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(policy);
        scenario.Validate();
        options ??= new SimulationOptions();
        if (options.TraceRequestCount < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Trace request count cannot be negative.");
        if (options.TraceRequestCount > SimulationOptions.MaximumTraceRequestCount)
            throw new ArgumentOutOfRangeException(nameof(options), $"Trace request count cannot exceed {SimulationOptions.MaximumTraceRequestCount}.");
        if (options.MaximumEventCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum event count must be positive.");

        return new RunState(scenario, policy, options).Execute();
    }

    private enum EventType { NewLogicalRequest, StartAttempt, AttemptTimeout, ServerProcessingComplete }

    private sealed class LogicalRequest
    {
        public long Id { get; }
        public TimeSpan CreatedAt { get; }
        public int LastAttempt { get; set; }

        public LogicalRequest(long id, TimeSpan createdAt)
        {
            Id = id;
            CreatedAt = createdAt;
        }
    }

    private sealed class Attempt
    {
        public LogicalRequest Request { get; }
        public int Number { get; }
        public TimeSpan StartedAt { get; }
        public TimeSpan Timeout { get; }
        public bool Observed { get; set; }
        public bool TimedOut { get; set; }

        public Attempt(LogicalRequest request, int number, TimeSpan startedAt, TimeSpan timeout)
        {
            Request = request;
            Number = number;
            StartedAt = startedAt;
            Timeout = timeout;
        }
    }

    private sealed class ScheduledEvent
    {
        public EventType Type { get; }
        public LogicalRequest? Request { get; }
        public Attempt? Attempt { get; }

        public ScheduledEvent(EventType type, LogicalRequest? request = null, Attempt? attempt = null)
        {
            Type = type;
            Request = request;
            Attempt = attempt;
        }
    }

    private sealed class RunState
    {
        private readonly SimulationScenario scenario;
        private readonly IRequestPolicy policy;
        private readonly SimulationOptions options;
        private readonly Random arrivalRandom;
        private readonly Random latencyRandom;
        private readonly Random policyRandom;
        private readonly PriorityQueue<ScheduledEvent, (long Ticks, long Sequence)> events = new();
        private readonly Dictionary<long, LogicalRequest> pendingRequests = new();
        private readonly List<long> successfulLatencies = new();
        private readonly List<SimulationTraceEvent> trace = new();
        private TimeSpan now;
        private long sequence;
        private long totalRequests;
        private long totalAttempts;
        private long timeouts;
        private long retries;
        private long lateResponses;
        private int inFlight;
        private int maximumInFlight;
        private decimal timeoutTicks;

        public RunState(SimulationScenario scenario, IRequestPolicy policy, SimulationOptions options)
        {
            this.scenario = scenario;
            this.policy = policy;
            this.options = options;
            // Separate streams keep the arrival schedule independent of retry count.
            arrivalRandom = new Random(options.Seed);
            latencyRandom = new Random(unchecked(options.Seed ^ 0x5f3759df));
            // Policy random draws do not consume values from either environment stream.
            policyRandom = new Random(unchecked(options.Seed ^ 0x6a09e667));
        }

        public SimulationResult Execute()
        {
            if (scenario.RequestGenerationDuration > TimeSpan.Zero)
                Schedule(TimeSpan.Zero, new(EventType.NewLogicalRequest));

            long processed = 0;
            while (events.TryDequeue(out var next, out var priority))
            {
                if (++processed > options.MaximumEventCount)
                    throw new InvalidOperationException(
                        $"Simulation exceeded the technical event limit ({options.MaximumEventCount}) at {now.TotalMilliseconds}ms. Check arrival intervals, timeouts, and retry behavior.");

                now = TimeSpan.FromTicks(priority.Ticks);
                switch (next.Type)
                {
                    case EventType.NewLogicalRequest: NewRequest(); break;
                    case EventType.StartAttempt: StartAttempt(next.Request!); break;
                    case EventType.AttemptTimeout: Timeout(next.Attempt!); break;
                    case EventType.ServerProcessingComplete: Complete(next.Attempt!); break;
                }
            }

            now = scenario.SimulationDuration;
            foreach (var request in pendingRequests.Values.OrderBy(request => request.Id))
                AddTrace(request, request.LastAttempt, TraceEventType.RequestFailed, new(simulationEnded: true));

            successfulLatencies.Sort();
            int successes = successfulLatencies.Count;
            TimeSpan? mean = successes == 0 ? null : TimeSpan.FromTicks(
                (long)(successfulLatencies.Sum(ticks => (decimal)ticks) / successes));
            TimeSpan? p95 = successes == 0 ? null : TimeSpan.FromTicks(
                successfulLatencies[(int)Math.Ceiling(successes * 0.95) - 1]);

            var metrics = new SimulationMetrics(
                totalRequests == 0 ? 0 : (double)successes / totalRequests,
                mean, p95, totalRequests == 0 ? 0 : (double)totalAttempts / totalRequests);
            var diagnostics = new SimulationDiagnostics(
                totalRequests, successes, totalRequests - successes, totalAttempts, timeouts,
                retries, lateResponses, maximumInFlight,
                totalAttempts == 0 ? null : TimeSpan.FromTicks((long)(timeoutTicks / totalAttempts)));
            return new(scenario, options, metrics, diagnostics, trace.AsReadOnly());
        }

        private void NewRequest()
        {
            var request = new LogicalRequest(totalRequests++, now);
            pendingRequests.Add(request.Id, request);
            AddTrace(request, 0, TraceEventType.RequestStarted, new());
            Schedule(TimeSpan.Zero, new(EventType.StartAttempt, request));

            var delay = scenario.ArrivalModel.GetNextDelay(now, arrivalRandom);
            if (delay <= TimeSpan.Zero)
                throw new InvalidOperationException($"Arrival model returned a nonpositive delay at {now}: {delay}.");
            if (delay < scenario.RequestGenerationDuration - now)
                Schedule(delay, new(EventType.NewLogicalRequest));
        }

        private void StartAttempt(LogicalRequest request)
        {
            int number = checked(++request.LastAttempt);
            var timeout = policy.GetTimeout(new(request.Id, number), policyRandom);
            if (timeout <= TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"Request {request.Id} attempt {number} returned invalid timeout: {timeout.TotalMilliseconds}ms. Timeout must be positive.");

            var baseLatency = scenario.LatencyModel.GetBaseLatency(now, latencyRandom);
            if (baseLatency < TimeSpan.Zero)
                throw new InvalidOperationException($"Latency model returned negative latency for request {request.Id} attempt {number}.");
            var latency = scenario.LoadModel.ApplyLoad(baseLatency, checked(inFlight + 1));
            if (latency < TimeSpan.Zero)
                throw new InvalidOperationException($"Load model returned negative latency for request {request.Id} attempt {number}.");

            var attempt = new Attempt(request, number, now, timeout);
            totalAttempts++;
            if (number > 1) retries++;
            timeoutTicks += timeout.Ticks;
            inFlight++;
            maximumInFlight = Math.Max(maximumInFlight, inFlight);
            AddTrace(request, number, TraceEventType.AttemptStarted, new(timeout: timeout, inFlightAttempts: inFlight));

            // Freeze effective latency at entry. Completion wins an exact timeout tie.
            Schedule(latency, new(EventType.ServerProcessingComplete, attempt: attempt));
            Schedule(timeout, new(EventType.AttemptTimeout, attempt: attempt));
        }

        private void Complete(Attempt attempt)
        {
            inFlight--;
            AddTrace(attempt.Request, attempt.Number, TraceEventType.ServerCompleted, new(inFlightAttempts: inFlight));
            if (attempt.TimedOut)
            {
                lateResponses++;
                AddTrace(attempt.Request, attempt.Number, TraceEventType.LateResponse, new(elapsed: now - attempt.StartedAt));
                return;
            }

            attempt.Observed = true;
            pendingRequests.Remove(attempt.Request.Id);
            var logicalLatency = now - attempt.Request.CreatedAt;
            successfulLatencies.Add(logicalLatency.Ticks);
            AddTrace(attempt.Request, attempt.Number, TraceEventType.Success, new(elapsed: logicalLatency));
            Observe(attempt, RequestOutcome.Success);
        }

        private void Timeout(Attempt attempt)
        {
            if (attempt.Observed) return;
            attempt.Observed = true;
            attempt.TimedOut = true;
            timeouts++;
            // Server work remains in flight until ServerProcessingComplete.
            AddTrace(attempt.Request, attempt.Number, TraceEventType.Timeout, new(elapsed: attempt.Timeout));
            var decision = Observe(attempt, RequestOutcome.Timeout);
            if (decision.Retry)
            {
                AddTrace(attempt.Request, attempt.Number, TraceEventType.RetryScheduled, new(retryDelay: decision.Delay));
                Schedule(decision.Delay, new(EventType.StartAttempt, attempt.Request));
            }
            else
            {
                pendingRequests.Remove(attempt.Request.Id);
                AddTrace(attempt.Request, attempt.Number, TraceEventType.RequestFailed, new());
            }
        }

        private RetryDecision Observe(Attempt attempt, RequestOutcome outcome)
        {
            var decision = policy.OnResult(new(attempt.Request.Id, attempt.Number, outcome, now - attempt.StartedAt), policyRandom);
            if (decision is null || decision.Delay < TimeSpan.Zero)
                throw new InvalidOperationException(
                    $"Request {attempt.Request.Id} attempt {attempt.Number} returned invalid retry decision: {decision}. Delay must be nonnegative.");
            return decision;
        }

        private void Schedule(TimeSpan delay, ScheduledEvent next)
        {
            // Comparing before addition also handles delays as large as TimeSpan.MaxValue.
            if (delay > scenario.SimulationDuration - now) return;
            events.Enqueue(next, ((now + delay).Ticks, checked(sequence++)));
        }

        private void AddTrace(LogicalRequest request, int attempt, TraceEventType type, TraceDetails details)
        {
            if (request.Id < options.TraceRequestCount)
                trace.Add(new(now, request.Id, attempt, type, details));
        }
    }
}
