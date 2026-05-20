using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/06-call-activity/test-plan.md
[TestClass]
[TestCategory("E2E")]
public class CallActivityTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task ParentCallsChild_ResultMappedBack_BothProcessesComplete()
    {
        // Deploy child first (parent references calledElement="child-process")
        var childXml = BpmnFixtureLoader.Load("06-call-activity", "child-process.bpmn");
        await ApiClient.DeployAsync(childXml);

        var parentXml = BpmnFixtureLoader.Load("06-call-activity", "parent-process.bpmn");
        var parent = await ApiClient.DeployAsync(parentXml);
        var started = await ApiClient.StartAsync(parent.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities("parentStart", "setInput", "callChild", "parentEnd");
        state.AssertVariableEquals("input", "21");
        state.AssertVariableEquals("result", "42");
    }
}
