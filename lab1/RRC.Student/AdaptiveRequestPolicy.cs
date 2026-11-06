using RRC.Abstractions;

namespace RRC.Student;

/// <summary>
/// Адаптивная политика тайм-аутов и повторных попыток.
///
/// Идея.
/// Тайм-аут не прерывает работу сервера: прерванная попытка продолжает занимать
/// ёмкость до фактического завершения, а повторная попытка добавляет к этой
/// нагрузке ещё одну. Поэтому тайм-аут нужен только для того, чтобы признать
/// попытку безнадёжной и получить шанс на более быстрый повтор. Слишком короткий
/// тайм-аут превращает нормальные запросы в отказы и дополнительно грузит сервер
/// (положительная обратная связь по нагрузке), слишком длинный - не успевает
/// отсечь редкие "тяжёлые" ответы.
///
/// Решение состоит из трёх частей.
///  1. Оценка времени ответа. Хранится окно последних успешных попыток, из него
///     берётся квантиль 0.95, умноженный на запас. Квантиль, в отличие от
///     среднего, устойчив к длинному "хвосту" распределения задержек.
///  2. Множитель терпения. Растёт при каждом тайм-ауте и медленно спадает при
///     успехах, поэтому политика быстро перестаёт отсекать запросы, если
///     задержки выросли (например, выросла нагрузка), и не "застревает" на
///     устаревшей оценке.
///  3. Повторы. Разрешены, только пока система выглядит здоровой: доля
///     тайм-аутов невелика. В перегруженном режиме повтор лишь добавляет
///     нагрузку и ухудшает положение остальных запросов, поэтому там политика
///     просто ждёт ответ и не повторяет запрос.
///
/// Пока наблюдений мало, политика не ставит тайм-аут вообще (возвращает верхнюю
/// границу): без данных любое отсечение - угадывание, а ожидание никогда не
/// хуже, чем отказ.
///
/// Экземпляр политики один на весь прогон, поэтому наблюдения одних запросов
/// влияют на тайм-ауты других. Генератор случайных чисел не используется:
/// поведение полностью детерминировано наблюдениями.
/// </summary>
public sealed class AdaptiveRequestPolicy : IRequestPolicy
{
    /// <summary>Сколько последних успешных попыток учитывается в оценке.</summary>
    private const int SampleWindow = 256;

    /// <summary>Квантиль распределения задержек, который берётся за основу тайм-аута.</summary>
    private const double LatencyQuantile = 0.95;

    /// <summary>Запас поверх квантиля: покрывает разброс и запаздывание оценки.</summary>
    private const double SafetyFactor = 2.5;

    /// <summary>Сколько попыток нужно пронаблюдать, прежде чем доверять своей оценке.</summary>
    private const int WarmupAttempts = 128;

    /// <summary>Через сколько новых наблюдений пересчитывать квантиль.</summary>
    private const int EstimateRefreshStep = 8;

    /// <summary>Предельное число попыток на один логический запрос.</summary>
    private const int MaxAttempts = 3;

    /// <summary>Во сколько раз растёт тайм-аут на каждой следующей попытке.</summary>
    private const double RetryTimeoutGrowth = 2.0;

    /// <summary>Рост множителя терпения при тайм-ауте и его затухание при успехе.</summary>
    private const double PatienceGrowth = 1.5;
    private const double PatienceDecay = 0.98;
    private const double MaxPatience = 64.0;

    /// <summary>Сглаживание оценки доли тайм-аутов (память около 100 попыток).</summary>
    private const double TimeoutRateSmoothing = 0.01;

    /// <summary>Доля тайм-аутов, выше которой система считается перегруженной.</summary>
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
        // Пока данных мало, оценивать нечего: ждём ответ, а не отсекаем запрос.
        double estimate = _sampleCount < WarmupAttempts ? MaximumTimeout.Ticks : CurrentEstimate();

        // Множитель терпения и рост тайм-аута для повторных попыток: у повтора
        // должен быть реальный шанс уложиться.
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
            // Оценка оказалась слишком оптимистичной - становимся терпеливее.
            _patience = Math.Min(_patience * PatienceGrowth, MaxPatience);
            return CanRetry(observation.Attempt)
                ? RetryDecision.RetryAfter(TimeSpan.Zero)
                : RetryDecision.NoRetry();
        }

        Remember(Math.Max(1.0, observation.Elapsed.Ticks));
        _patience = Math.Max(1.0, _patience * PatienceDecay);
        return RetryDecision.NoRetry();
    }

    /// <summary>Повтор разрешён, только если система не выглядит перегруженной.</summary>
    private bool CanRetry(int attempt)
    {
        if (attempt >= MaxAttempts) return false;
        if (_timeoutRate > RetryTimeoutRateLimit) return false;
        return true;
    }

    /// <summary>Тайм-аут растёт с номером попытки, чтобы у повтора был запас времени.</summary>
    private static double GrowthForAttempt(int attempt)
    {
        if (attempt <= 1) return 1.0;
        if (attempt >= 8) return 256.0;
        return Math.Pow(RetryTimeoutGrowth, attempt - 1);
    }

    /// <summary>Оценка времени ответа: квантиль окна успешных задержек с запасом.</summary>
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
