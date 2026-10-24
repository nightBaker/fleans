using Fleans.Api.Admission;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleans.Api.Tests;

[TestClass]
public class AdaptiveConcurrencyLimiterTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(2);

    private ManualTimeProvider _time = null!;

    [TestInitialize]
    public void Setup() => _time = new ManualTimeProvider();

    private AdaptiveConcurrencyLimiter Create(int initial = 100, int min = 8, int max = 1024) =>
        new(new AdmissionOptions
        {
            InitialLimit = initial,
            MinLimit = min,
            MaxLimit = max,
            TargetLatencyMs = 500,
            WindowMs = 1000,
        }, _time, NullLogger<AdaptiveConcurrencyLimiter>.Instance);

    /// <summary>
    /// One window: admits <paramref name="count"/> requests at once and releases them; the last
    /// release comes after the window elapsed, so the adjustment sees all of them.
    /// </summary>
    private void RunWindow(AdaptiveConcurrencyLimiter limiter, int count, TimeSpan latency, bool overloaded = false)
    {
        for (var i = 0; i < count; i++)
            Assert.IsTrue(limiter.TryAcquire(), $"request {i} of {count} should be admitted (limit {limiter.Limit})");
        for (var i = 0; i < count - 1; i++)
            limiter.Release(latency, overloaded);
        _time.Advance(TimeSpan.FromMilliseconds(1001));
        limiter.Release(latency, overloaded);
    }

    [TestMethod]
    public void TryAcquire_RejectsOnceTheLimitIsInFlight_AndAdmitsAgainAfterARelease()
    {
        // Arrange
        var limiter = Create(initial: 2, min: 1);

        // Act / Assert
        Assert.IsTrue(limiter.TryAcquire());
        Assert.IsTrue(limiter.TryAcquire());
        Assert.IsFalse(limiter.TryAcquire());
        Assert.AreEqual(2, limiter.InFlight);

        limiter.Release(Fast, overloaded: false);

        Assert.IsTrue(limiter.TryAcquire());
        Assert.AreEqual(2, limiter.InFlight);
    }

    [TestMethod]
    public void SlowWindow_LowersTheLimitMultiplicatively()
    {
        // Arrange
        var limiter = Create(initial: 100);

        // Act
        RunWindow(limiter, 10, Slow);

        // Assert
        Assert.AreEqual(75, limiter.Limit);
    }

    [TestMethod]
    public void SlowWindows_StopAtMinLimit()
    {
        // Arrange
        var limiter = Create(initial: 100, min: 8);

        // Act
        for (var i = 0; i < 20; i++)
            RunWindow(limiter, 4, Slow);

        // Assert
        Assert.AreEqual(8, limiter.Limit);
    }

    [TestMethod]
    public void OverloadFailures_CountAsSlow_EvenWhenTheyFailFast()
    {
        // Arrange
        var limiter = Create(initial: 100);

        // Act — a shedding gateway answers in a millisecond, but it is still overload.
        RunWindow(limiter, 10, TimeSpan.FromMilliseconds(1), overloaded: true);

        // Assert
        Assert.AreEqual(75, limiter.Limit);
    }

    [TestMethod]
    public void AFewSlowOutliers_BelowTheP95_DoNotLowerTheLimit()
    {
        // Arrange
        var limiter = Create(initial: 40);
        for (var i = 0; i < 40; i++) Assert.IsTrue(limiter.TryAcquire());

        // Act — 1 of 40 requests (2.5 %) is slow: the p95 is still fast.
        limiter.Release(Slow, overloaded: false);
        for (var i = 0; i < 38; i++) limiter.Release(Fast, overloaded: false);
        _time.Advance(TimeSpan.FromMilliseconds(1001));
        limiter.Release(Fast, overloaded: false);

        // Assert — the window used the whole limit and met the target, so it grows by one.
        Assert.AreEqual(41, limiter.Limit);
    }

    [TestMethod]
    public void FastBusyWindows_RaiseTheLimitByOne_UpToMaxLimit()
    {
        // Arrange
        var limiter = Create(initial: 10, min: 1, max: 12);

        // Act
        RunWindow(limiter, 10, Fast);
        Assert.AreEqual(11, limiter.Limit);
        RunWindow(limiter, 11, Fast);
        Assert.AreEqual(12, limiter.Limit);
        RunWindow(limiter, 12, Fast);

        // Assert
        Assert.AreEqual(12, limiter.Limit);
    }

    [TestMethod]
    public void FastIdleWindows_DoNotRaiseTheLimit()
    {
        // Arrange
        var limiter = Create(initial: 100);

        // Act — 10 in flight of a limit of 100: the limit is not what bounds the traffic.
        RunWindow(limiter, 10, Fast);

        // Assert
        Assert.AreEqual(100, limiter.Limit);
    }

    [TestMethod]
    public void NoAdjustment_BeforeTheWindowHasElapsed()
    {
        // Arrange
        var limiter = Create(initial: 100);
        Assert.IsTrue(limiter.TryAcquire());

        // Act
        _time.Advance(TimeSpan.FromMilliseconds(500));
        limiter.Release(Slow, overloaded: false);

        // Assert
        Assert.AreEqual(100, limiter.Limit);
    }

    [TestMethod]
    public void AfterAnOverload_TheLimitRecovers_WhileLatencyStaysUnderTheTarget()
    {
        // Arrange — an overload drives the limit to its floor.
        var limiter = Create(initial: 20, min: 8);
        for (var i = 0; i < 5; i++)
            RunWindow(limiter, 8, Slow);
        Assert.AreEqual(8, limiter.Limit);

        // Act — the cluster is healthy again and clients keep the limit busy.
        for (var i = 0; i < 12; i++)
            RunWindow(limiter, limiter.Limit, Fast);

        // Assert
        Assert.AreEqual(20, limiter.Limit);
    }

    [TestMethod]
    public void Options_AreValidatedAtStartup()
    {
        Assert.IsNull(new AdmissionOptions().Validate());
        Assert.IsNotNull(new AdmissionOptions { MinLimit = 0 }.Validate());
        Assert.IsNotNull(new AdmissionOptions { MinLimit = 10, MaxLimit = 5, InitialLimit = 5 }.Validate());
        Assert.IsNotNull(new AdmissionOptions { InitialLimit = 2000 }.Validate());
        Assert.IsNotNull(new AdmissionOptions { TargetLatencyMs = 0 }.Validate());
        Assert.IsNotNull(new AdmissionOptions { WindowMs = 0 }.Validate());
    }
}

/// <summary>A <see cref="TimeProvider"/> whose clock moves only when a test advances it.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
