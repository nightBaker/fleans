using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/02-script-tasks/ (manual plan retired — automated here)
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

    [TestMethod]
    public async Task JavaScriptScript_Deploy_Start_VerifyVariables()
    {
        // #1070: a scriptFormat="javascript" task reads a C# task's variable and writes back
        var xml = BpmnFixtureLoader.Load("02-script-tasks", "javascript-variables.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("start", "seed", "compute", "end");
        Assert.IsEmpty(state.ActiveActivityIds);
        state.AssertVariableEquals("x", "24");
        state.AssertVariableEquals("greeting", "hello 24");
    }

    [TestMethod]
    public async Task PythonScript_Deploy_Start_VerifyVariables()
    {
        // #1102: an indented scriptFormat="python" task reads a C# task's variable and writes back
        var xml = BpmnFixtureLoader.Load("02-script-tasks", "python-variables.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("start", "seed", "compute", "end");
        Assert.IsEmpty(state.ActiveActivityIds);
        state.AssertVariableEquals("x", "24");
        state.AssertVariableEquals("greeting", "hello 24");
    }
}
