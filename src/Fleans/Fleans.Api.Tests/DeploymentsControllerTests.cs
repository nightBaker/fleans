using Fleans.Api.Authorization;
using Fleans.Api.Controllers;
using Fleans.Application;
using Fleans.Application.Bpmn;
using Fleans.Application.Deployments;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.States;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Fleans.Api.Tests;

[TestClass]
public class DeploymentsControllerTests
{
    private static readonly Guid RequestId = Guid.NewGuid();

    private IWorkflowCommandService _commands = null!;
    private IWorkflowQueryService _queries = null!;

    [TestInitialize]
    public void Setup()
    {
        _commands = Substitute.For<IWorkflowCommandService>();
        _queries = Substitute.For<IWorkflowQueryService>();
        _commands.SubmitDeployment(default!, default!, default, default)
            .ReturnsForAnyArgs(ci => Request(requestedBy: ci.ArgAt<string>(1)));
        _commands.ApproveDeployment(default, default!, default)
            .ReturnsForAnyArgs(_ => Request());
    }

    [TestMethod]
    public async Task Submit_Jwt_NoBodyRequester_SubmitsAsTokenUser()
    {
        var controller = JwtController(tokenUser: "alice");

        var result = await controller.Submit(new SubmitDeploymentRequest("<bpmn/>", "adds a timer"));

        var created = Assert.IsInstanceOfType<CreatedAtRouteResult>(result);
        var dto = Assert.IsInstanceOfType<DeploymentRequestDto>(created.Value);
        Assert.AreEqual("alice", dto.RequestedBy);
        Assert.AreEqual("Pending", dto.Status);
        await _commands.Received(1).SubmitDeployment("<bpmn/>", "alice", "adds a timer", false);
    }

    [TestMethod]
    public async Task Submit_Jwt_BodyRequesterOfAnotherUser_Returns403_AndSubmitsNothing()
    {
        var controller = JwtController(tokenUser: "bob");

        var result = await controller.Submit(new SubmitDeploymentRequest("<bpmn/>", RequestedBy: "alice"));

        var forbidden = Assert.IsInstanceOfType<ObjectResult>(result);
        Assert.AreEqual(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        await _commands.DidNotReceiveWithAnyArgs().SubmitDeployment(default!, default!, default, default);
    }

    [TestMethod]
    public async Task Submit_NoAuth_WithoutRequester_Returns400()
    {
        var controller = BodyController();

        var result = await controller.Submit(new SubmitDeploymentRequest("<bpmn/>"));

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        await _commands.DidNotReceiveWithAnyArgs().SubmitDeployment(default!, default!, default, default);
    }

    [TestMethod]
    public async Task Approve_Jwt_ApprovesAsTokenUser()
    {
        var controller = JwtController(tokenUser: "bob");

        var result = await controller.Approve(RequestId, new ApproveDeploymentRequest(Comment: "ok"));

        Assert.IsInstanceOfType<OkObjectResult>(result);
        await _commands.Received(1).ApproveDeployment(RequestId, "bob", "ok");
    }

    [TestMethod]
    public async Task Reject_WithoutReason_Returns400()
    {
        var controller = BodyController();

        var result = await controller.Reject(RequestId, new RejectDeploymentRequest(" ", UserId: "bob"));

        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        await _commands.DidNotReceiveWithAnyArgs().RejectDeployment(default, default!, default!);
    }

    [TestMethod]
    public async Task Get_UnknownRequest_Returns404()
    {
        var controller = BodyController();

        var result = await controller.Get(RequestId);

        Assert.IsInstanceOfType<NotFoundObjectResult>(result);
    }

    [TestMethod]
    public async Task Deploy_WhenApprovalsAreRequired_Returns409()
    {
        // The direct deploy endpoint must not bypass the review (#900).
        var converter = Substitute.For<IBpmnConverter>();
        converter.ConvertFromXmlAsync(Arg.Any<Stream>())
            .Returns(new WorkflowDefinition { WorkflowId = "p", Activities = [], SequenceFlows = [] });
        _commands.DeployWorkflow(default!, default!).ThrowsAsyncForAnyArgs(new DeploymentApprovalRequiredException(2));
        var controller = new DefinitionsController(_commands, _queries, converter);

        var result = await controller.DeployWorkflow(new DeployBpmnRequest("<bpmn/>"));

        var conflict = Assert.IsInstanceOfType<ConflictObjectResult>(result);
        var error = Assert.IsInstanceOfType<ErrorResponse>(conflict.Value);
        StringAssert.Contains(error.Error, "/Deployments/requests");
    }

    private static DeploymentRequestState Request(string requestedBy = "alice")
    {
        var request = new DeploymentRequestState();
        request.Submit(RequestId, "order-process", "<bpmn/>", requestedBy, null, 2, false, DateTimeOffset.UtcNow);
        return request;
    }

    private DeploymentsController JwtController(string tokenUser) =>
        Controller(UserIdResolverTests.Jwt(), UserIdResolverTests.Authenticated(("preferred_username", tokenUser)));

    private DeploymentsController BodyController() =>
        Controller(new BodyUserIdResolver(), new DefaultHttpContext());

    private DeploymentsController Controller(IUserIdResolver userIdResolver, HttpContext httpContext) =>
        new(NullLogger<DeploymentsController>.Instance, _commands, _queries, userIdResolver)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
}
