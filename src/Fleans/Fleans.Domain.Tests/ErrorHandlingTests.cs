using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Diagnostics;
using Fleans.Domain.Activities;
using Fleans.Domain.Connections;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ErrorHandlingTests
    {
        [TestMethod]
        public async Task ActivityShouldFail_OnException()
        {
            var activity = Substitute.For<IExecutableActivity>();
            activity.Id.Returns(Guid.NewGuid());
            activity.ExecuteAsync(Arg.Any<IContext>()).Throws(new Exception());
            activity.Status.Returns(ActivityStatus.Failed);

            var startProcessId = Guid.NewGuid();
            var startProcessEvent = Substitute.For<IStartProcessEventActivity>();
            startProcessEvent.IsDefault.Returns(true);
            startProcessEvent.Id.Returns(startProcessId);

            var connection = Substitute.For<IWorkflowConnection<IActivity, IActivity>>();
            connection.From.Returns( startProcessEvent);
            connection.To.Returns(activity);
            
            var conditionExpression = Substitute.For<IConditionExpressionRunner>();
            conditionExpression.Evaluate(Arg.Any<IContext>(), Arg.Any<Exception>()).Returns(true);

            var workflowDefinition = new WorkflowDefinition(Guid.NewGuid(), 1,
                new[]{ startProcessEvent },
                new[] { activity },
                new Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]>
                {
                    [startProcessId] = new []{ connection  }
                });
            
            var workflow = new Workflow(Guid.NewGuid(), new(), workflowDefinition);
            
            // Act
            await workflow.Start();

            // Assert
            _ = activity.Received(1).ExecuteAsync(Arg.Any<IContext>());
            activity.Received(1).Fail(Arg.Any<Exception>());
            Assert.AreEqual(Workflow.WorkflowStatus.Failed, workflow.Status);
        }

        [TestMethod]
        public async Task ActivityShouldGoToErrorActivity_OnException()
        {
            var firstActivity = Substitute.For<IExecutableActivity>();
            firstActivity.Id.Returns(Guid.NewGuid());
            firstActivity.ExecuteAsync(Arg.Any<IContext>()).Throws(new Exception());
            
            var targetActivity = Substitute.For<IExecutableActivity>();
            targetActivity.Id.Returns(Guid.NewGuid());
            targetActivity.Status.Returns(ActivityStatus.Completed);
            
            var startProcessId = Guid.NewGuid();
            var startProcessEvent = Substitute.For<IStartProcessEventActivity>();
            startProcessEvent.IsDefault.Returns(true);
            startProcessEvent.Id.Returns(startProcessId);

            var connection = Substitute.For<IWorkflowConnection<IActivity, IActivity>>();
            connection.From.Returns( startProcessEvent);
            connection.To.Returns(firstActivity);

            var conditionExpression = Substitute.For<IConditionExpressionRunner>();
            conditionExpression.Evaluate(Arg.Any<IContext>(), Arg.Any<Exception>()).Returns(true);

            var workflowDefinition = new WorkflowDefinition(Guid.NewGuid(), 1,
                new[] { startProcessEvent },
                new[] { firstActivity, targetActivity },
                new Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]>
                {
                    [startProcessId] = new []{ connection  },
                    [firstActivity.Id] = new []
                        { new OnErrorConnection(firstActivity, targetActivity, conditionExpression) },
                });

            var workflow = new Workflow(Guid.NewGuid(), new(), workflowDefinition);

            await workflow.Start();
            
            _ = targetActivity.Received(1).ExecuteAsync(Arg.Any<IContext>());
            Assert.AreEqual(Workflow.WorkflowStatus.Completed, workflow.Status);
        }
    }
}