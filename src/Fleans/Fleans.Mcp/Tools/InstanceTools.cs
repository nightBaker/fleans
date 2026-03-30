using System.ComponentModel;
using System.Text.Json;
using Fleans.Application;
using Fleans.Application.QueryModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Fleans.Mcp.Tools;

[McpServerToolType]
public static class InstanceTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [McpServerTool, Description("Get the full state snapshot of a workflow instance including active/completed activities, variables, condition sequences, and timestamps.")]
    public static async Task<string> GetInstanceState(
        IWorkflowQueryService queryService,
        [Description("The workflow instance ID (GUID format)")] string instanceId)
    {
        if (!Guid.TryParse(instanceId, out var id))
            throw new McpException($"Invalid GUID format: {instanceId}");

        try
        {
            var snapshot = await queryService.GetStateSnapshot(id);
            if (snapshot is null)
                throw new McpException($"Workflow instance not found: {instanceId}");

            return JsonSerializer.Serialize(snapshot, JsonOptions);
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to get instance state: {ex.Message}", ex);
        }
    }

    [McpServerTool, Description("List workflow instances for a given process definition key with optional pagination. Returns paginated results with instance IDs, status, and timestamps.")]
    public static async Task<string> ListInstances(
        IWorkflowQueryService queryService,
        [Description("The process definition key (human-readable identifier, e.g. 'my-process')")] string processDefinitionKey,
        [Description("Page number (1-based, default: 1)")] int page = 1,
        [Description("Items per page (1-100, default: 50)")] int pageSize = 50,
        [Description("Sieve sort expression (e.g. '-CreatedAt' for newest first)")] string? sorts = null,
        [Description("Sieve filter expression (e.g. 'IsCompleted==true')")] string? filters = null)
    {
        try
        {
            var request = new PageRequest(page, pageSize, sorts, filters);
            var result = await queryService.GetInstancesByKey(processDefinitionKey, request);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new McpException($"Failed to list instances: {ex.Message}", ex);
        }
    }
}
