using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/26-transaction-subprocess/test-plan.md (happy path) and the hazard path
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

    [TestMethod]
    public async Task TransactionHazard_ErrorEscapingTheTransaction_TakesTheErrorBoundaryPath()
    {
        // Arrange — processTask throws inside the transaction; a catch-all error boundary sits on it
        var xml = BpmnFixtureLoader.Load("26-transaction-subprocess", "hazard-path.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);

        // Act
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);

        // Assert
        state.AssertCompletedActivities("setupTask", "validateTask", "recoveryTask", "errorEnd");
        CollectionAssert.DoesNotContain(state.CompletedActivityIds, "end", "the normal path is not taken");
        state.AssertVariableEquals("errorHandled", "True");
        state.AssertVariableEquals("fallbackResult", "DECLINED");
    }
}
