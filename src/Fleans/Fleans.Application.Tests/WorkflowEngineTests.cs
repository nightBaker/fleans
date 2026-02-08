using Fleans.Application;
using Fleans.Application.WorkflowFactory;
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
    public class WorkflowEngineTests
    {
        private WorkflowEngine _workflowEngine = null!;
        private IGrainFactory _grainFactory = null!;
        private IWorkflowInstanceFactoryGrain _factoryGrain = null!;

        [TestInitialize]
        public void Setup()
        {
            _grainFactory = Substitute.For<IGrainFactory>();
            _factoryGrain = Substitute.For<IWorkflowInstanceFactoryGrain>();
            _workflowEngine = new WorkflowEngine(_grainFactory, NullLogger<WorkflowEngine>.Instance);
        }

        [TestMethod]
        public async Task StartWorkflow_ShouldCreateWorkflowInstance_AndStartIt()
        {
            // Arrange
            var workflowId = "test-workflow-1";
            var workflowInstanceId = Guid.NewGuid();
            var workflowInstance = Substitute.For<IWorkflowInstance>();

            _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0)
                .Returns(_factoryGrain);

            _factoryGrain.CreateWorkflowInstanceGrain(workflowId)
                .Returns(workflowInstance);

            workflowInstance.GetWorkflowInstanceId()
                .Returns(ValueTask.FromResult(workflowInstanceId));
            workflowInstance.StartWorkflow().Returns(Task.CompletedTask);

            // Act
            var result = await _workflowEngine.StartWorkflow(workflowId);

            // Assert
            Assert.AreEqual(workflowInstanceId, result);
            await _factoryGrain.Received(1).CreateWorkflowInstanceGrain(workflowId);
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

            var workflowInstance = Substitute.For<IWorkflowInstance>();
            _grainFactory.GetGrain<IWorkflowInstance>(workflowInstanceId)
                .Returns(workflowInstance);

            workflowInstance.CompleteActivity(activityId, variables)
                .Returns(Task.CompletedTask);

            // Act
            _workflowEngine.CompleteActivity(workflowInstanceId, activityId, variables);

            // Assert
            await workflowInstance.Received(1).CompleteActivity(activityId, variables);
        }

        [TestMethod]
        public void CompleteActivity_ShouldGetCorrectGrain_ByInstanceId()
        {
            // Arrange
            var workflowInstanceId = Guid.NewGuid();
            var activityId = "task-1";
            var variables = new ExpandoObject();

            var workflowInstance = Substitute.For<IWorkflowInstance>();
            _grainFactory.GetGrain<IWorkflowInstance>(workflowInstanceId)
                .Returns(workflowInstance);

            // Act
            _workflowEngine.CompleteActivity(workflowInstanceId, activityId, variables);

            // Assert
            _grainFactory.Received(1).GetGrain<IWorkflowInstance>(workflowInstanceId);
        }
    }
}

