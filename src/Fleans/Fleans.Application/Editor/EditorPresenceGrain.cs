using Fleans.Application.Placement;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Editor;

/// <inheritdoc cref="IEditorPresenceGrain"/>
[CorePlacement]
public sealed partial class EditorPresenceGrain : Grain, IEditorPresenceGrain
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, EditorPresence> _sessions = new();
    private readonly ILogger<EditorPresenceGrain> _logger;

    public EditorPresenceGrain(ILogger<EditorPresenceGrain> logger)
    {
        _logger = logger;
    }

    public Task<List<EditorPresence>> Heartbeat(EditorPresence session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var now = DateTimeOffset.UtcNow;
        PruneStale(now);

        if (!_sessions.ContainsKey(session.SessionId))
            LogSessionJoined(this.GetPrimaryKeyString(), session.SessionId, session.DisplayName);
        _sessions[session.SessionId] = session with { SeenAt = now };

        var others = _sessions.Values
            .Where(s => s.SessionId != session.SessionId)
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult(others);
    }

    public Task Leave(string sessionId)
    {
        if (_sessions.Remove(sessionId))
            LogSessionLeft(this.GetPrimaryKeyString(), sessionId);
        return Task.CompletedTask;
    }

    private void PruneStale(DateTimeOffset now)
    {
        foreach (var stale in _sessions.Values.Where(s => now - s.SeenAt > StaleAfter).ToList())
        {
            _sessions.Remove(stale.SessionId);
            LogSessionExpired(this.GetPrimaryKeyString(), stale.SessionId);
        }
    }

    [LoggerMessage(EventId = 14200, Level = LogLevel.Debug,
        Message = "Editor session {SessionId} ({DisplayName}) opened process {ProcessKey}")]
    private partial void LogSessionJoined(string processKey, string sessionId, string displayName);

    [LoggerMessage(EventId = 14201, Level = LogLevel.Debug,
        Message = "Editor session {SessionId} left process {ProcessKey}")]
    private partial void LogSessionLeft(string processKey, string sessionId);

    [LoggerMessage(EventId = 14202, Level = LogLevel.Debug,
        Message = "Editor session {SessionId} on process {ProcessKey} expired (no heartbeat)")]
    private partial void LogSessionExpired(string processKey, string sessionId);
}
