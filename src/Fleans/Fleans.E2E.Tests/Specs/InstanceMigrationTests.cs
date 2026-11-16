using System.Net;
using System.Net.Http.Json;
using Fleans.Application.QueryModels;
using Fleans.Domain.InstanceMigration;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Automates tests/manual/81-instance-migration/ scenarios 1–4 and 6 (#1069). Each test gives the
// fixture process a per-run key, so the first deploy is v1 (the E2E database outlives runs).
[TestClass]
[TestCategory("E2E")]
public class InstanceMigrationTests : WorkflowE2ETestBase
{
    private const string PlanFolder = "81-instance-migration";
    private const string TaskUser = "migration-e2e-user";

    [TestMethod]
    public async Task Api_PreviewDryRunMigrate_InstancesFinishOnTheTargetVersion()
    {
        // Arrange: two instances wait in v1's 'review'; v2 renames it 'approve'.
        var key = Key();
        var v1 = await ApiClient.DeployAsync(Load("migration-order-v1.bpmn", key));
        var a = (await ApiClient.StartAsync(key)).WorkflowInstanceId;
        var b = (await ApiClient.StartAsync(key)).WorkflowInstanceId;
        await WaitForActive(a, "review");
        var bBefore = await WaitForActive(b, "review");
        await ApiClient.DeployAsync(Load("migration-order-v2.bpmn", key));

        // Preview: 'review' has no same-id element in v2; 'approve' is offered.
        var preview = await WaitForRunningCount(key, 2);
        Assert.AreEqual(MigrationRejectionCodes.UnmappedActivity, preview.Elements.Single(e => e.SourceActivityId == "review").Problem);
        Assert.IsTrue(preview.TargetElements.Any(e => e.ActivityId == "approve" && e.ActivityType == "UserTask"));
        var v1Id = preview.SourceProcessDefinitionId;
        var v2Id = preview.TargetProcessDefinitionId;

        // Without the mapping a single-instance migration is rejected (409) and changes nothing.
        using (var rejected = await ApiClient.MigrateInstanceRawAsync(a, new MigrateInstanceRequest(v2Id)))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, rejected.StatusCode);
            var body = await rejected.Content.ReadFromJsonAsync<InstanceMigrationResultDto>();
            Assert.AreEqual(MigrationRejectionCodes.UnmappedActivity, body!.Rejections.Single().Code);
        }

        // Act: dry run, then migrate every running v1 instance with review → approve.
        List<MigrationMappingDto> mappings = [new("review", "approve")];
        var requestId = $"e2e-{Guid.NewGuid():N}";
        var dryRun = await ApiClient.MigrateInstancesAsync(new MigrateInstancesRequest(null, v1Id, v2Id, mappings, DryRun: true, RequestId: requestId));
        var migrated = await ApiClient.MigrateInstancesAsync(new MigrateInstancesRequest(null, v1Id, v2Id, mappings, RequestId: requestId));
        var retried = await ApiClient.MigrateInstancesAsync(new MigrateInstancesRequest([a, b], null, v2Id, mappings, RequestId: requestId));

        // Assert
        Assert.AreEqual(2, dryRun.Outcomes.GetValueOrDefault("Migratable"));
        Assert.AreEqual(2, migrated.Outcomes.GetValueOrDefault("Migrated"));
        Assert.IsNull(migrated.ContinuationToken);
        Assert.AreEqual(2, retried.Outcomes.GetValueOrDefault("AlreadyMigrated"), "a retried request is a no-op");

        var bAfter = await WaitForActive(b, "approve");
        Assert.AreEqual(v2Id, bAfter.ProcessDefinitionId);
        Assert.AreEqual(bBefore.ActiveActivities.Single(x => x.ActivityId == "review").ActivityInstanceId,
            bAfter.ActiveActivities.Single(x => x.ActivityId == "approve").ActivityInstanceId, "the wait kept its instance");
        var task = await ApiClient.WaitForUserTaskAsync(bAfter.ActiveActivities.Single(x => x.ActivityId == "approve").ActivityInstanceId,
            t => t.ActivityId == "approve");
        Assert.IsNotNull(task, "the user-task read model follows the rename");

        foreach (var id in new[] { a, b })
        {
            await CompleteUserTask(id, "approve");
            var final = await ApiClient.WaitForCompletionAsync(id);
            final.AssertCompletedActivities("start", "approve", "v2Finish", "v2End");
            final.AssertNotCompleted("v1Finish", "v1End");
            final.AssertVariableEquals("finishedBy", "v2");
        }
    }

    [TestMethod]
    public async Task Api_TypeChanged_Rejected_InstanceStaysOnV1()
    {
        var key = Key();
        await ApiClient.DeployAsync(Load("migration-order-v1.bpmn", key));
        var id = (await ApiClient.StartAsync(key)).WorkflowInstanceId;
        var before = await WaitForActive(id, "review");
        await ApiClient.DeployAsync(Load("migration-order-v3-script.bpmn", key));
        var preview = await ApiClient.GetMigrationPreviewAsync(key, 1, 2);

        var result = await ApiClient.MigrateInstancesAsync(new MigrateInstancesRequest([id], null, preview.TargetProcessDefinitionId));

        Assert.AreEqual(1, result.RejectionCodes.GetValueOrDefault(MigrationRejectionCodes.TypeChanged));
        await CompleteUserTask(id, "review");
        var final = await ApiClient.WaitForCompletionAsync(id);
        Assert.AreEqual(before.ProcessDefinitionId, final.ProcessDefinitionId);
        final.AssertVariableEquals("finishedBy", "v1");
    }

    [TestMethod]
    public async Task Ui_InstancePage_MigrateDialog_MovesTheInstance()
    {
        // Arrange
        var key = Key();
        await ApiClient.DeployAsync(Load("migration-order-v1.bpmn", key));
        var id = (await ApiClient.StartAsync(key)).WorkflowInstanceId;
        await WaitForActive(id, "review");
        await ApiClient.DeployAsync(Load("migration-order-v2.bpmn", key));
        var instancePage = new InstanceDetailsPage(Page);
        await instancePage.OpenAsync(id);

        // Act: open the dialog, map review → approve, dry run, migrate.
        await Page.GetByTestId("instance-migrate").ClickAsync();
        var dialog = Page.GetByTestId("migrate-dialog");
        await Expect(dialog.GetByTestId("migrate-problem-review")).ToHaveTextAsync(MigrationRejectionCodes.UnmappedActivity);
        await dialog.GetByTestId("migrate-map-review").SelectOptionAsync("approve");
        await Expect(dialog.GetByTestId("migrate-problem-review")).ToHaveCountAsync(0);
        await dialog.GetByTestId("migrate-dry-run").ClickAsync();
        await Expect(dialog.GetByTestId("migrate-outcome-Migratable")).ToBeVisibleAsync();
        await dialog.GetByTestId("migrate-confirm").ClickAsync();
        await Expect(dialog.GetByTestId("migrate-outcome-Migrated")).ToBeVisibleAsync();
        await dialog.GetByTestId("migrate-cancel").ClickAsync();

        // Assert: the reloaded page shows v2; the instance waits in 'approve'.
        await Expect(dialog).ToHaveCountAsync(0);
        var state = await WaitForActive(id, "approve");
        StringAssert.StartsWith(state.ProcessDefinitionId, $"{key}:2:");
        await Expect(instancePage.DiagramElement("approve")).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    [TestMethod]
    public async Task Ui_WorkflowsPage_MigrateDialog_CountsAndMigratesTheVersion()
    {
        // Arrange
        var key = Key();
        await ApiClient.DeployAsync(Load("migration-order-v1.bpmn", key));
        var id = (await ApiClient.StartAsync(key)).WorkflowInstanceId;
        await WaitForActive(id, "review");
        await ApiClient.DeployAsync(Load("migration-order-v2.bpmn", key));
        await WaitForRunningCount(key, 1);
        var workflows = new WorkflowsPage(Page);
        await workflows.OpenAsync();
        await workflows.SearchAsync(key);
        await workflows.ToggleVersionsAsync(key);

        // Act
        await workflows.VersionRow(key, 1).GetByTestId("workflows-migrate").ClickAsync();
        var dialog = Page.GetByTestId("migrate-dialog");
        await Expect(dialog.GetByTestId("migrate-running-count")).ToHaveTextAsync("1 running instance(s) on v1.");
        await dialog.GetByTestId("migrate-map-review").SelectOptionAsync("approve");
        await dialog.GetByTestId("migrate-confirm").ClickAsync();

        // Assert
        await Expect(dialog.GetByTestId("migrate-outcome-Migrated")).ToBeVisibleAsync();
        await WaitForActive(id, "approve");
    }

    /// <summary>The preview's count reads the read model, which runs behind the instances.</summary>
    private async Task<MigrationPreviewResponse> WaitForRunningCount(string key, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var preview = await ApiClient.GetMigrationPreviewAsync(key, 1, 2);
            if (preview.RunningInstanceCount == expected)
                return preview;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"{key} v1 shows {preview.RunningInstanceCount} running instances, expected {expected}");
            await Task.Delay(200);
        }
    }

    private static string Key() => $"migration-order-{Guid.NewGuid():N}"[..24];

    private static string Load(string fileName, string key)
        => BpmnFixtureLoader.Load(PlanFolder, fileName).Replace("migration-order", key, StringComparison.Ordinal);

    private Task<InstanceStateSnapshot> WaitForActive(Guid instanceId, string activityId) =>
        ApiClient.WaitForStateAsync(instanceId, s => s.ActiveActivityIds.Contains(activityId));

    private async Task CompleteUserTask(Guid instanceId, string activityId)
    {
        var state = await WaitForActive(instanceId, activityId);
        var taskId = state.ActiveActivities.First(x => x.ActivityId == activityId).ActivityInstanceId;
        using (var claim = await ApiClient.ClaimUserTaskAsync(taskId, TaskUser))
            Assert.IsTrue(claim.IsSuccessStatusCode, $"claim failed: {claim.StatusCode}");
        using (var complete = await ApiClient.CompleteUserTaskAsync(taskId, TaskUser, new Dictionary<string, object?>()))
            Assert.IsTrue(complete.IsSuccessStatusCode, $"complete failed: {complete.StatusCode}");
    }
}
