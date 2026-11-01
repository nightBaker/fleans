namespace Fleans.Application.Abstractions.Events;

/// <summary>
/// Stream-namespace constants for Fleans engine-published events. Lives in the
/// abstractions package so plugin handlers (e.g. <c>CustomTaskHandlerBase</c>
/// subclasses) can carry <c>[ImplicitStreamSubscription(...)]</c> attributes without
/// taking a transitive reference on <c>Fleans.Application</c> / <c>Fleans.Domain</c>.
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
    /// provider (engine and plugin hosts). Orleans retries one event for this long, then skips it
    /// for good — implicit subscriptions are never faulted, so nothing records the skip. It must
    /// outlast the longest handler run (a <c>rest-call</c> custom task may take 300 s) plus the time
    /// an event waits behind it on the same handler grain. Orleans' own default is 1 minute (#850).
    /// Overridden by <c>Fleans:Streaming:MaxEventDeliveryTime</c>.
    /// </summary>
    public static readonly TimeSpan DefaultMaxEventDeliveryTime = TimeSpan.FromMinutes(6);

    /// <summary>
    /// Shared namespace prefix for engine-published events. Custom-task events use a dedicated
    /// namespace below to keep plugin handlers from being implicit-subscribed to other streams.
    /// Script tasks and conditions use no stream: the instance dispatches scripts to the
    /// executor grain and evaluates conditions itself.
    /// </summary>
    public const string StreamNameSpace = "events";

    /// <summary>
    /// Dedicated namespace for <c>ExecuteCustomTaskEvent</c>. Plugin handlers
    /// (<c>CustomTaskHandlerBase</c> subclasses) carry
    /// <c>[ImplicitStreamSubscription(ExecuteCustomTaskStreamNamespace)]</c> so Orleans
    /// only activates them on custom-task events. Sharing the engine "events" namespace
    /// caused every implicit-subscriber grain class to be activated for every event type
    /// — Orleans then logs "got an item for subscription …, but I don't have any
    /// subscriber for that stream. Dropping on the floor." on each cross-event delivery.
    /// </summary>
    public const string ExecuteCustomTaskStreamNamespace = "events.ExecuteCustomTaskEvent";

    /// <summary>
    /// Dedicated namespace for <c>InstanceAdvanced</c>: one stream per workflow instance, keyed by
    /// its id, consumed by the read-model projector (#861).
    /// </summary>
    public const string InstanceAdvancedStreamNamespace = "events.InstanceAdvanced";

    /// <summary>
    /// Dedicated namespace for <c>SignalDeliveryPage</c>: a large signal bucket publishes its
    /// deliveries in pages on its own stream (<see cref="SignalDeliveryStreamKey"/>), consumed by
    /// the signal delivery grain of that bucket (#976).
    /// </summary>
    public const string SignalDeliveryStreamNamespace = "events.SignalDelivery";

    /// <summary>Key of the delivery stream of one signal bucket: <c>{bucket}:{signalName}</c>.</summary>
    public static string SignalDeliveryStreamKey(int bucket, string signalName) => $"{bucket}:{signalName}";

    /// <summary>
    /// Builds the per-<c>TaskType</c> stream namespace for custom-task events:
    /// <c>events.ExecuteCustomTaskEvent.{taskType}</c>. The publisher routes through this
    /// helper so each plugin's subscriber grain only sees its own traffic.
    /// </summary>
    /// <remarks>
    /// Plugin subclasses cannot use this helper for their <c>[ImplicitStreamSubscription]</c>
    /// attribute because attribute arguments must be compile-time constant expressions —
    /// each subclass declares the literal string. <c>AddCustomTaskPlugin&lt;T&gt;</c> validates
    /// at registration time that the attribute string matches the value returned here.
    /// </remarks>
    public static string GetExecuteCustomTaskNamespace(string taskType) =>
        $"{ExecuteCustomTaskStreamNamespace}.{taskType}";
}
