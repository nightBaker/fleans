using Fleans.Application.Editor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Fleans.Web.Components.Pages;

// Collaboration (design board "System states · collaboration"): who else has this process open, what
// they have selected, and — at deploy time — whether someone deployed a newer version since this tab
// was opened. Presence goes through IEditorPresenceGrain (ephemeral, keyed by process key); the editor
// heartbeats every PresenceInterval while a tab with a process key is active.
public partial class Editor
{
    private static readonly TimeSpan PresenceInterval = TimeSpan.FromSeconds(5);

    [CascadingParameter] private Task<AuthenticationState>? AuthenticationState { get; set; }
    [Inject] private IGrainFactory GrainFactory { get; set; } = default!;

    // One presence session per editor page; only its active tab is announced.
    private readonly string presenceSessionId = Guid.NewGuid().ToString("N");
    private string? presenceKey;
    private string displayName = "Guest";
    private List<EditorPresence> collaborators = new();
    private System.Threading.Timer? presenceTimer;

    private async Task StartPresenceAsync()
    {
        if (AuthenticationState != null)
        {
            var state = await AuthenticationState;
            var name = state.User.Identity?.IsAuthenticated == true ? state.User.Identity.Name : null;
            displayName = string.IsNullOrWhiteSpace(name) ? $"Guest {presenceSessionId[..4]}" : name;
        }
        presenceTimer = new System.Threading.Timer(_ => _ = InvokeAsync(HeartbeatPresenceAsync),
            null, TimeSpan.Zero, PresenceInterval);
    }

    private async Task HeartbeatPresenceAsync()
    {
        var key = ActiveTab?.ProcessKey;
        try
        {
            if (presenceKey != null && presenceKey != key)
            {
                await GrainFactory.GetGrain<IEditorPresenceGrain>(presenceKey).Leave(presenceSessionId);
                presenceKey = null;
            }

            if (string.IsNullOrEmpty(key))
            {
                SetCollaborators(new List<EditorPresence>());
                return;
            }

            presenceKey = key;
            var others = await GrainFactory.GetGrain<IEditorPresenceGrain>(key).Heartbeat(new EditorPresence(
                presenceSessionId, displayName, selectedElement?.Id, selectedElement?.Name, DateTimeOffset.UtcNow));
            SetCollaborators(others);
        }
        catch (Exception ex)
        {
            // Presence is best-effort: a silo hiccup must not disturb editing.
            LogPresenceHeartbeatFailed(ex);
        }
    }

    private void SetCollaborators(List<EditorPresence> others)
    {
        var changed = others.Count != collaborators.Count ||
            others.Zip(collaborators).Any(p => p.First.SessionId != p.Second.SessionId ||
                                               p.First.SelectedElementId != p.Second.SelectedElementId);
        collaborators = others;
        if (!changed) return;
        _ = PaintPresenceAsync();
        StateHasChanged();
    }

    private async Task PaintPresenceAsync()
    {
        if (!editorInitialized) return;
        var marks = collaborators
            .Where(c => !string.IsNullOrEmpty(c.SelectedElementId))
            .Select(c => new { elementId = c.SelectedElementId, label = $"{Initials(c.DisplayName)} editing" })
            .ToList();
        try
        {
            await JS.InvokeVoidAsync("bpmnEditor.setPresence", marks);
        }
        catch (JSDisconnectedException)
        {
            // Page is going away.
        }
    }

    private async Task StopPresenceAsync()
    {
        presenceTimer?.Dispose();
        presenceTimer = null;
        if (presenceKey == null) return;
        try
        {
            await GrainFactory.GetGrain<IEditorPresenceGrain>(presenceKey).Leave(presenceSessionId);
        }
        catch (Exception ex)
        {
            LogPresenceLeaveFailed(ex);
        }
    }

    private static string Initials(string name)
    {
        var parts = name.Split([' ', '.', '@', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}"
        };
    }

    private static string PresenceTitle(EditorPresence p) =>
        string.IsNullOrEmpty(p.SelectedElementId)
            ? $"{p.DisplayName} has this process open"
            : $"{p.DisplayName} is editing {(string.IsNullOrWhiteSpace(p.SelectedElementName) ? p.SelectedElementId : p.SelectedElementName)}";

    // ---- Deploy conflicts -----------------------------------------------------------------

    // Newer than the version this tab is based on (opened from, or last deployed by it). A new tab
    // whose key already has versions conflicts too: someone else owns that key.
    private int? deployConflictVersion;

    private void DetectDeployConflict(int maxDeployedVersion)
    {
        var baseVersion = ActiveTab?.Version ?? 0;
        deployConflictVersion = maxDeployedVersion > baseVersion ? maxDeployedVersion : null;
    }

    private async Task CompareWithConflict()
    {
        var version = deployConflictVersion;
        CancelDeploy();
        if (version is null) return;
        await SwitchMode(EditorMode.History);
        await CompareWithVersionAsync(version.Value);
    }

    private async Task TakeTheirs()
    {
        var version = deployConflictVersion;
        CancelDeploy();
        if (version is null) return;
        await OpenVersionInTab(version.Value);
    }
}
