namespace Fleans.Application.StartEvents;

/// <summary>
/// Told that a deployment changed which processes a message, signal or conditional start event
/// starts. Implemented by <see cref="StartEventSubscriptionCache"/> in every host (#858).
/// </summary>
public interface IStartEventSubscriptionObserver : IGrainObserver
{
    Task SubscriptionsChanged();
}

/// <summary>
/// Fans a start-event subscription change out to the <see cref="StartEventSubscriptionCache"/> of
/// every host that uses one (#858). Called only on deploy, enable and disable, and by each host's
/// cache about once a minute to renew its subscription — never on the start path.
/// </summary>
public interface IStartEventSubscriptionNotifierGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Adds <paramref name="observer"/> or renews its subscription. Returns <c>false</c> when it was
    /// not subscribed — new, expired, dropped after a failed notification, or this grain was
    /// reactivated — so it may have missed a change and should drop what it cached.
    /// </summary>
    Task<bool> Subscribe(IStartEventSubscriptionObserver observer);

    /// <summary>Tells every subscribed observer, waiting a few seconds at most for each.</summary>
    Task NotifyChanged();
}
