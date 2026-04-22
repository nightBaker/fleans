using System.Text.Json;
using Fleans.Application.QueryModels;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Fleans.Application.Tests;

// Locks the JSON wire shape that k6 load scripts depend on.
// If Program.cs adds AddJsonOptions(...), it propagates here via the resolved JsonOptions.
// A dedicated Fleans.Api.Tests project would be the more principled home; this is pragmatic
// given only three tests need the MVC pipeline's JsonSerializerOptions.
[TestClass]
public class JsonContractTests
{
    private static readonly JsonSerializerOptions ApiOptions = BuildApiOptions();

    private static JsonSerializerOptions BuildApiOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
    }

    [TestMethod]
    public void StartWorkflowResponse_Serialises_WithCamelCaseKey()
    {
        var dto = new StartWorkflowResponse(Guid.NewGuid());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, ApiOptions));
        Assert.IsTrue(doc.RootElement.TryGetProperty("workflowInstanceId", out _),
            "Expected camelCase 'workflowInstanceId' — the k6 load script reads this exact key.");
    }

    [TestMethod]
    public void InstanceStateSnapshot_Serialises_WithCamelCaseKeys()
    {
        var dto = new InstanceStateSnapshot(
            ActiveActivityIds: new() { "waitMessage" },
            CompletedActivityIds: new() { "start" },
            IsStarted: true,
            IsCompleted: false,
            IsCancelled: false,
            ActiveActivities: new(),
            CompletedActivities: new(),
            VariableStates: new(),
            ConditionSequences: new(),
            ProcessDefinitionId: "load-events",
            CreatedAt: null,
            ExecutionStartedAt: null,
            CompletedAt: null);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, ApiOptions));
        foreach (var key in new[] { "activeActivityIds", "completedActivityIds", "isStarted", "isCompleted" })
            Assert.IsTrue(doc.RootElement.TryGetProperty(key, out _),
                $"Expected camelCase '{key}' — the k6 load script reads this exact key.");
    }

    [TestMethod]
    public void ErrorResponse_Serialises_WithCamelCaseErrorKey()
    {
        var dto = new ErrorResponse("Instance abc123 not found");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(dto, ApiOptions));
        Assert.IsTrue(doc.RootElement.TryGetProperty("error", out _),
            "Expected camelCase 'error' — ErrorResponse.Error is the established wire field across all controller error paths.");
    }
}
