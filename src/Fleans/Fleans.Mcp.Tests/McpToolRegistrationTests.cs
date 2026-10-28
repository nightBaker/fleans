using Fleans.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Fleans.Mcp.Tests;

[TestClass]
public class McpToolRegistrationTests
{
    // Same discovery path as Program.cs, so a tool that loses its attributes or gets renamed
    // disappears from (or changes in) the list MCP clients see.
    [TestMethod]
    public void WithToolsFromAssembly_RegistersTheExpectedTools()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithToolsFromAssembly(typeof(WorkflowTools).Assembly);
        using var provider = services.BuildServiceProvider();

        var names = provider.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).Order().ToList();

        CollectionAssert.AreEqual(
            new[] { "deploy_workflow", "get_instance_state", "list_definitions", "list_instances" },
            names,
            $"Registered: {string.Join(", ", names)}");
    }

    [TestMethod]
    public async Task GetInstanceState_ThrowsMcpException_ForInvalidGuid()
    {
        var ex = await Assert.ThrowsExactlyAsync<ModelContextProtocol.McpException>(
            () => InstanceTools.GetInstanceState(null!, "not-a-guid"));

        Assert.IsTrue(ex.Message.Contains("Invalid GUID format"));
    }
}
