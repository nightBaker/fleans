using Fleans.Mcp.Tools;
using ModelContextProtocol.Server;
using System.Reflection;

namespace Fleans.Mcp.Tests;

[TestClass]
public class McpToolRegistrationTests
{
    [TestMethod]
    public void AllToolClasses_HaveMcpServerToolTypeAttribute()
    {
        var toolTypes = typeof(WorkflowTools).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .ToList();

        Assert.IsTrue(toolTypes.Count >= 2, $"Expected at least 2 tool classes, found {toolTypes.Count}");
        CollectionAssert.Contains(toolTypes, typeof(WorkflowTools));
        CollectionAssert.Contains(toolTypes, typeof(InstanceTools));
    }

    [TestMethod]
    public void WorkflowTools_ExposesExpectedTools()
    {
        var tools = typeof(WorkflowTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        Assert.AreEqual(2, tools.Count, $"Expected 2 tools, found: {string.Join(", ", tools)}");
        CollectionAssert.Contains(tools, nameof(WorkflowTools.DeployWorkflow));
        CollectionAssert.Contains(tools, nameof(WorkflowTools.ListDefinitions));
    }

    [TestMethod]
    public void InstanceTools_ExposesExpectedTools()
    {
        var tools = typeof(InstanceTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => m.Name)
            .ToList();

        Assert.AreEqual(2, tools.Count, $"Expected 2 tools, found: {string.Join(", ", tools)}");
        CollectionAssert.Contains(tools, nameof(InstanceTools.GetInstanceState));
        CollectionAssert.Contains(tools, nameof(InstanceTools.ListInstances));
    }

    [TestMethod]
    public async Task GetInstanceState_ThrowsMcpException_ForInvalidGuid()
    {
        var ex = await Assert.ThrowsExactlyAsync<ModelContextProtocol.McpException>(
            () => InstanceTools.GetInstanceState(null!, "not-a-guid"));

        Assert.IsTrue(ex.Message.Contains("Invalid GUID format"));
    }
}
