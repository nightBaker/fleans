using System.Dynamic;
using Fleans.Api.Controllers;
using Fleans.Application;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Fleans.Api.Tests;

/// <summary>Completion and failure of external tasks by activity instance id (#979).</summary>
[TestClass]
public class ExecutionControllerTests
{
    private static readonly Guid InstanceId = Guid.NewGuid();
    private static readonly Guid ActivityInstanceId = Guid.NewGuid();

    private IWorkflowCommandService _commands = null!;
    private ExecutionController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _commands = Substitute.For<IWorkflowCommandService>();
        _controller = new ExecutionController(_commands);
    }

    [TestMethod]
    public async Task CompleteActivity_WithActivityInstanceId_CompletesThatInstance()
    {
        // Act
        var result = await _controller.CompleteActivity(
            new CompleteActivityRequest(InstanceId, "charge", new() { ["paymentRef"] = "ch_1" }, ActivityInstanceId));

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        await _commands.Received(1).CompleteActivity(InstanceId, "charge", ActivityInstanceId,
            Arg.Is<ExpandoObject>(v => Equals(((IDictionary<string, object?>)v)["paymentRef"], "ch_1")));
        await _commands.DidNotReceiveWithAnyArgs().CompleteActivity(default, default!, default(ExpandoObject)!);
    }

    [TestMethod]
    public async Task CompleteActivity_WithoutActivityInstanceId_CompletesByActivityId()
    {
        // Act
        await _controller.CompleteActivity(new CompleteActivityRequest(InstanceId, "charge"));

        // Assert
        await _commands.Received(1).CompleteActivity(InstanceId, "charge", Arg.Any<ExpandoObject>());
    }

    [TestMethod]
    public async Task FailActivity_PassesTheErrorCodeAndVariables()
    {
        // Act
        var result = await _controller.FailActivity(new FailActivityRequest(
            InstanceId, "charge", ActivityInstanceId, "PAYMENT_DECLINED", "card declined",
            new() { ["reason"] = "insufficient-funds" }));

        // Assert
        Assert.IsInstanceOfType<OkResult>(result);
        await _commands.Received(1).FailActivity(InstanceId, "charge", ActivityInstanceId,
            "PAYMENT_DECLINED", "card declined",
            Arg.Is<ExpandoObject?>(v => Equals(((IDictionary<string, object?>)v!)["reason"], "insufficient-funds")));
    }

    [TestMethod]
    public async Task FailActivity_WithoutErrorCode_Returns400()
    {
        // Act
        var result = await _controller.FailActivity(new FailActivityRequest(
            InstanceId, "charge", ActivityInstanceId, ""));

        // Assert
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        await _commands.DidNotReceiveWithAnyArgs().FailActivity(default, default!, default, default!, default!, default);
    }

    [TestMethod]
    public async Task FailActivity_WithoutActivityInstanceId_Returns400()
    {
        // Act
        var result = await _controller.FailActivity(new FailActivityRequest(
            InstanceId, "charge", Guid.Empty, "PAYMENT_DECLINED"));

        // Assert
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
    }
}
