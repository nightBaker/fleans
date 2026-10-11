using Microsoft.Playwright;

namespace Fleans.E2E.Tests.PageObjects;

/// <summary>
/// Wraps Fleans.Web's <c>/editor</c> Razor page. Drives the underlying bpmn-js modeler
/// via its <c>window.bpmnEditor.*</c> JS API rather than DOM clicks / drag-drop —
/// editor-UI plans verify properties-panel field round-trips, which are equally well
/// exercised by reading the model state after a programmatic update.
/// </summary>
public sealed class EditorPage
{
    private readonly IPage _page;

    public EditorPage(IPage page)
    {
        _page = page;
    }

    public async Task OpenAsync()
    {
        await _page.GotoAsync("/editor");
        // Wait for Editor.razor's whole OnAfterRenderAsync boot sequence, not just for
        // window.bpmnEditor._modeler to exist. The modeler is created by the first JS
        // interop call, but the boot sequence then keeps going over the SignalR circuit
        // (restore tabs → AddBlankTab → bpmnEditor.newDiagram → loadXml). A LoadXmlAsync
        // issued in that window is silently overwritten by the blank diagram, after which
        // elementRegistry.get(id) returns null (#773 — deterministic on slower Linux CI
        // runners, intermittent on macOS). data-editor-ready is rendered "true" only
        // after the boot sequence finished, so it is a deterministic signal.
        await _page.Locator(".editor-page[data-editor-ready='true']").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 30_000 });
    }

    public async Task LoadXmlAsync(string bpmnXml)
    {
        await _page.EvaluateAsync("xml => window.bpmnEditor.loadXml(xml)", bpmnXml);
    }

    public async Task<string> GetXmlAsync()
    {
        return await _page.EvaluateAsync<string>("() => window.bpmnEditor.getXml()");
    }

    /// <summary>
    /// Reads a single string-valued property off the bpmn-js property bag returned by
    /// <c>window.bpmnEditor.getElementProperties(elementId)</c>. Using a typed extractor
    /// avoids deserialising the whole property bag through Playwright's JSON converter
    /// (which can't materialise an <see cref="IDictionary{TKey, TValue}"/>).
    /// </summary>
    public async Task<string> GetStringPropertyAsync(string elementId, string propertyName)
    {
        var script = $@"() => {{
            try {{
                const p = window.bpmnEditor.getElementProperties({System.Text.Json.JsonSerializer.Serialize(elementId)});
                if (!p) return 'null-props';
                const v = p[{System.Text.Json.JsonSerializer.Serialize(propertyName)}];
                if (v == null) return 'null-value';
                return String(v);
            }} catch (e) {{
                return 'js-error: ' + (e && e.message);
            }}
        }}";
        return await _page.EvaluateAsync<string>(script);
    }

    public async Task UpdateActivationConditionAsync(string elementId, string? expression)
    {
        var script = $@"() => {{
            try {{
                window.bpmnEditor.updateActivationCondition(
                    {System.Text.Json.JsonSerializer.Serialize(elementId)},
                    {System.Text.Json.JsonSerializer.Serialize(expression)});
                return 'ok';
            }} catch (e) {{
                return 'error: ' + (e && e.message);
            }}
        }}";
        var result = await _page.EvaluateAsync<string>(script);
        if (result != "ok")
        {
            throw new InvalidOperationException($"updateActivationCondition failed in JS: {result}");
        }
    }

    public async Task UpdateDefaultFlowAsync(string gatewayId, string? sequenceFlowId)
    {
        await _page.EvaluateAsync(
            "args => window.bpmnEditor.updateDefaultFlow(args.id, args.flow)",
            new { id = gatewayId, flow = sequenceFlowId });
    }

    public Task<string> GetDefaultFlowAsync(string gatewayId) =>
        GetStringPropertyAsync(gatewayId, "defaultFlow");

    public Task<string> GetActivationConditionAsync(string elementId) =>
        GetStringPropertyAsync(elementId, "activationCondition");
}
