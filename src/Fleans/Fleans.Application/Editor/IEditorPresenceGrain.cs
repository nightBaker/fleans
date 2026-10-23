namespace Fleans.Application.Editor;

/// <summary>
/// Who has a process open in the Admin UI's BPMN editor right now, keyed by process key. Each
/// editor tab heartbeats every few seconds; sessions not seen for <see cref="EditorPresenceGrain.StaleAfter"/>
/// drop out. Ephemeral by design: presence is not worth persisting, and a reactivated grain
/// repopulates within one heartbeat.
/// </summary>
public interface IEditorPresenceGrain : IGrainWithStringKey
{
    /// <summary>Upserts the caller's session and returns the other live sessions.</summary>
    Task<List<EditorPresence>> Heartbeat(EditorPresence session);

    /// <summary>Removes a session (tab closed, process renamed, page left). Unknown ids are ignored.</summary>
    Task Leave(string sessionId);
}

/// <param name="SessionId">One per editor tab (browser page × process); not a user id.</param>
/// <param name="DisplayName">The signed-in user's name, or "Guest" without authentication.</param>
/// <param name="SelectedElementId">The element selected in that tab, if any.</param>
[GenerateSerializer]
public sealed record EditorPresence(
    [property: Id(0)] string SessionId,
    [property: Id(1)] string DisplayName,
    [property: Id(2)] string? SelectedElementId,
    [property: Id(3)] string? SelectedElementName,
    [property: Id(4)] DateTimeOffset SeenAt);
