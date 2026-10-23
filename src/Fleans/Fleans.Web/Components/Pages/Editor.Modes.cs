using Fleans.Application.QueryModels;
using Microsoft.JSInterop;

namespace Fleans.Web.Components.Pages;

// Editor modes (design: "Modeling · Simulation · XML · History"). Modeling edits; XML edits the text
// (Editor.razor, the text stays in the browser); Simulation and History lock the canvas read-only
// (bpmnEditor.setReadOnly) and take over the inspector column.
public partial class Editor
{
    private enum EditorMode { Modeling, Simulation, Xml, History }

    private EditorMode mode;
    private bool xmlMode => mode == EditorMode.Xml;
    private bool ReadOnlyMode => mode is EditorMode.Simulation or EditorMode.History;
    private bool persistFailed;

    private SimulationSnapshot? simulation;

    private List<ProcessDefinitionSummary>? historyVersions;
    private int? compareVersion;
    private ComparisonResult? comparison;
    private string? historyError;
    private bool historyLoading;

    private async Task SwitchMode(EditorMode target)
    {
        if (!editorInitialized || mode == target) return;

        if (mode == EditorMode.Xml)
        {
            // Applying the edited text is what "back to Modeling" means; any other mode would drop it.
            if (xmlEdited && target != EditorMode.Modeling)
            {
                errorMessage = XmlPendingHint + ".";
                return;
            }
            ShowModel();
        }
        else if (ReadOnlyMode)
        {
            await LeaveReadOnlyModeAsync();
        }

        switch (target)
        {
            case EditorMode.Xml:
                await ShowXml();
                break;
            case EditorMode.Simulation:
                await StartSimulationAsync(null);
                break;
            case EditorMode.History:
                await EnterHistoryAsync();
                break;
        }
    }

    // Tab and file actions replace the diagram, so they return to Modeling first. XML mode with
    // unapplied edits refuses (the edits would be lost) — its buttons are disabled meanwhile.
    private async Task<bool> TryReturnToModelingAsync()
    {
        if (mode == EditorMode.Xml) return TryLeaveXmlMode();
        if (ReadOnlyMode) await LeaveReadOnlyModeAsync();
        return true;
    }

    private async Task LeaveReadOnlyModeAsync()
    {
        if (mode == EditorMode.Simulation)
            await JS.InvokeVoidAsync("bpmnEditor.simulation", "stop");
        if (mode == EditorMode.History)
            await JS.InvokeVoidAsync("bpmnEditor.clearComparison");
        await JS.InvokeVoidAsync("bpmnEditor.setReadOnly", false);
        mode = EditorMode.Modeling;
        pendingCanvasRefresh = true;
        simulation = null;
        comparison = null;
    }

    // ---- Simulation -----------------------------------------------------------------------

    private async Task StartSimulationAsync(string? fromElementId)
    {
        mode = EditorMode.Simulation;
        simulation = null;
        // The run's bar takes a row above the canvas: let bpmn-js re-measure after the render.
        pendingCanvasRefresh = true;
        try
        {
            await JS.InvokeVoidAsync("bpmnEditor.setReadOnly", true);
            await JS.InvokeVoidAsync("bpmnEditor.simulation", "start", fromElementId);
        }
        catch (JSException ex)
        {
            Logger.LogError(ex, "Failed to start the simulation");
            errorMessage = $"Couldn't start the simulation: {ex.Message}";
            await LeaveReadOnlyModeAsync();
        }
    }

    [JSInvokable]
    public Task OnSimulationUpdate(SimulationSnapshot snapshot)
    {
        if (mode != EditorMode.Simulation) return Task.CompletedTask;
        simulation = snapshot;
        return InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task OnSimulateFromRequested(string elementId) => InvokeAsync(async () =>
    {
        if (!await TryReturnToModelingAsync()) return;
        await StartSimulationAsync(elementId);
        StateHasChanged();
    });

    private Task SimCommand(string command, object? arg = null, object? arg2 = null) =>
        JS.InvokeVoidAsync("bpmnEditor.simulation", command, arg, arg2).AsTask();

    private Task RestartSimulation() => SimCommand("start");

    private Task ToggleSimulation() => SimCommand(simulation?.Running == true ? "pause" : "resume");

    private Task ChooseSimulationOption(int tokenId, string optionId) => SimCommand("choose", tokenId, optionId);

    private static string FormatElapsed(long ms) =>
        (ms / 1000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";

    // ---- History --------------------------------------------------------------------------

    private async Task EnterHistoryAsync()
    {
        mode = EditorMode.History;
        pendingCanvasRefresh = true;
        comparison = null;
        historyError = null;
        historyVersions = null;
        await JS.InvokeVoidAsync("bpmnEditor.setReadOnly", true);

        var key = ActiveTab?.ProcessKey;
        if (string.IsNullOrEmpty(key))
        {
            historyVersions = [];
            return;
        }

        historyLoading = true;
        try
        {
            var page = await QueryService.GetProcessDefinitionGroups(
                new PageRequest(Page: 1, PageSize: 1, Filters: $"ProcessDefinitionKey=={key}"));
            historyVersions = page.Items.FirstOrDefault()?.Versions
                .OrderByDescending(v => v.Version)
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load versions for {Key}", key);
            historyError = $"Couldn't load versions: {ex.Message}";
            historyVersions = [];
        }
        finally
        {
            historyLoading = false;
        }

        // Default comparison: the version this tab was opened from, else the latest deployed one.
        var baseVersion = ActiveTab?.Version ?? historyVersions.FirstOrDefault()?.Version;
        if (baseVersion is { } v) await CompareWithVersionAsync(v);
    }

    private async Task CompareWithVersionAsync(int version)
    {
        var key = ActiveTab?.ProcessKey;
        if (mode != EditorMode.History || string.IsNullOrEmpty(key)) return;
        compareVersion = version;
        historyError = null;
        try
        {
            var xml = await QueryService.GetBpmnXmlByKeyAndVersion(key, version);
            if (string.IsNullOrEmpty(xml))
            {
                historyError = $"v{version} has no stored BPMN.";
                comparison = null;
                return;
            }
            comparison = await JS.InvokeAsync<ComparisonResult?>("bpmnEditor.compareWith", xml);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to compare with {Key} v{Version}", key, version);
            historyError = $"Couldn't compare with v{version}: {ex.Message}";
            comparison = null;
        }
    }

    private async Task OpenVersionInTab(int version)
    {
        var key = ActiveTab?.ProcessKey;
        if (string.IsNullOrEmpty(key) || !await TryReturnToModelingAsync()) return;
        await OpenFromWorkflow(key, version);
        await PersistTabs();
    }

    private static string ChangeSymbol(string kind) => kind switch
    {
        "added" => "+",
        "removed" => "−",
        _ => "~"
    };

    // ---- Payloads from js/bpmnEditor.js ----------------------------------------------------

    public sealed class SimulationSnapshot
    {
        public bool Running { get; set; }
        public bool Finished { get; set; }
        public bool Started { get; set; }
        public int Step { get; set; }
        public long ElapsedMs { get; set; }
        public double Speed { get; set; } = 1;
        public int ActiveTokens { get; set; }
        public int Visited { get; set; }
        public List<SimulationPending> Pending { get; set; } = new();
        public List<SimulationLogEntry> Log { get; set; } = new();
    }

    public sealed class SimulationPending
    {
        public int TokenId { get; set; }
        public string ElementId { get; set; } = "";
        public string ElementName { get; set; } = "";
        public string ElementType { get; set; } = "";
        public List<SimulationOption> Options { get; set; } = new();
    }

    public sealed class SimulationOption
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public bool Primary { get; set; }
    }

    public sealed class SimulationLogEntry
    {
        public int Step { get; set; }
        public long AtMs { get; set; }
        public string Text { get; set; } = "";
        public string? ElementId { get; set; }
    }

    public sealed class ComparisonResult
    {
        public int Added { get; set; }
        public int Changed { get; set; }
        public int Removed { get; set; }
        public List<ComparisonItem> Items { get; set; } = new();
    }

    public sealed class ComparisonItem
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public List<ComparisonField> Fields { get; set; } = new();
    }

    public sealed class ComparisonField
    {
        public string Field { get; set; } = "";
        public string OldValue { get; set; } = "";
        public string NewValue { get; set; } = "";
    }
}
