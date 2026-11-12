namespace Fleans.Application.Abstractions.Events;

/// <summary>
/// Stream-namespace constants for Fleans engine-published events.
/// </summary>
public static class WorkflowEventStreams
{
    /// <summary>
    /// Orleans stream provider name. Keep in sync with
    /// <c>FleanStreamingExtensions.StreamProviderName</c> in <c>Fleans.ServiceDefaults</c>.
    /// </summary>
    public const string StreamProvider = "StreamProvider";

    /// <summary>
    /// Default pulling-agent <c>MaxEventDeliveryTime</c> for every silo that runs the stream
    /// provider. Orleans retries one event for this long, then skips it
    /// for good — implicit subscriptions are never faulted, so nothing records the skip. It must
    /// outlast the longest handler run (a <c>rest-call</c> custom task may take 300 s); a custom task's
    /// dispatcher is keyed per task (#815), so only a redelivery of the same task waits behind it.
    /// Orleans' own default is 1 minute (#850).
    /// Overridden by <c>Fleans:Streaming:MaxEventDeliveryTime</c>.
    /// </summary>
    public static readonly TimeSpan DefaultMaxEventDeliveryTime = TimeSpan.FromMinutes(6);

    /// <summary>
    /// Pulling-agent <c>StreamInactivityPeriod</c> for a delivery window: how long a stream may stay
    /// idle before the agent drops its cache entry (subscribers, cursor). Custom-task streams are
    /// keyed per task (#815), so Orleans' 30-minute default would hold one entry per task started in
    /// the last half hour. Twice the window keeps a stream whose event is still being retried.
    /// </summary>
    public static TimeSpan StreamInactivityPeriodFor(TimeSpan maxEventDeliveryTime) => maxEventDeliveryTime * 2;

    /// <summary>
    /// Shared namespace prefix for engine-published events.
    /// Script tasks and conditions use no stream: the instance dispatches scripts to the
    /// executor grain and evaluates conditions itself.
    /// </summary>
    public const string StreamNameSpace = "events";

    /// <summary>
    /// Namespace for every custom task (<c>ExecuteCustomTaskEvent</c>, #979, #1013), keyed by
    /// activity instance id. Its only subscriber is the engine's <c>ExternalTaskDispatcherGrain</c>,
    /// which hands the tasks to gRPC workers.
    /// </summary>
    public const string ExternalTaskStreamNamespace = "events.ExternalTask";

    /// <summary>
    /// Dedicated namespace for <c>InstanceAdvanced</c>, consumed by the read-model projector (#861).
    /// One stream per projector bucket (<see cref="InstanceAdvancedStreamKey"/>, #1011), not per
    /// instance: a burst of completions is handled by the bucket projectors already active instead
    /// of placing one projector per instance.
    /// </summary>
    public const string InstanceAdvancedStreamNamespace = "events.InstanceAdvanced";

    /// <summary>Key of the <c>InstanceAdvanced</c> stream of one projector bucket.</summary>
    public static string InstanceAdvancedStreamKey(int bucket) => bucket.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Dedicated namespace for <c>SignalDeliveryPage</c>: a large signal bucket publishes its
    /// deliveries in pages on its own stream (<see cref="SignalDeliveryStreamKey"/>), consumed by
    /// the signal delivery grain of that bucket (#976).
    /// </summary>
    public const string SignalDeliveryStreamNamespace = "events.SignalDelivery";

    /// <summary>Key of the delivery stream of one signal bucket: <c>{bucket}:{signalName}</c>.</summary>
    public static string SignalDeliveryStreamKey(int bucket, string signalName) => $"{bucket}:{signalName}";
}
