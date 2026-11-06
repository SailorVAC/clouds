using System;
using System.Collections.Generic;
using System.Linq;
using SPS.Abstractions;

namespace SPS.Student
{
    public class MySystemPolicy : ISystemPolicy
    {
        private int _targetApiCount = 2;
        private int _targetWorkerCount = 2;
        
        // Подключаем вашу адаптивную политику повторов из первой лабы
        public IRequestPolicy RpcPolicy { get; } = new AdaptiveRequestPolicy();

        // ---------------------------------------------------------
        // 1. УПРАВЛЕНИЕ КЭШОМ
        // ---------------------------------------------------------
        public CacheReadDecision OnCacheRead(CacheReadContext context, Random random)
        {
            if (context.Entry != null)
            {
                var age = context.Now - context.Entry.SourceObservedAt;
                if (age <= context.Request.MaxDataAge)
                {
                    return CacheReadDecision.ServeCached();
                }
            }

            if (context.PendingFetches != null && context.PendingFetches.Count > 0)
            {
                return CacheReadDecision.JoinFetch(context.PendingFetches[0].Id);
            }

            return CacheReadDecision.StartFetch();
        }

        public CacheWriteDecision OnCacheFill(CacheFillContext context, Random random)
        {
            var ttl = TimeSpan.FromSeconds(5);
            var keysToEvict = new List<string>();

            long currentSize = context.Entries.Sum(e => e.SizeBytes) + context.Value.SizeBytes;
            if (currentSize > context.CapacityBytes)
            {
                var sortedByAge = context.Entries.OrderBy(e => e.SourceObservedAt).ToList();
                foreach (var entry in sortedByAge)
                {
                    keysToEvict.Add(entry.Key);
                    currentSize -= entry.SizeBytes;
                    if (currentSize <= context.CapacityBytes)
                        break;
                }
            }

            return CacheWriteDecision.Retain(ttl, keysToEvict.ToArray());
        }

        // ---------------------------------------------------------
        // 2. МАРШРУТИЗАЦИЯ И API
        // ---------------------------------------------------------
        public EndpointSelectionDecision SelectEndpoint(RoutingContext context, Random random)
        {
            var availableApis = context.Endpoints.Where(e => e.State == InstanceState.Ready).ToList();
            
            if (!availableApis.Any())
                return null;

            var bestApi = availableApis.OrderBy(e => e.OutstandingCalls).First();
            return EndpointSelectionDecision.Assign(bestApi.Id);
        }

        public EndpointDecision ReviewEndpoint(EndpointContext context, Random random)
        {
            var recentFailures = context.RecentResults.Count(c => c.Outcome != RequestOutcome.Success);
            
            if (recentFailures > 2)
            {
                return EndpointDecision.Probe();
            }

            return EndpointDecision.Allow();
        }

        // ---------------------------------------------------------
        // 3. УПРАВЛЕНИЕ ЗАДАНИЯМИ И WORKER'АМИ
        // ---------------------------------------------------------
        public JobAssignmentDecision SelectJobAssignment(JobSchedulingContext context, Random random)
        {
            if (!context.QueuedJobs.Any() || !context.AvailableWorkers.Any())
                return null;

            var availableWorkers = context.AvailableWorkers.Where(w => w.State == InstanceState.Ready && w.Slots > w.AssignedJobs).ToList();
            
            if (!availableWorkers.Any())
                return null;

            var bestWorker = availableWorkers.OrderByDescending(w => w.Slots - w.AssignedJobs).First();
            var bestJob = context.QueuedJobs.OrderByDescending(j => j.Request.Revenue).First();

            return JobAssignmentDecision.Assign(bestJob.Id, bestWorker.Id);
        }

        public JobRecoveryDecision ReviewJob(JobRecoveryContext context, Random random)
        {
            if (context.Job.LastHeartbeat.HasValue && (context.Now - context.Job.LastHeartbeat.Value).TotalSeconds > 5)
            {
                return JobRecoveryDecision.RequeueAfter(TimeSpan.Zero);
            }
            
            return JobRecoveryDecision.KeepWaiting();
        }

        // ---------------------------------------------------------
        // 4. МАСШТАБИРОВАНИЕ И БЮДЖЕТ (Capacity)
        // ---------------------------------------------------------
        public CapacityPlan ReviewCapacity(CapacityContext context, Random random)
        {
            int currentApis = context.Instances.Count(e => e.Role == InstanceRole.Api);
            int currentWorkers = context.Instances.Count(e => e.Role == InstanceRole.Worker);

            if (context.PendingCalls > currentApis * 5 && _targetApiCount < context.Settings.MaximumApis)
            {
                _targetApiCount++;
            }
            else if (context.PendingCalls == 0 && currentApis > 2)
            {
                _targetApiCount--;
            }

            if (context.QueuedJobs.Count > currentWorkers * 2 && _targetWorkerCount < context.Settings.MaximumWorkers)
            {
                _targetWorkerCount++;
            }
            else if (context.QueuedJobs.Count == 0 && currentWorkers > 2)
            {
                _targetWorkerCount--;
            }

            return new CapacityPlan(_targetApiCount, _targetWorkerCount);
        }

        // ---------------------------------------------------------
        // 5. ОГРАНИЧЕНИЕ ВРЕМЕНИ ОБРАБОТКИ
        // ---------------------------------------------------------
        public TimeSpan? GetProcessingBudget(RequestInfo request, Random random)
        {
            switch (request.Kind)
            {
                case RequestKind.Metadata: return TimeSpan.FromSeconds(1.5);
                case RequestKind.Preview: return TimeSpan.FromSeconds(3.0);
                case RequestKind.Generate: return TimeSpan.FromSeconds(60.0);
                default: return null;
            }
        }

        // ---------------------------------------------------------
        // 6. НАБЛЮДЕНИЕ
        // ---------------------------------------------------------
        public void Observe(SystemObservation observation)
        {
        }


        // =========================================================
        // АДАПТИВНАЯ ПОЛИТИКА ИЗ ПЕРВОЙ ЛАБОРАТОРНОЙ
        // =========================================================
        public sealed class AdaptiveRequestPolicy : IRequestPolicy
        {
            private const int SampleWindow = 256;
            private const double LatencyQuantile = 0.95;
            private const double SafetyFactor = 2.5;
            private const int WarmupAttempts = 128;
            private const int EstimateRefreshStep = 8;
            private const int MaxAttempts = 3;
            private const double RetryTimeoutGrowth = 2.0;

            private const double PatienceGrowth = 1.5;
            private const double PatienceDecay = 0.98;
            private const double MaxPatience = 64.0;

            private const double TimeoutRateSmoothing = 0.01;
            private const double RetryTimeoutRateLimit = 0.15;

            private static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(20);
            private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(2);

            private readonly double[] _latencies = new double[SampleWindow];
            private readonly double[] _sorted = new double[SampleWindow];
            private int _sampleCount;
            private int _nextSample;
            private int _samplesSinceEstimate = EstimateRefreshStep;
            private double _estimate;
            private double _patience = 1.0;
            private double _timeoutRate;

            public TimeSpan GetTimeout(RequestContext context, Random random)
            {
                double estimate = _sampleCount < WarmupAttempts ? MaximumTimeout.Ticks : CurrentEstimate();
                estimate *= _patience * GrowthForAttempt(context.Attempt);

                if (double.IsNaN(estimate) || estimate < MinimumTimeout.Ticks) estimate = MinimumTimeout.Ticks;
                if (estimate > MaximumTimeout.Ticks) estimate = MaximumTimeout.Ticks;
                return TimeSpan.FromTicks((long)estimate);
            }

            public RetryDecision OnResult(RequestObservation observation, Random random)
            {
                bool timedOut = observation.Outcome == RequestOutcome.Timeout;
                _timeoutRate += ((timedOut ? 1.0 : 0.0) - _timeoutRate) * TimeoutRateSmoothing;

                if (timedOut)
                {
                    _patience = Math.Min(_patience * PatienceGrowth, MaxPatience);
                    return CanRetry(observation.Attempt)
                        ? RetryDecision.RetryAfter(TimeSpan.Zero)
                        : RetryDecision.NoRetry();
                }

                Remember(Math.Max(1.0, observation.Elapsed.Ticks));
                _patience = Math.Max(1.0, _patience * PatienceDecay);
                return RetryDecision.NoRetry();
            }

            private bool CanRetry(int attempt)
            {
                if (attempt >= MaxAttempts) return false;
                if (_timeoutRate > RetryTimeoutRateLimit) return false;
                return true;
            }

            private static double GrowthForAttempt(int attempt)
            {
                if (attempt <= 1) return 1.0;
                if (attempt >= 8) return 256.0;
                return Math.Pow(RetryTimeoutGrowth, attempt - 1);
            }

            private double CurrentEstimate()
            {
                if (_samplesSinceEstimate >= EstimateRefreshStep)
                {
                    Array.Copy(_latencies, _sorted, _sampleCount);
                    Array.Sort(_sorted, 0, _sampleCount);
                    int index = (int)Math.Ceiling(LatencyQuantile * _sampleCount) - 1;
                    if (index < 0) index = 0;
                    _estimate = _sorted[index] * SafetyFactor;
                    _samplesSinceEstimate = 0;
                }
                return _estimate;
            }

            private void Remember(double ticks)
            {
                _latencies[_nextSample] = ticks;
                _nextSample = (_nextSample + 1) % SampleWindow;
                if (_sampleCount < SampleWindow) _sampleCount++;
                _samplesSinceEstimate++;
            }
        }
    }
}