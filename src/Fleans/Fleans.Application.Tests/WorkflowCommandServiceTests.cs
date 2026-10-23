using Fleans.Application;
using Fleans.Application.Deployments;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Orleans;
using System.Dynamic;

namespace Fleans.Application.Tests
{
    [TestClass]
    public class WorkflowCommandServiceTests
    {
        private WorkflowCommandService _commandService = null!;
        private IGrainFactory _grainFactory = null!;
        private IProcessDefinitionGrain _processGrain = null!;

        [TestInitialize]
        public void Setup()
        {
            _grainFactory = Substitute.For<IGrainFactory>();
            _processGrain = Substitute.For<IProcessDefinitionGrain>();
            _commandService = new WorkflowCommandService(_grainFactory, NullLogger<WorkflowCommandService>.Instance,
                Options.Create(new DeploymentApprovalOptions()));
        }

        [TestMethod]
        public async Task StartWorkflow_ShouldStartTheNewInstance_InOneGrainCall()
        {
            // Arrange
            var workflowId = "test-workflow-1";
            var definition = Substitute.For<IWorkflowDefinition>();
            var variables = new ExpandoObject();
            var workflowInstance = Substitute.For<IWorkflowInstanceGrain>();
            Guid? instanceKey = null;

            _grainFactory.GetGrain<IProcessDefinitionGrain>(workflowId)
                .Returns(_processGrain);
            _processGrain.GetStartableDefinition()
                .Returns(Task.FromResult<IWorkflowDefinition?>(definition));
            _grainFactory.GetGrain<IWorkflowInstanceGrain>(Arg.Do<Guid>(key => instanceKey = key), Arg.Any<string?>())
                .Returns(workflowInstance);

            // Act
            var result = await _commandService.StartWorkflow(workflowId, variables, businessId: "ORDER-1");

            // Assert — the id returned is the grain started; one call carries everything (#863)
            Assert.AreEqual(instanceKey, result);
            await workflowInstance.Received(1).StartWorkflow(definition, null, "ORDER-1", variables, null);
            Assert.HasCount(1, workflowInstance.ReceivedCalls());
        }

        [TestMethod]
        public async Task StartWorkflow_WhenTheProcessIsDisabled_ThrowsAndStartsNothing()
        {
            // Arrange
            _grainFactory.GetGrain<IProcessDefinitionGrain>("disabled-process")
                .Returns(_processGrain);
            _processGrain.GetStartableDefinition()
                .Returns(Task.FromResult<IWorkflowDefinition?>(null));

            // Act & Assert — an InvalidOperationException: the API answers 409
            await Assert.ThrowsExactlyAsync<ProcessDisabledException>(
                () => _commandService.StartWorkflow("disabled-process"));
            _grainFactory.DidNotReceive().GetGrain<IWorkflowInstanceGrain>(Arg.Any<Guid>(), Arg.Any<string?>());
        }

        [TestMethod]
        public async Task StartWorkflowByProcessDefinitionId_ShouldStartThatVersion_UnlessTheProcessIsDisabled()
        {
            // Arrange
            const string processDefinitionId = "versioned-process:2:20260101T000000.0000000Z";
            var definition = Substitute.For<IWorkflowDefinition>();
            var workflowInstance = Substitute.For<IWorkflowInstanceGrain>();
            _grainFactory.GetGrain<IProcessDefinitionGrain>("versioned-process")
                .Returns(_processGrain);
            _processGrain.GetStartableDefinition(processDefinitionId)
                .Returns(Task.FromResult<IWorkflowDefinition?>(definition), Task.FromResult<IWorkflowDefinition?>(null));
            _grainFactory.GetGrain<IWorkflowInstanceGrain>(Arg.Any<Guid>(), Arg.Any<string?>())
                .Returns(workflowInstance);

            // Act
            await _commandService.StartWorkflowByProcessDefinitionId(processDefinitionId);

            // Assert
            await workflowInstance.Received(1).StartWorkflow(definition, null, null, null, null);
            await Assert.ThrowsExactlyAsync<ProcessDisabledException>(
                () => _commandService.StartWorkflowByProcessDefinitionId(processDefinitionId));
        }

        [TestMethod]
        public async Task CompleteActivity_ShouldCallWorkflowInstance_WithCorrectParameters()
        {
            // Arrange
            var workflowInstanceId = Guid.NewGuid();
            var activityId = "task-1";
            var variables = new ExpandoObject();
            ((IDictionary<string, object>)variables)["key"] = "value";

            var workflowInstance = Substitute.For<IWorkflowInstanceGrain>();
            _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId)
                .Returns(workflowInstance);

            workflowInstance.CompleteActivity(activityId, variables)
                .Returns(Task.CompletedTask);

            // Act
            await _commandService.CompleteActivity(workflowInstanceId, activityId, variables);

            // Assert
            await workflowInstance.Received(1).CompleteActivity(activityId, variables);
        }

        [TestMethod]
        public async Task CompleteActivity_ShouldGetCorrectGrain_ByInstanceId()
        {
            // Arrange
            var workflowInstanceId = Guid.NewGuid();
            var activityId = "task-1";
            var variables = new ExpandoObject();

            var workflowInstance = Substitute.For<IWorkflowInstanceGrain>();
            _grainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId)
                .Returns(workflowInstance);

            // Act
            await _commandService.CompleteActivity(workflowInstanceId, activityId, variables);

            // Assert
            _grainFactory.Received(1).GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
        }

        [TestMethod]
        public async Task DeployWorkflow_WhenApprovalsAreRequired_ThrowsAndDeploysNothing()
        {
            // Arrange
            var service = new WorkflowCommandService(_grainFactory, NullLogger<WorkflowCommandService>.Instance,
                Options.Create(new DeploymentApprovalOptions { RequiredApprovals = 2 }));
            var workflow = new WorkflowDefinition { WorkflowId = "guarded", Activities = [], SequenceFlows = [] };

            // Act & Assert — an InvalidOperationException: the API answers 409
            await Assert.ThrowsExactlyAsync<DeploymentApprovalRequiredException>(
                () => service.DeployWorkflow(workflow, "<bpmn/>"));
            _grainFactory.DidNotReceive().GetGrain<IProcessDefinitionGrain>(Arg.Any<string>(), Arg.Any<string?>());
        }

        [TestMethod]
        public async Task SubmitDeployment_PassesThisClustersRequiredApprovals()
        {
            // Arrange
            var service = new WorkflowCommandService(_grainFactory, NullLogger<WorkflowCommandService>.Instance,
                Options.Create(new DeploymentApprovalOptions { RequiredApprovals = 2 }));
            var requestGrain = Substitute.For<IDeploymentRequestGrain>();
            _grainFactory.GetGrain<IDeploymentRequestGrain>(Arg.Any<Guid>(), Arg.Any<string?>()).Returns(requestGrain);

            // Act
            await service.SubmitDeployment("<bpmn/>", "alice", "note", deployDisabled: true);

            // Assert
            await requestGrain.Received(1).Submit("<bpmn/>", "alice", "note", 2, true);
        }
    }
}
