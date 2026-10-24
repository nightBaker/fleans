using Fleans.Application.Observability;

namespace Fleans.Api.Admission;

/// <summary>
/// A concurrency limit on in-flight REST mutations, sized by AIMD on their latency (#922). Past the
/// CPU ceiling the cluster stops completing instances instead of slowing down, and the rate it can
/// sustain under contention is lower than the peak it reaches on an idle start — so a fixed rate
/// limit has to sit well below the ceiling. This one follows the cluster instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Admission.</b> A request is admitted while fewer than <see cref="Limit"/> requests are in
/// flight; otherwise it is rejected at once (no queue). By Little's law the admitted rate is at
/// most <c>Limit / latency</c>, so a shrinking limit sheds load exactly when the cluster slows.
/// </para>
/// <para>
/// <b>Adjustment</b>, once per <see cref="AdmissionOptions.WindowMs"/>, on the requests that
/// finished in the window: p95 latency over <see cref="AdmissionOptions.TargetLatencyMs"/> (an
/// overload failure counts as infinitely slow) → the limit is multiplied by
/// <see cref="DecreaseFactor"/>; otherwise, if the window used at least half the limit, it grows by
/// one. An idle limit does not grow, so it cannot drift far above what the cluster has shown it can
/// take. The window closes on the first completion after it elapsed — no timer.
/// </para>
/// </remarks>
public sealed partial class AdaptiveConcurrencyLimiter
{
    /// <summary>Multiplicative decrease per window that missed the target.</summary>
    public const double DecreaseFactor = 0.75;

    private readonly AdmissionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AdaptiveConcurrencyLimiter> _logger;
    private readonly Lock _lock = new();
    private readonly List<double> _windowLatenciesMs = [];

    private int _limit;
    private int _inFlight;
    private int _windowMaxInFlight;
    private long _windowStart;

    public AdaptiveConcurrencyLimiter(AdmissionOptions options, TimeProvider time, ILogger<AdaptiveConcurrencyLimiter> logger)
    {
        _options = options;
        _time = time;
        _logger = logger;
        _limit = options.InitialLimit;
        _windowStart = time.GetTimestamp();
        FleansDiagnostics.RecordAdmissionLimit(_limit);
    }

    /// <summary>Requests admitted at the same time, now.</summary>
    public int Limit
    {
        get { lock (_lock) return _limit; }
    }

    /// <summary>Requests admitted and not yet released.</summary>
    public int InFlight
    {
        get { lock (_lock) return _inFlight; }
    }

    /// <summary>Admits a request if one is free; every <c>true</c> must be paired with one <see cref="Release"/>.</summary>
    public bool TryAcquire()
    {
        int limit;
        lock (_lock)
        {
            if (_inFlight < _limit)
            {
                _inFlight++;
                _windowMaxInFlight = Math.Max(_windowMaxInFlight, _inFlight);
                return true;
            }
            limit = _limit;
        }

        FleansDiagnostics.OnAdmissionRejected();
        LogRejected(limit);
        return false;
    }

    /// <summary>
    /// Releases an admitted request and records how it went: its <paramref name="latency"/>, and
    /// whether it failed because the cluster is overloaded (a timeout, a shedding gateway).
    /// </summary>
    public void Release(TimeSpan latency, bool overloaded)
    {
        int oldLimit, newLimit;
        double p95Ms;
        int samples;
        lock (_lock)
        {
            _inFlight--;
            _windowLatenciesMs.Add(overloaded ? double.PositiveInfinity : latency.TotalMilliseconds);

            var now = _time.GetTimestamp();
            if (_time.GetElapsedTime(_windowStart, now).TotalMilliseconds < _options.WindowMs)
                return;

            samples = _windowLatenciesMs.Count;
            p95Ms = Percentile95(_windowLatenciesMs);
            oldLimit = _limit;
            if (p95Ms > _options.TargetLatencyMs)
                _limit = Math.Max(_options.MinLimit, (int)(_limit * DecreaseFactor));
            else if (_windowMaxInFlight * 2 >= _limit)
                _limit = Math.Min(_options.MaxLimit, _limit + 1);
            newLimit = _limit;

            _windowLatenciesMs.Clear();
            _windowMaxInFlight = _inFlight;
            _windowStart = now;
        }

        if (newLimit == oldLimit) return;
        FleansDiagnostics.RecordAdmissionLimit(newLimit);
        if (newLimit < oldLimit)
            LogLimitLowered(oldLimit, newLimit, p95Ms, _options.TargetLatencyMs, samples);
        else
            LogLimitRaised(oldLimit, newLimit, p95Ms, samples);
    }

    private static double Percentile95(List<double> values)
    {
        values.Sort();
        var index = (int)Math.Ceiling(values.Count * 0.95) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }

    [LoggerMessage(EventId = 8300, Level = LogLevel.Warning,
        Message = "Admission limit lowered from {OldLimit} to {NewLimit}: p95 latency {P95Ms} ms over the {TargetMs} ms target ({Samples} requests)")]
    private partial void LogLimitLowered(int oldLimit, int newLimit, double p95Ms, int targetMs, int samples);

    [LoggerMessage(EventId = 8301, Level = LogLevel.Debug,
        Message = "Admission limit raised from {OldLimit} to {NewLimit}: p95 latency {P95Ms} ms ({Samples} requests)")]
    private partial void LogLimitRaised(int oldLimit, int newLimit, double p95Ms, int samples);

    [LoggerMessage(EventId = 8302, Level = LogLevel.Debug,
        Message = "Request rejected with 429: {Limit} requests already in flight")]
    private partial void LogRejected(int limit);
}
