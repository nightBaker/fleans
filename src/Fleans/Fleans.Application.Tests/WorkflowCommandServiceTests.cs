using Fleans.Application;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.Logging.Abstractions;
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
            _commandService = new WorkflowCommandService(_grainFactory, NullLogger<WorkflowCommandService>.Instance);
        }

        [TestMethod]
        public async Task StartWorkflow_ShouldCreateWorkflowInstance_AndStartIt()
        {
            // Arrange
            var workflowId = "test-workflow-1";
            var workflowInstanceId = Guid.NewGuid();
            var workflowInstance = Substitute.For<IWorkflowInstanceGrain>();

            _grainFactory.GetGrain<IProcessDefinitionGrain>(Arg.Any<string>())
                .Returns(_processGrain);

            _processGrain.CreateInstance()
                .Returns(workflowInstance);

            workflowInstance.GetWorkflowInstanceId()
                .Returns(ValueTask.FromResult(workflowInstanceId));
            workflowInstance.StartWorkflow().Returns(Task.CompletedTask);

            // Act
            var result = await _commandService.StartWorkflow(workflowId);

            // Assert
            Assert.AreEqual(workflowInstanceId, result);
            await _processGrain.Received(1).CreateInstance();
            await workflowInstance.Received(1).StartWorkflow();
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
    }
}
