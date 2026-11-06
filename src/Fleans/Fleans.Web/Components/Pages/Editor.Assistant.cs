using System.Text;
using Fleans.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Fleans.Web.Components.Pages;

// AI assistant panel (#1015): the chat lives in AssistantPanel; the editor reads the diagram for it and
// applies proposals to the active tab or a new one, recording each change in the tab's
// AssistantChangeHistory (#1024). Nothing here deploys.
public partial class Editor
{
    // Same cap as Import BPMN.
    private const long MaxAssistantXmlBytes = 10 * 1024 * 1024;

    // A history holds at most its character cap, or one diagram over it; UTF-8 can take three bytes per character.
    private const long MaxHistoryBytes = 2 * MaxAssistantXmlBytes;

    private const string AssistantAutoApplyStorageKey = "fleans.editor.assistant.autoApply.v1";
    private const string AssistantHistoryStoragePrefix = "fleans.editor.assistant.history.v1.";

    [Inject]
    private IOptions<AssistantOptions> AssistantConfig { get; set; } = null!;

    [Inject]
    private IStringLocalizer<AssistantPanel> AssistantL { get; set; } = null!;

    private bool assistantOpen;
    private bool assistantAutoApply = true;
    private bool changingDiagram;

    private bool AssistantVisible => assistantOpen && AssistantConfig.Value.IsEnabled;

    private void ToggleAssistant() => assistantOpen = !assistantOpen;

    // Read as a byte stream: getXml's string return value is capped by the SignalR receive limit.
    private async Task<string> ReadModelerXmlAsync()
    {
        var bytes = await JS.InvokeAsync<IJSStreamReference>("bpmnEditor.getXmlBytes");
        await using (bytes)
        {
            await using var stream = await bytes.OpenReadStreamAsync(MaxAssistantXmlBytes);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
    }

    private async Task<string?> GetXmlForAssistantAsync()
    {
        // A blank tab (at most its start event) is a new diagram: the contract sends null.
        if (summary.Elements <= 1 && summary.Flows == 0) return null;

        var xml = await ReadModelerXmlAsync();
        return xml.Length == 0 ? null : xml;
    }

    private async Task OpenProposalInNewTabAsync(AssistantChat.Entry reply)
    {
        if (reply.ProposalXml is not { } xml) return;
        if (!EnsureCapacity() || !await TryReturnToModelingAsync()) return;
        await SaveActiveTabXml();

        // The new tab continues the conversation, so a follow-up ("now add a timer") works there.
        var request = ActiveTab?.Assistant.RequestFor(reply);
        var tab = new TabSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = "",
            ProcessKey = "",
            Version = null,
            BpmnXml = xml,
            Dirty = true,
            Assistant = ActiveTab?.Assistant.Clone() ?? new AssistantChat(),
        };
        tabs.Add(tab);
        if (await ActivateTab(tab.Id, saveOutgoing: false))
        {
            try
            {
                var loaded = await ReadModelerXmlAsync();
                LogRevision(tab, tab.Changes.RecordAssistant(loaded, request, reply.Text, reply.ProposalHash, DateTimeOffset.UtcNow));
                await PersistHistoryAsync(tab);
            }
            catch (Exception ex)
            {
                LogAssistantHistoryWriteFailed(ex);
            }
        }
        if (string.IsNullOrEmpty(tab.Label))
            tab.Label = $"Assistant-{tabs.Count}";
        await UpdateDirtyFlagAndPersist();
    }

    // Only a proposal that passed the engine's BPMN check ever reaches the diagram.
    private Task ApplyProposalAsync(AssistantChat.Entry reply)
    {
        if (reply is not { ProposalXml: { } xml, ProposalValid: true } || ActiveTab is not { } tab)
            return Task.CompletedTask;

        var request = tab.Assistant.RequestFor(reply);
        return ChangeDiagramAsync(tab, () => xml,
            (loaded, at) => tab.Changes.RecordAssistant(loaded, request, reply.Text, reply.ProposalHash, at));
    }

    private Task RestoreRevisionAsync(int number)
    {
        if (ActiveTab is not { } tab) return Task.CompletedTask;
        // Picked now: saving hand edits below can evict it from a full history.
        var revision = tab.Changes.Find(number);
        return RestoreAsync(tab, () => revision);
    }

    // Picked after hand edits are saved: they are the change being undone.
    private Task UndoAssistantChangeAsync() =>
        ActiveTab is { } tab ? RestoreAsync(tab, () => tab.Changes.UndoTarget) : Task.CompletedTask;

    private Task RestoreAsync(TabSession tab, Func<AssistantRevision?> pick)
    {
        AssistantRevision? revision = null;
        return ChangeDiagramAsync(tab, () => (revision = pick())?.Xml,
            (loaded, at) => tab.Changes.RecordRestored(revision!, loaded, at));
    }

    // Every assistant-driven change of the open diagram: saves what is there, loads the new XML and records
    // what the modeler then holds. That read-back is what the next change compares with, so an untouched
    // diagram never looks hand-edited, whatever the import added to the XML (a proposal's layout, formatting).
    private async Task ChangeDiagramAsync(TabSession tab, Func<string?> pickXml, Func<string, DateTimeOffset, AssistantRevision?> record)
    {
        if (changingDiagram || !await TryReturnToModelingAsync()) return;
        changingDiagram = true;
        try
        {
            var at = DateTimeOffset.UtcNow;
            // Failing here leaves the diagram alone: it isn't changed without being saved first.
            LogRevision(tab, tab.Changes.CaptureIfChanged(await ReadModelerXmlAsync(), at));
            if (pickXml() is not { } xml)
            {
                await PersistHistoryAsync(tab);
                return;
            }

            // loadXml re-imports, which also clears bpmn-js' undo stack: History is the way back.
            await JS.InvokeVoidAsync("bpmnEditor.loadXml", xml);
            var loaded = await ReadModelerXmlAsync();
            LogRevision(tab, record(loaded, at));

            tab.BpmnXml = loaded;
            tab.Dirty = true;
            selectedElement = null;
            await RefreshActiveProcessId();
        }
        catch (Exception ex)
        {
            LogLoadBpmnForTabFailed(tab.Id, ex);
            errorMessage = $"Failed to load the proposal: {ex.Message}";
            await PersistHistoryAsync(tab);
            return;
        }
        finally
        {
            changingDiagram = false;
        }

        await PersistHistoryAsync(tab);
        await UpdateDirtyFlagAndPersist();
    }

    private void LogRevision(TabSession tab, AssistantRevision? revision)
    {
        if (revision is not null)
            LogAssistantRevisionRecorded(tab.Id, revision.Number, revision.Kind, tab.Changes.Revisions.Count, tab.Changes.Dropped);
    }

    private async Task SetAssistantAutoApplyAsync(bool on)
    {
        assistantAutoApply = on;
        // Only a convenience: without storage the switch still works until the next reload.
        await JS.InvokeAsync<bool>("bpmnEditor.writeStorage", AssistantAutoApplyStorageKey, on ? "true" : "false");
    }

    // ---- History storage ------------------------------------------------------------------

    private static string HistoryStorageKey(string tabId) => AssistantHistoryStoragePrefix + tabId;

    // A rejected write (quota) leaves the history in circuit memory and warns in the panel, like the tabs payload.
    private async Task PersistHistoryAsync(TabSession tab)
    {
        try
        {
            var ok = await JS.InvokeAsync<bool>("bpmnEditor.writeSessionStorage", HistoryStorageKey(tab.Id), tab.Changes.Serialize());
            tab.ChangesPersistFailed = !ok;
            if (!ok)
                LogAssistantHistoryWriteRejected(tab.Id);
        }
        catch (Exception ex)
        {
            tab.ChangesPersistFailed = true;
            LogAssistantHistoryWriteFailed(ex);
        }
    }

    // The switch and the restored tabs' histories. Read once per circuit, right after the tabs.
    private async Task RestoreAssistantStateAsync()
    {
        if (!AssistantConfig.Value.IsEnabled) return;
        try
        {
            assistantAutoApply = await JS.InvokeAsync<string?>("bpmnEditor.readStorage", AssistantAutoApplyStorageKey) != "false";
            foreach (var tab in tabs)
            {
                var json = await ReadHistoryJsonAsync(tab.Id);
                if (json.Length == 0) continue;
                if (AssistantChangeHistory.Deserialize(json) is { } history)
                    tab.Changes = history;
                else
                    LogAssistantHistoryUnreadable(tab.Id);
            }
        }
        catch (Exception ex)
        {
            LogAssistantHistoryReadFailed(ex);
        }
    }

    // As a byte stream too: a history is far over the SignalR receive limit.
    private async Task<string> ReadHistoryJsonAsync(string tabId)
    {
        var bytes = await JS.InvokeAsync<IJSStreamReference>("bpmnEditor.readSessionStorageBytes", HistoryStorageKey(tabId));
        await using (bytes)
        {
            await using var stream = await bytes.OpenReadStreamAsync(MaxHistoryBytes);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
    }

    private async Task ForgetHistoryAsync(TabSession tab)
    {
        if (!AssistantConfig.Value.IsEnabled) return;
        try
        {
            await JS.InvokeVoidAsync("bpmnEditor.removeSessionStorage", HistoryStorageKey(tab.Id));
        }
        catch (Exception ex)
        {
            LogAssistantHistoryWriteFailed(ex);
        }
    }
}
