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
    /// Shared namespace for engine-internal events (script, condition, activation-condition,
    /// default fallback). Custom-task events use a dedicated namespace below to keep
    /// plugin handlers from being implicit-subscribed to engine-internal streams.
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
}
