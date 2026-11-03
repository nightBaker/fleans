using System.Text;
using Fleans.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Fleans.Web.Components.Pages;

// AI assistant panel (#1015): the chat lives in AssistantPanel; the editor reads the diagram for it
// and applies a proposal to a new tab or the active one. Nothing here deploys.
public partial class Editor
{
    // Same cap as Import BPMN.
    private const long MaxAssistantXmlBytes = 10 * 1024 * 1024;

    [Inject]
    private IOptions<AssistantOptions> AssistantConfig { get; set; } = null!;

    [Inject]
    private IStringLocalizer<AssistantPanel> AssistantL { get; set; } = null!;

    private bool assistantOpen;

    private bool AssistantVisible => assistantOpen && AssistantConfig.Value.IsEnabled;

    private void ToggleAssistant() => assistantOpen = !assistantOpen;

    // Read as a byte stream: getXml's string return value is capped by the SignalR receive limit.
    private async Task<string?> GetXmlForAssistantAsync()
    {
        // A blank tab (at most its start event) is a new diagram: the contract sends null.
        if (summary.Elements <= 1 && summary.Flows == 0) return null;

        var bytes = await JS.InvokeAsync<IJSStreamReference>("bpmnEditor.getXmlBytes");
        await using (bytes)
        {
            await using var stream = await bytes.OpenReadStreamAsync(MaxAssistantXmlBytes);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var xml = await reader.ReadToEndAsync();
            return xml.Length == 0 ? null : xml;
        }
    }

    private async Task OpenProposalInNewTabAsync(string xml)
    {
        if (!EnsureCapacity() || !await TryReturnToModelingAsync()) return;
        await SaveActiveTabXml();

        // The new tab continues the conversation, so a follow-up ("now add a timer") works there.
        var tab = new TabSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = "",
            ProcessKey = "",
            Version = null,
            BpmnXml = xml,
            Dirty = true,
            Assistant = ActiveTab?.Assistant.Clone() ?? new AssistantConversation(),
        };
        tabs.Add(tab);
        await ActivateTab(tab.Id, saveOutgoing: false);
        if (string.IsNullOrEmpty(tab.Label))
            tab.Label = $"Assistant-{tabs.Count}";
        await UpdateDirtyFlagAndPersist();
    }

    private async Task ReplaceWithProposalAsync(string xml)
    {
        var tab = ActiveTab;
        if (tab == null || !await TryReturnToModelingAsync()) return;

        try
        {
            // loadXml re-imports, which also clears bpmn-js' undo stack.
            await JS.InvokeVoidAsync("bpmnEditor.loadXml", xml);
        }
        catch (Exception ex)
        {
            LogLoadBpmnForTabFailed(tab.Id, ex);
            errorMessage = $"Failed to load the proposal: {ex.Message}";
            return;
        }

        tab.BpmnXml = xml;
        tab.Dirty = true;
        selectedElement = null;
        await RefreshActiveProcessId();
        await UpdateDirtyFlagAndPersist();
    }
}
