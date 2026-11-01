using Fleans.Api.Controllers;
using Fleans.Application;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Fleans.Api.Tests;

[TestClass]
public class InstancesControllerTests
{
    private static readonly DateTimeOffset CompletedBefore = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private IWorkflowInstancePurgeService _purge = null!;
    private InstancesController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _purge = Substitute.For<IWorkflowInstancePurgeService>();
        _controller = new InstancesController(Substitute.For<IWorkflowQueryService>(), _purge);
    }

    [TestMethod]
    public async Task Purge_OnlyCutoff_UsesDefaultSizes_ReturnsTheResult()
    {
        // Arrange
        _purge.PurgeCompletedAsync(CompletedBefore, IWorkflowInstancePurgeService.DefaultBatchSize,
                IWorkflowInstancePurgeService.DefaultMaxInstances, Arg.Any<CancellationToken>())
            .Returns(new WorkflowInstancePurgeResult(42, HasMore: true, PurgedDeduplicationCount: 7));

        // Act
        var result = await _controller.PurgeInstances(new PurgeInstancesRequest(CompletedBefore), CancellationToken.None);

        // Assert
        var ok = result as OkObjectResult;
        Assert.IsNotNull(ok);
        Assert.AreEqual(new PurgeInstancesResponse(42, HasMore: true, PurgedDeduplicationCount: 7), ok.Value);
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
}
