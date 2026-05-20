using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/26-transaction-subprocess/test-plan.md (happy path)
[TestClass]
[TestCategory("E2E")]
public class TransactionSubprocessTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task TransactionHappyPath_VariablesMergedAfterTransactionScope()
    {
        var xml = BpmnFixtureLoader.Load("26-transaction-subprocess", "happy-path.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        state.AssertCompletedActivities(
            "setupTask", "validateTask", "processTask", "tx_end", "confirmTask", "end");
        state.AssertVariableEquals("requestId", "tx-001");
        state.AssertVariableEquals("amount", "100");
        state.AssertVariableEquals("validated", "True");
        state.AssertVariableEquals("processed", "True");
        state.AssertVariableEquals("result", "SUCCESS");
        state.AssertVariableEquals("confirmed", "True");
    }
}
