using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Fleans.Application;
using Fleans.Infrastructure.Bpmn;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fleans.Mcp.Tools;

[McpServerToolType]
public static class WorkflowTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description("Deploy a BPMN XML workflow definition to the engine. Returns the process definition ID, key, version, and activity/flow counts.")]
    public static async Task<string> DeployWorkflow(
        IBpmnConverter bpmnConverter,
        IWorkflowCommandService commandService,
        [Description("The complete BPMN 2.0 XML string to deploy")] string bpmnXml)
    {
        if (string.IsNullOrWhiteSpace(bpmnXml))
            throw new McpException("BPMN XML cannot be null or empty");

        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml));
            var workflow = await bpmnConverter.ConvertFromXmlAsync(stream);
            var summary = await commandService.DeployWorkflow(workflow, bpmnXml);
            return JsonSerializer.Serialize(summary, JsonOptions);
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to deploy workflow: {ex.Message}", ex);
        }
    }

    [McpServerTool, Description("List all deployed process definitions. Returns an array of definitions with their IDs, keys, versions, and activity counts.")]
    public static async Task<string> ListDefinitions(
        IWorkflowQueryService queryService)
    {
        try
        {
            var definitions = await queryService.GetAllProcessDefinitions();
            return JsonSerializer.Serialize(definitions, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to list definitions: {ex.Message}", ex);
        }
    }
}
