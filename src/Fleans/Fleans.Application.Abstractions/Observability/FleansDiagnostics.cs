using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Fleans.Application.Observability;

/// <summary>
/// Fleans-defined <see cref="System.Diagnostics.Metrics.Meter"/> and <see cref="System.Diagnostics.ActivitySource"/>
/// for workflow-level metrics and tracing. Registered with OpenTelemetry via
/// <c>ConfigureOpenTelemetry</c> by name ("Fleans") — no project reference required.
///
/// See <c>website/src/content/docs/reference/observability.md</c> for the metric catalog.
/// Lives in Abstractions so worker hosts and plugins (which do not reference Fleans.Application)
/// emit to the same instruments.
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
        description: "Workflow instances that reached a terminal state. Tag 'result' = completed|cancelled|failed.");

    public static readonly Counter<long> WorkflowsStalled = Meter.CreateCounter<long>(
        "fleans.workflow.stalled",
        unit: "{deliveries}",
        description: "Activity outcome deliveries the workflow instance rejected: FailActivity itself threw, so the activity stays executing until a redelivery succeeds. Tag 'handler' = script|condition|activation_condition|custom_task.");

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

    public static readonly Histogram<double> WorkflowDuration = Meter.CreateHistogram<double>(
        "fleans.workflow.duration",
        unit: "s",
        description: "Process duration: from the instance's start (WorkflowStarted) to its terminal event. " +
                     "Tag 'result' = completed|cancelled|failed, 'process.definition.key' = the BPMN process id.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = new double[]
            {
                0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 300, 1800, 3600, 86400
            }
        });

    public static readonly Histogram<double> JournalAppendDuration = Meter.CreateHistogram<double>(
        "fleans.journal.append.duration",
        unit: "s",
        description: "Duration of one workflow-instance journal append (the event-store write behind ConfirmEvents). " +
                     "Tag 'result' = appended|conflict|failed.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = new double[]
            {
                0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5
            }
        });

    public static readonly Histogram<double> ProjectionLag = Meter.CreateHistogram<double>(
        "fleans.projection.lag",
        unit: "s",
        description: "Read-model projector lag: from the instance publishing InstanceAdvanced to the projection being written.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = new double[]
            {
                0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60
            }
        });

    public static readonly Histogram<double> TimerLag = Meter.CreateHistogram<double>(
        "fleans.timer.lag",
        unit: "s",
        description: "BPMN timer lag: the reminder tick that delivered the timer minus the timer's due time.",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = new double[]
            {
                0.01, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30, 60, 120, 300
            }
        });

    public static void OnWorkflowStarted() => WorkflowsStarted.Add(1);

    /// <summary>
    /// Counts the terminal transition and records the process duration. <paramref name="startedAt"/>
    /// and <paramref name="endedAt"/> are journaled times (the <c>WorkflowStarted</c> and terminal
    /// events' <c>OccurredAt</c>), so an instance that spans reactivations is measured end to end.
    /// </summary>
    public static void OnWorkflowCompleted(string? processDefinitionKey, DateTimeOffset? startedAt, DateTimeOffset endedAt) =>
        OnWorkflowTerminated("completed", processDefinitionKey, startedAt, endedAt);

    public static void OnWorkflowCancelled(string? processDefinitionKey, DateTimeOffset? startedAt, DateTimeOffset endedAt) =>
        OnWorkflowTerminated("cancelled", processDefinitionKey, startedAt, endedAt);

    public static void OnWorkflowFailed(string? processDefinitionKey, DateTimeOffset? startedAt, DateTimeOffset endedAt) =>
        OnWorkflowTerminated("failed", processDefinitionKey, startedAt, endedAt);

    private static void OnWorkflowTerminated(string result, string? processDefinitionKey, DateTimeOffset? startedAt, DateTimeOffset endedAt)
    {
        WorkflowsTerminated.Add(1, new KeyValuePair<string, object?>("result", result));
        if (startedAt is not { } start)
            return;
        WorkflowDuration.Record(NonNegativeSeconds(endedAt - start),
            new KeyValuePair<string, object?>("result", result),
            new KeyValuePair<string, object?>("process.definition.key", processDefinitionKey ?? "unknown"));
    }

    public static void RecordJournalAppend(TimeSpan elapsed, string result) =>
        JournalAppendDuration.Record(NonNegativeSeconds(elapsed), new KeyValuePair<string, object?>("result", result));

    /// <summary>Projector lag: <paramref name="projectedAt"/> − <paramref name="publishedAt"/>.</summary>
    public static void RecordProjectionLag(DateTimeOffset publishedAt, DateTimeOffset projectedAt) =>
        ProjectionLag.Record(NonNegativeSeconds(projectedAt - publishedAt));

    /// <summary>Timer lag: <paramref name="firedAt"/> − <paramref name="dueAt"/>.</summary>
    public static void RecordTimerLag(DateTime dueAt, DateTime firedAt) =>
        TimerLag.Record(NonNegativeSeconds(firedAt - dueAt));

    // A lag's two ends can be stamped by different silos' clocks: skew must not record negative time.
    private static double NonNegativeSeconds(TimeSpan span) => Math.Max(0, span.TotalSeconds);

    public static void OnWorkflowStalled(string handler) =>
        WorkflowsStalled.Add(1, new KeyValuePair<string, object?>("handler", handler));

    public static void RecordActivityDuration(double milliseconds, string activityType) =>
        ActivityDuration.Record(milliseconds, new KeyValuePair<string, object?>("activity.type", activityType));
}
