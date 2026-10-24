using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Fleans.Application.Observability;

namespace Fleans.Application.Tests;

/// <summary>
/// The duration / lag arithmetic behind the scale metrics (roadmap v1, "How it is measured").
/// </summary>
[TestClass]
[DoNotParallelize] // listens to the process-wide Fleans meter
public class FleansDiagnosticsTests
{
    [TestMethod]
    public void WorkflowDuration_IsEndMinusStart_TaggedByResultAndProcessKey()
    {
        // Arrange
        using var metrics = new MetricsCapture();
        var key = $"proc-{Guid.NewGuid():N}";
        var start = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        // Act
        FleansDiagnostics.OnWorkflowCompleted(key, start, start.AddSeconds(2.5));
        FleansDiagnostics.OnWorkflowFailed(key, start, start.AddMilliseconds(250));

        // Assert
        var durations = metrics.Of("fleans.workflow.duration").Where(m => Equals(m.Tag("process.definition.key"), key)).ToList();
        Assert.AreEqual(2, durations.Count);
        Assert.AreEqual(2.5, durations.Single(m => Equals(m.Tag("result"), "completed")).Value, 1e-9);
        Assert.AreEqual(0.25, durations.Single(m => Equals(m.Tag("result"), "failed")).Value, 1e-9);
    }

    [TestMethod]
    public void WorkflowTerminated_WithoutAStartTime_CountsButRecordsNoDuration()
    {
        // Arrange
        using var metrics = new MetricsCapture();
        var key = $"proc-{Guid.NewGuid():N}";

        // Act
        FleansDiagnostics.OnWorkflowCancelled(key, startedAt: null, DateTimeOffset.UtcNow);

        // Assert
        Assert.IsFalse(metrics.Of("fleans.workflow.duration").Any(m => Equals(m.Tag("process.definition.key"), key)));
        Assert.IsTrue(metrics.Of("fleans.workflow.terminated").Any(m => Equals(m.Tag("result"), "cancelled")));
    }

    [TestMethod]
    public void WorkflowDuration_WithoutAProcessKey_IsTaggedUnknown()
    {
        using var metrics = new MetricsCapture();
        var start = DateTimeOffset.UtcNow;

        FleansDiagnostics.OnWorkflowCompleted(null, start, start.AddSeconds(1));

        Assert.IsTrue(metrics.Of("fleans.workflow.duration").Any(m => Equals(m.Tag("process.definition.key"), "unknown")));
    }

    [TestMethod]
    public void TimerLag_IsFiredMinusDue()
    {
        using var metrics = new MetricsCapture();
        var due = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        FleansDiagnostics.RecordTimerLag(due, due.AddSeconds(1.5));

        Assert.IsTrue(metrics.Of("fleans.timer.lag").Any(m => Math.Abs(m.Value - 1.5) < 1e-9));
    }

    [TestMethod]
    public void ProjectionLag_IsProjectedMinusPublished()
    {
        using var metrics = new MetricsCapture();
        var published = DateTimeOffset.UtcNow;

        FleansDiagnostics.RecordProjectionLag(published, published.AddMilliseconds(40));

        Assert.IsTrue(metrics.Of("fleans.projection.lag").Any(m => Math.Abs(m.Value - 0.04) < 1e-9));
    }

    [TestMethod]
    public void Lags_StampedBySkewedClocks_AreNeverNegative()
    {
        // The two ends of a lag can come from different silos' clocks.
        using var metrics = new MetricsCapture();
        var now = DateTime.UtcNow;

        FleansDiagnostics.RecordTimerLag(now, now.AddSeconds(-3));
        FleansDiagnostics.RecordProjectionLag(now, new DateTimeOffset(now).AddSeconds(-3));

        Assert.IsTrue(metrics.Of("fleans.timer.lag").Any(m => m.Value == 0));
        Assert.IsTrue(metrics.Of("fleans.projection.lag").Any(m => m.Value == 0));
    }

    [TestMethod]
    public void JournalAppend_IsRecordedInSecondsWithItsResult()
    {
        using var metrics = new MetricsCapture();

        FleansDiagnostics.RecordJournalAppend(TimeSpan.FromMilliseconds(12), "conflict");

        Assert.IsTrue(metrics.Of("fleans.journal.append.duration")
            .Any(m => Math.Abs(m.Value - 0.012) < 1e-9 && Equals(m.Tag("result"), "conflict")));
    }

    internal sealed record Measurement(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public object? Tag(string name) => Tags.FirstOrDefault(t => t.Key == name).Value;
    }

    /// <summary>Captures every measurement of the Fleans meter while alive.</summary>
    internal sealed class MetricsCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<Measurement> _measurements = new();

        public MetricsCapture()
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == FleansDiagnostics.MeterName)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) =>
                _measurements.Enqueue(new Measurement(i.Name, v, tags.ToArray())));
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
                _measurements.Enqueue(new Measurement(i.Name, v, tags.ToArray())));
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Of(string instrument) =>
            _measurements.Where(m => m.Instrument == instrument).ToList();

        public void Dispose() => _listener.Dispose();
    }
}
