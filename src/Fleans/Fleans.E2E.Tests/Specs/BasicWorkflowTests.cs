using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/01-basic-workflow/test-plan.md
//
// Phase 1 pilot — driving deploy + start via the API rather than the bpmn-js editor.
// "Deploy via drag-drop in the editor" and "click Start on the Workflows row" are NOT
// covered here; they will land in a follow-up spec once the EditorPage + WorkflowsListPage
// page objects are written. Until then, the manual plan stays in tests/manual/ (do not
// archive yet).
[TestClass]
[TestCategory("E2E")]
public class BasicWorkflowTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task Deploy_Start_VerifyAllActivitiesComplete()
    {
        // Arrange — deploy the fixture and start an instance via API
        var bpmnXml = BpmnFixtureLoader.Load("01-basic-workflow", "simple-workflow.bpmn");
        var deployed = await ApiClient.DeployAsync(bpmnXml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        // Assert — instance reaches IsCompleted, with 3 completed activities and none active/failed
        var state = await ApiClient.WaitForCompletionAsync(started.WorkflowInstanceId);
        Assert.IsTrue(state.IsCompleted, "Workflow instance should be marked IsCompleted.");
        Assert.IsFalse(state.IsCancelled, "Workflow instance should not be marked IsCancelled.");
        Assert.IsEmpty(state.ActiveActivityIds,
            $"Expected no active activities, but found: [{string.Join(",", state.ActiveActivityIds)}].");
        Assert.HasCount(3, state.CompletedActivityIds,
            $"Expected 3 completed activities (start, task1, end), but found: " +
            $"[{string.Join(",", state.CompletedActivityIds)}].");

        // UI verification — instance detail page reflects the Completed status
        var detailsPage = new InstanceDetailsPage(Page);
        await detailsPage.OpenAsync(started.WorkflowInstanceId);
        await detailsPage.AssertCompletedAsync(started.WorkflowInstanceId);
    }
}
