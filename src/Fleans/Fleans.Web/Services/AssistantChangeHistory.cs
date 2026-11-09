using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fleans.Assistant.Contracts;

namespace Fleans.Web.Services;

public enum AssistantRevisionKind
{
    /// <summary>The diagram as it was before the tab's first change.</summary>
    Original,
    Assistant,
    /// <summary>Hand edits found just before the diagram was changed again.</summary>
    Manual,
    Restored,
}

/// <param name="Number">Stable through eviction; what "restored revision N" refers to.</param>
/// <param name="Xml">What the modeler held after this change. The state before it is the previous revision.</param>
/// <param name="Request">The user request a proposal answered.</param>
/// <param name="Summary">The start of the assistant's reply.</param>
/// <param name="ProposalHash">Links the revision to its chat message, see <see cref="AssistantChangeHistory.HashOf"/>.</param>
/// <param name="RestoredFrom">The revision a <see cref="AssistantRevisionKind.Restored"/> one brought back.</param>
public sealed record AssistantRevision(
    int Number,
    AssistantRevisionKind Kind,
    string Xml,
    DateTimeOffset At,
    string? Request = null,
    string? Summary = null,
    string? ProposalHash = null,
    int? RestoredFrom = null);

/// <summary>
/// One editor tab's states reached through the assistant panel (#1024). Linear and non-destructive: entries
/// are only appended, restoring one appends a copy, and the last entry is always the current state. Only the
/// oldest entries ever leave, to stay within the caps. Kept in <c>sessionStorage</c> by the editor and never
/// sent to the assistant backend or the cluster.
/// </summary>
public sealed class AssistantChangeHistory(int maxRevisions = AssistantChangeHistory.DefaultMaxRevisions, int maxChars = AssistantChangeHistory.DefaultMaxChars)
{
    public const int DefaultMaxRevisions = 50;

    /// <summary>About 40% of a browser's usual 5 million character <c>sessionStorage</c> quota, which the tabs share.</summary>
    public const int DefaultMaxChars = 2_000_000;

    public const int MaxSummaryLength = 120;

    private static readonly JsonSerializerOptions StorageJson = new()
    {
        // Stays in sessionStorage, so XML needn't be escaped: "<" as < would use up the quota six times as fast.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<AssistantRevision> _revisions = new();
    private int _nextNumber = 1;

    /// <summary>Oldest first; the last one is <see cref="Current"/>.</summary>
    public IReadOnlyList<AssistantRevision> Revisions => _revisions;

    public AssistantRevision? Current => _revisions.Count == 0 ? null : _revisions[^1];

    /// <summary>How many of the oldest revisions the caps have removed.</summary>
    public int Dropped { get; private set; }

    public AssistantRevision? Find(int number) => _revisions.Find(r => r.Number == number);

    /// <summary>Whether the diagram holds this proposal now; a proposal that was undone can be applied again.</summary>
    public bool IsApplied(string? proposalHash) =>
        proposalHash is not null && Current?.ProposalHash == proposalHash;

    /// <summary>
    /// The state before the current one. After an undo that is the one before the revision it brought back, so
    /// undoing repeatedly walks back through the changes instead of swapping the last two states.
    /// </summary>
    public AssistantRevision? UndoTarget
    {
        get
        {
            var position = Current;
            while (position?.RestoredFrom is { } from)
                position = Find(from);
            if (position is null) return null;
            var index = _revisions.IndexOf(position);
            return index > 0 ? _revisions[index - 1] : null;
        }
    }

    /// <summary>
    /// Call with the modeler's XML before changing the diagram, so nothing is lost: starts the history, or
    /// saves hand edits made since the last revision.
    /// </summary>
    /// <returns>The new revision, or <c>null</c> when the diagram is the known current state.</returns>
    public AssistantRevision? CaptureIfChanged(string xml, DateTimeOffset at)
    {
        if (Current is null) return Append(new(0, AssistantRevisionKind.Original, xml, at));
        return Current.Xml == xml ? null : Append(new(0, AssistantRevisionKind.Manual, xml, at));
    }

    /// <param name="xml">What the modeler holds after loading the proposal, not the proposal: the import can add a layout.</param>
    /// <returns>The new revision, or <c>null</c> when the proposal didn't change the diagram.</returns>
    public AssistantRevision? RecordAssistant(string xml, string? request, string? replyText, string? proposalHash, DateTimeOffset at)
    {
        if (Current is { } current && current.Xml == xml)
        {
            _revisions[^1] = current with { ProposalHash = proposalHash };
            return null;
        }
        return Append(new(0, AssistantRevisionKind.Assistant, xml, at, request, Summarize(replyText), proposalHash));
    }

    /// <param name="xml">What the modeler holds after loading <paramref name="source"/>.</param>
    public AssistantRevision RecordRestored(AssistantRevision source, string xml, DateTimeOffset at) =>
        Append(new(0, AssistantRevisionKind.Restored, xml, at, source.Request, source.Summary, source.ProposalHash, source.Number));

    private AssistantRevision Append(AssistantRevision revision)
    {
        revision = revision with { Number = _nextNumber++ };
        _revisions.Add(revision);
        Evict();
        return revision;
    }

    // The newest entry always stays, even alone over the byte cap: it is the current state.
    private void Evict()
    {
        var chars = _revisions.Sum(Size);
        while (_revisions.Count > 1 && (_revisions.Count > maxRevisions || chars > maxChars))
        {
            chars -= Size(_revisions[0]);
            _revisions.RemoveAt(0);
            Dropped++;
        }
    }

    private static int Size(AssistantRevision r) => r.Xml.Length + (r.Request?.Length ?? 0) + (r.Summary?.Length ?? 0);

    public static string? Summarize(string? replyText)
    {
        var text = replyText?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= MaxSummaryLength ? text : text[..MaxSummaryLength].TrimEnd() + "…";
    }

    /// <summary>The proposal's identity: a stored chat comes back from the backend and the history from the browser, so a
    /// reloaded page matches them by this.</summary>
    public static string HashOf(string proposalXml) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(proposalXml)));

    /// <summary>
    /// The proposal a finished turn should apply on its own, if any: the turn's last reply, only if it passed the
    /// engine's BPMN check, only with the switch on and the editor able to take it.
    /// </summary>
    public static AssistantChat.Entry? AutoApplyCandidate(AssistantChat chat, bool autoApply, bool ready)
    {
        if (!autoApply || !ready || chat.Entries.Count == 0) return null;
        return chat.Entries[^1] is { Role: AssistantRoles.Assistant, ProposalXml: not null, ProposalValid: true } reply ? reply : null;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(new StoragePayload(_nextNumber, Dropped, _revisions), StorageJson);

    /// <returns>The stored history, or <c>null</c> when <paramref name="json"/> isn't one.</returns>
    public static AssistantChangeHistory? Deserialize(string json, int maxRevisions = DefaultMaxRevisions, int maxChars = DefaultMaxChars)
    {
        StoragePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<StoragePayload>(json, StorageJson);
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload?.Revisions is null) return null;

        var history = new AssistantChangeHistory(maxRevisions, maxChars) { _nextNumber = payload.NextNumber, Dropped = payload.Dropped };
        history._revisions.AddRange(payload.Revisions);
        return history;
    }

    private sealed record StoragePayload(int NextNumber, int Dropped, List<AssistantRevision> Revisions);
}
