using System.Dynamic;
using Fleans.Api.Authorization;
using Fleans.Api.Controllers;
using Fleans.Application;
using Fleans.Application.DTOs;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Fleans.Api.Tests;

[TestClass]
public class UserTasksControllerTests
{
    private static readonly Guid WorkflowInstanceId = Guid.NewGuid();
    private static readonly Guid TaskId = Guid.NewGuid();

    private IWorkflowCommandService _commands = null!;
    private IWorkflowQueryService _queries = null!;

    [TestInitialize]
    public void Setup()
    {
        _commands = Substitute.For<IWorkflowCommandService>();
        _queries = Substitute.For<IWorkflowQueryService>();
        _queries.GetUserTask(TaskId).Returns(new UserTaskResponse(
            WorkflowInstanceId, TaskId, "ApproveTask", "alice", [], [], null, "Created",
            DateTimeOffset.UtcNow, null));
    }

    [TestMethod]
    public async Task Claim_Jwt_BodyUserIdOfAnotherUser_Returns403_AndDoesNotClaim()
    {
        var controller = JwtController(tokenUser: "bob");

        var result = await controller.ClaimTask(TaskId, new ClaimTaskRequest("alice"));

        AssertForbidden(result);
        await _commands.DidNotReceiveWithAnyArgs().ClaimUserTask(default, default, default!, default!);
    }

    [TestMethod]
    public async Task Claim_Jwt_NoBodyUserId_ClaimsAsTokenUser()
    {
        var controller = JwtController(tokenUser: "alice");

        var result = await controller.ClaimTask(TaskId, new ClaimTaskRequest(null));

        Assert.IsInstanceOfType<OkResult>(result);
        await _commands.Received(1).ClaimUserTask(
            WorkflowInstanceId, TaskId, "alice", Arg.Any<IReadOnlyList<string>>());
    }

    [TestMethod]
    public async Task Claim_Jwt_MatchingBodyUserId_ClaimsAsTokenUser()
    {
        var controller = JwtController(tokenUser: "alice");

        var result = await controller.ClaimTask(TaskId, new ClaimTaskRequest("alice"));

        Assert.IsInstanceOfType<OkResult>(result);
        await _commands.Received(1).ClaimUserTask(
            WorkflowInstanceId, TaskId, "alice", Arg.Any<IReadOnlyList<string>>());
    }

    [TestMethod]
    public async Task Complete_Jwt_BodyUserIdOfAnotherUser_Returns403_AndDoesNotComplete()
    {
        var controller = JwtController(tokenUser: "bob");

        var result = await controller.CompleteTask(TaskId, new CompleteTaskRequest("alice", null));

        AssertForbidden(result);
        await _commands.DidNotReceiveWithAnyArgs().CompleteUserTask(default, default, default!, default!);
    }

    [TestMethod]
    public async Task Complete_Jwt_NoBody_CompletesAsTokenUser()
    {
        var controller = JwtController(tokenUser: "alice");

        var result = await controller.CompleteTask(TaskId, null);

        Assert.IsInstanceOfType<OkResult>(result);
        await _commands.Received(1).CompleteUserTask(
            WorkflowInstanceId, TaskId, "alice", Arg.Any<ExpandoObject>());
    }

    [TestMethod]
    public async Task Claim_NoAuth_UsesBodyUserId()
    {
        var controller = Controller(new BodyUserIdResolver(), new DefaultHttpContext());

        var result = await controller.ClaimTask(TaskId, new ClaimTaskRequest("alice"));

        Assert.IsInstanceOfType<OkResult>(result);
        await _commands.Received(1).ClaimUserTask(
            WorkflowInstanceId, TaskId, "alice", Arg.Any<IReadOnlyList<string>>());
    }

    [TestMethod]
    public async Task Claim_NoAuth_MissingUserId_Returns400()
    {
        var controller = Controller(new BodyUserIdResolver(), new DefaultHttpContext());

        var result = await controller.ClaimTask(TaskId, new ClaimTaskRequest(null));

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        await _commands.DidNotReceiveWithAnyArgs().ClaimUserTask(default, default, default!, default!);
    }

    [TestMethod]
    public async Task Complete_NoAuth_MissingUserId_Returns400()
    {
        var controller = Controller(new BodyUserIdResolver(), new DefaultHttpContext());

        var result = await controller.CompleteTask(TaskId, new CompleteTaskRequest(" ", null));

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        await _commands.DidNotReceiveWithAnyArgs().CompleteUserTask(default, default, default!, default!);
    }

    private static void AssertForbidden(IActionResult result)
    {
        var objectResult = Assert.IsInstanceOfType<ObjectResult>(result);
        Assert.AreEqual(StatusCodes.Status403Forbidden, objectResult.StatusCode);
        var error = Assert.IsInstanceOfType<ErrorResponse>(objectResult.Value);
        Assert.DoesNotContain("alice", error.Error);
        Assert.DoesNotContain("bob", error.Error);
    }

    private UserTasksController JwtController(string tokenUser) =>
        Controller(
            UserIdResolverTests.Jwt(),
            UserIdResolverTests.Authenticated(("preferred_username", tokenUser)),
            new JwtUserGroupResolver(new ConfigurationBuilder().Build()));

    private UserTasksController Controller(
        IUserIdResolver userIdResolver, HttpContext httpContext, IUserGroupResolver? groupResolver = null) =>
        new(NullLogger<UserTasksController>.Instance, _commands, _queries,
            groupResolver ?? new BodyUserGroupResolver(), userIdResolver)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
}
