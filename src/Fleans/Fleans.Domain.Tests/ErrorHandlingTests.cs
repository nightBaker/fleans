using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Diagnostics;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ErrorHandlingTests
    {
        [TestMethod]
        public async Task ActivityShouldFail_OnException()
        {
            // Arrange
            var workflowVersion = 1;

            var conditionRunner = Substitute.For<IConditionExpressionRunner>();
            conditionRunner.Evaluate(Arg.Any<IContext>(), Arg.Any<Exception>()).Returns(false);

            var conditionBuilder = Substitute.For<IConditionBuilder>();
            conditionBuilder.Build().Returns(conditionRunner);

            var activity = Substitute.For<IActivity>();
            activity.Id.Returns(Guid.NewGuid());
            activity.ExecuteAsync(Arg.Any<IContext>()).Throws(new Exception());            
            activity.Status.Returns(ActivityStatus.Failed); 

            var activityBuilder = Substitute.For<IActivityBuilder>();
            activityBuilder.Build().Returns(new ActivityBuilderResult
            {
                Activity = activity,
                ChildActivities = new List<IActivity>(),
                Connections = new List<IWorkflowConnection<IActivity, IActivity>>()
            });
            
            var workflowBuilder = new WorkflowBuilder();
            var workflow = workflowBuilder
                .With(new Dictionary<string, object>
                {

                })
                .With(new WorkflowDefinitionBuilder(Guid.NewGuid(), workflowVersion)
                    .StartWith(activityBuilder))
                .Build(Guid.NewGuid());

            // Act
            await workflow.Run();

            // Assert
            _ = activity.Received(1).ExecuteAsync(Arg.Any<IContext>());
            activity.Received(1).Fail(Arg.Any<Exception>());
            Assert.AreEqual(Workflow.WorkflowStatus.Failed, workflow.Status);            
        }        
    }
}