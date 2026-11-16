using Fleans.Api.Controllers;
using Fleans.Application;
using Fleans.Application.InstanceMigration;
using Fleans.Application.QueryModels;
using Fleans.Domain.InstanceMigration;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Fleans.Api.Tests;

[TestClass]
public class InstancesControllerTests
{
    private static readonly DateTimeOffset CompletedBefore = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private IWorkflowInstancePurgeService _purge = null!;
    private IInstanceMigrationService _migration = null!;
    private InstancesController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _purge = Substitute.For<IWorkflowInstancePurgeService>();
        _migration = Substitute.For<IInstanceMigrationService>();
        _controller = new InstancesController(Substitute.For<IWorkflowQueryService>(), _purge, _migration);
    }

    [TestMethod]
    public async Task Purge_OnlyCutoff_UsesDefaultSizes_ReturnsTheResult()
    {
        // Arrange
        _purge.PurgeCompletedAsync(CompletedBefore, IWorkflowInstancePurgeService.DefaultBatchSize,
                IWorkflowInstancePurgeService.DefaultMaxInstances, Arg.Any<CancellationToken>())
            .Returns(new WorkflowInstancePurgeResult(42, HasMore: true, PurgedMessageDeduplicationsCount: 7));

        // Act
        var result = await _controller.PurgeInstances(new PurgeInstancesRequest(CompletedBefore), CancellationToken.None);

        // Assert
        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreEqual(new PurgeInstancesResponse(42, HasMore: true, PurgedMessageDeduplicationsCount: 7), ok.Value);
    }

    [TestMethod]
    public async Task Purge_ExplicitSizes_ArePassedThrough()
    {
        // Arrange
        _purge.PurgeCompletedAsync(default, default, default, default)
            .ReturnsForAnyArgs(new WorkflowInstancePurgeResult(0, HasMore: false));

        // Act
        await _controller.PurgeInstances(new PurgeInstancesRequest(CompletedBefore, BatchSize: 100, MaxInstances: 250), CancellationToken.None);

        // Assert
        await _purge.Received(1).PurgeCompletedAsync(CompletedBefore, 100, 250, Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task Purge_NoCutoff_Returns400_AndPurgesNothing()
    {
        // Act
        var result = await _controller.PurgeInstances(new PurgeInstancesRequest(null), CancellationToken.None);

        // Assert
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        await _purge.DidNotReceiveWithAnyArgs().PurgeCompletedAsync(default, default, default, default);
    }

    // --- Instance migration (#1069) ---

    private static readonly Guid InstanceId = Guid.NewGuid();

    private static InstanceMigrationResult Result(InstanceMigrationOutcome outcome, params MigrationRejection[] rejections)
        => new(InstanceId, outcome, "p:1:20260101T000000.0000000Z", "p:2:20260102T000000.0000000Z", rejections.ToList());

    [TestMethod]
    public async Task MigrateInstance_PassesThePlan_ReturnsTheResult()
    {
        // Arrange
        _migration.MigrateInstanceAsync(InstanceId, Arg.Any<InstanceMigrationRequest>())
            .Returns(Result(InstanceMigrationOutcome.Migrated));

        // Act
        var result = await _controller.MigrateInstance(InstanceId, new MigrateInstanceRequest(
            "p:2:20260102T000000.0000000Z", [new MigrationMappingDto("review", "approve")], UpdateEventTriggers: true, RequestId: "r1"));

        // Assert
        var ok = Assert.IsInstanceOfType<OkObjectResult>(result);
        Assert.AreEqual("Migrated", ((InstanceMigrationResultDto)ok.Value!).Outcome);
        await _migration.Received(1).MigrateInstanceAsync(InstanceId, Arg.Is<InstanceMigrationRequest>(r =>
            r.TargetProcessDefinitionId == "p:2:20260102T000000.0000000Z"
            && r.Mappings!.Single() == new ActivityMapping("review", "approve")
            && r.UpdateEventTriggers && !r.DryRun && r.RequestId == "r1"));
    }

    [TestMethod]
    public async Task MigrateInstance_Rejected_Returns409WithTheCodes()
    {
        _migration.MigrateInstanceAsync(InstanceId, Arg.Any<InstanceMigrationRequest>())
            .Returns(Result(InstanceMigrationOutcome.Rejected, new MigrationRejection("unmapped-activity", "review", "no element")));

        var result = await _controller.MigrateInstance(InstanceId, new MigrateInstanceRequest("p:2:20260102T000000.0000000Z"));

        var conflict = Assert.IsInstanceOfType<ConflictObjectResult>(result);
        Assert.AreEqual("unmapped-activity", ((InstanceMigrationResultDto)conflict.Value!).Rejections.Single().Code);
    }

    [TestMethod]
    public async Task MigrateInstance_RejectedDryRun_Returns200WithTheReport()
    {
        _migration.MigrateInstanceAsync(InstanceId, Arg.Any<InstanceMigrationRequest>())
            .Returns(Result(InstanceMigrationOutcome.Rejected, new MigrationRejection("type-changed", "review", "type")));

        var result = await _controller.MigrateInstance(InstanceId, new MigrateInstanceRequest("p:2:20260102T000000.0000000Z", DryRun: true));

        Assert.IsInstanceOfType<OkObjectResult>(result);
    }

    [TestMethod]
    public async Task MigrateInstance_NoTarget_Returns400()
    {
        var result = await _controller.MigrateInstance(InstanceId, new MigrateInstanceRequest(null));

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        await _migration.DidNotReceiveWithAnyArgs().MigrateInstanceAsync(default, default!);
    }

    [TestMethod]
    public async Task MigrateInstances_CountsOutcomesAndRejectionCodes()
    {
        // Arrange
        var rejected = new MigrationRejection("in-flight-work", "script", "running");
        _migration.MigrateAsync(Arg.Any<InstanceMigrationRequest>(), Arg.Any<CancellationToken>()).Returns(
            new InstanceMigrationBatchResult("r1", DryRun: true,
                [Result(InstanceMigrationOutcome.Migratable), Result(InstanceMigrationOutcome.Rejected, rejected, rejected)],
                ContinuationToken: "next"));

        // Act
        var result = await _controller.MigrateInstances(new MigrateInstancesRequest(
            null, "p:1:20260101T000000.0000000Z", "p:2:20260102T000000.0000000Z", DryRun: true), CancellationToken.None);

        // Assert
        var response = (MigrateInstancesResponse)Assert.IsInstanceOfType<OkObjectResult>(result).Value!;
        Assert.AreEqual(1, response.Outcomes["Migratable"]);
        Assert.AreEqual(1, response.Outcomes["Rejected"]);
        Assert.AreEqual(1, response.RejectionCodes["in-flight-work"], "an instance counts once per code");
        Assert.AreEqual("next", response.ContinuationToken);
        await _migration.Received(1).MigrateAsync(Arg.Is<InstanceMigrationRequest>(r =>
            r.SourceProcessDefinitionId == "p:1:20260101T000000.0000000Z" && r.DryRun), Arg.Any<CancellationToken>());
    }
}
