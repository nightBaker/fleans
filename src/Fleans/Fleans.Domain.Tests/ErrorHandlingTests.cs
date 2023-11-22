using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Diagnostics;
using Fleans.Domain.Connections;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ErrorHandlingTests
    {
        [TestMethod]
        public async Task WorkflowShouldFail_OnActivityException()
        {
            var activity = Substitute.For<IActivity>();
            activity.Id.Returns(Guid.NewGuid());
            activity.ExecuteAsync(Arg.Any<IContext>()).Throws(new Exception());
            activity.Status.Returns(ActivityStatus.Failed);
                        
            var workflowDefinition = new WorkflowDefinition(Guid.NewGuid(), 1,
                new[] { activity },
                new Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]>
                {
                    
                });
            
            var workflow = new Workflow(Guid.NewGuid(), new(), activity, workflowDefinition);
            
            // Act
            await workflow.Run();

            // Assert
            _ = activity.Received(1).ExecuteAsync(Arg.Any<IContext>());
            activity.Received(1).Fail(Arg.Any<Exception>());
            Assert.AreEqual(Workflow.WorkflowStatus.Failed, workflow.Status);
        }

        [TestMethod]
        public async Task WorkfflowShouldBeComplete_ByErrorBranch_OnException()
        {
            var firstActivity = Substitute.For<IActivity>();
            firstActivity.Id.Returns(Guid.NewGuid());
            firstActivity.ExecuteAsync(Arg.Any<IContext>()).Throws(new Exception());
            
            var targetActivity = Substitute.For<IActivity>();
            targetActivity.Id.Returns(Guid.NewGuid());
            targetActivity.Status.Returns(ActivityStatus.Completed);

            var conditionExpression = Substitute.For<IConditionExpressionRunner>();
            conditionExpression.Evaluate(Arg.Any<IContext>(), Arg.Any<Exception>()).Returns(true);

            var workflowDefinition = new WorkflowDefinition(Guid.NewGuid(), 1,
                new[] { firstActivity, targetActivity },
                new Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]>
                {
                    [firstActivity.Id] = new[]{ new OnErrorConnection(firstActivity, targetActivity, conditionExpression)},
                });

            var workflow = new Workflow(Guid.NewGuid(), new(), firstActivity, workflowDefinition);

            await workflow.Run();
            
            _ = targetActivity.Received(1).ExecuteAsync(Arg.Any<IContext>());
            Assert.AreEqual(Workflow.WorkflowStatus.Completed, workflow.Status);
        }
    }
}