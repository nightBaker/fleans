using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Fleans.Application.Observability;

/// <summary>
/// Fleans-defined <see cref="System.Diagnostics.Metrics.Meter"/> and <see cref="System.Diagnostics.ActivitySource"/>
/// for workflow-level metrics and tracing. Registered with OpenTelemetry via
/// <c>ConfigureOpenTelemetry</c> by name ("Fleans") — no project reference required.
///
/// See <c>website/src/content/docs/reference/observability.md</c> for the metric catalog.
/// </summary>
public static class FleansDiagnostics
{
    public const string MeterName = "Fleans";
    public const string ActivitySourceName = "Fleans";
    public const string InstrumentationVersion = "1.0.0";

    public static readonly Meter Meter = new(MeterName, InstrumentationVersion);
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, InstrumentationVersion);

    public static readonly Counter<long> WorkflowsStarted = Meter.CreateCounter<long>(
        "fleans.workflow.started",
        unit: "{instances}",
        description: "Workflow instances started.");

    public static readonly Counter<long> WorkflowsTerminated = Meter.CreateCounter<long>(
        "fleans.workflow.terminated",
        unit: "{instances}",
        description: "Workflow instances that reached a terminal state. Tag 'result' = completed|cancelled.");

    public static readonly Histogram<double> ActivityDuration = Meter.CreateHistogram<double>(
        "fleans.activity.duration",
        unit: "ms",
        description: "Per-activity wall-clock duration from start to completion, failure, or cancellation.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = new double[]
            {
                10, 50, 100, 250, 500, 1000, 5000, 10000, 30000, 60000, 300000, 600000
            }
        });

    public static void OnWorkflowStarted() => WorkflowsStarted.Add(1);

    public static void OnWorkflowCompleted() =>
        WorkflowsTerminated.Add(1, new KeyValuePair<string, object?>("result", "completed"));

    public static void OnWorkflowCancelled() =>
        WorkflowsTerminated.Add(1, new KeyValuePair<string, object?>("result", "cancelled"));

    public static void RecordActivityDuration(double milliseconds, string activityType) =>
        ActivityDuration.Record(milliseconds, new KeyValuePair<string, object?>("activity.type", activityType));
}
