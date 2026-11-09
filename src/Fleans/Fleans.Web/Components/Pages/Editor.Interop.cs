using System.Text;
using Microsoft.JSInterop;

namespace Fleans.Web.Components.Pages;

public partial class Editor
{
    // Same limit as Import BPMN.
    private const long MaxDiagramXmlBytes = 10 * 1024 * 1024;

    // Read as a byte stream: getXml's string return value is capped by the SignalR receive limit.
    private async Task<string> ReadModelerXmlAsync()
    {
        var bytes = await JS.InvokeAsync<IJSStreamReference>("bpmnEditor.getXmlBytes");
        await using (bytes)
        {
            await using var stream = await bytes.OpenReadStreamAsync(MaxDiagramXmlBytes);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
    }
}

