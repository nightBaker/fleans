using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/12-variable-scoping/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class VariableScopingTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ParallelBranches_GetIsolatedScopes_MergeAtJoin()
    {
        var xml = BpmnFixtureLoader.Load("12-variable-scoping", "parallel-variable-isolation.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        // Per the manual plan: branch scopes should be removed after join, leaving only the merged scope.
        Assert.HasCount(1, state.VariableStates,
            $"Expected exactly 1 merged variable scope after join, but found {state.VariableStates.Count}.");

        Assert.IsTrue(state.TryGetVariable("shared", out _),
            "Merged scope should contain 'shared'.");
    }
}
