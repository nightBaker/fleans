using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/02-script-tasks/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class ScriptTaskTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ScriptVariables_Deploy_Start_VerifyVariablesAndCompletedActivities()
    {
        var xml = BpmnFixtureLoader.Load("02-script-tasks", "script-variable-manipulation.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("start", "setVar", "incrementVar", "createSecondVar", "end");
        Assert.HasCount(5, state.CompletedActivityIds);
        Assert.IsEmpty(state.ActiveActivityIds);
        state.AssertVariableEquals("x", "15");
        state.AssertVariableEquals("greeting", "hello");
    }
}
