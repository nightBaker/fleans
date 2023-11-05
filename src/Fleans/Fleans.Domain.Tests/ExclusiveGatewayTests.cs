using NSubstitute;
using System.Diagnostics;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ExclusiveGatewayTests
    {
        [TestMethod]
        public async Task IfStatement_ShouldRun_ThenBranchNotElse()
        {
            // Arrange
            var workflowVersion = 1;

            var conditionRunner = Substitute.For<IConditionExpressionRunner>();
            conditionRunner.Evaluate(Arg.Any<IContext>());
            conditionRunner.Result.Returns(true);

            var conditionBuilder = Substitute.For<IConditionBuilder>();
            conditionBuilder.Build().Returns(conditionRunner);

            var activity = Substitute.For<IActivity>();
            activity.Id.Returns(Guid.NewGuid());
            activity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));
            activity.Status.Returns(ActivityStatus.Completed);

            var thenActivityBuilder = Substitute.For<IActivityBuilder>();          
            thenActivityBuilder.Build().Returns(new ActivityBuilderResult 
            { 
                Activity = activity,
                ChildActivities = new List<IActivity>(),
                Connections = new List<IWorkflowConnection<IActivity, IActivity>>()
            });

            var elseActivity = Substitute.For<IActivity>();
            elseActivity.Id.Returns(Guid.NewGuid());
            elseActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));

            var elseActivityBuilder = Substitute.For<IActivityBuilder>();
            elseActivityBuilder.Build().Returns(new ActivityBuilderResult
            {
                Activity = elseActivity,
                ChildActivities = new List<IActivity>(),
                Connections = new List<IWorkflowConnection<IActivity, IActivity>>()
            });

            var workflowBuilder = new WorkflowBuilder();
            var workflow = workflowBuilder
                .With(new Dictionary<string, object>
                {
                    ["expressionVariable"] = true
                })
                .With(new WorkflowDefinitionBuilder(Guid.NewGuid(), workflowVersion)
                    .StartWith(new ExclusiveGatewayBuilder(Guid.NewGuid())
                            .Condition(conditionBuilder)
                            .Then(thenActivityBuilder)
                            .Else(elseActivityBuilder)))
                .Build(Guid.NewGuid());

            // Act
            await workflow.Run();

            // Assert
            _ = activity.Received(1).ExecuteAsync(Arg.Any<IContext>());
            _ = elseActivity.Received(0).ExecuteAsync(Arg.Any<IContext>());
        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ElseBranchNotThen()
        {
            // Arrange
            var workflowVersion = 1;

            var conditionRunner = Substitute.For<IConditionExpressionRunner>();
            conditionRunner.Evaluate(Arg.Any<IContext>());
            conditionRunner.Result.Returns(false);

            var conditionBuilder = Substitute.For<IConditionBuilder>();
            conditionBuilder.Build().Returns(conditionRunner);

            var activity = Substitute.For<IActivity>();
            activity.Id.Returns(Guid.NewGuid());
            activity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));
            

            var thenActivityBuilder = Substitute.For<IActivityBuilder>();
            thenActivityBuilder.Build().Returns(new ActivityBuilderResult
            {
                Activity = activity,
                ChildActivities = new List<IActivity>(),
                Connections = new List<IWorkflowConnection<IActivity, IActivity>>()
            });

            var elseActivity = Substitute.For<IActivity>();
            elseActivity.Id.Returns(Guid.NewGuid());
            elseActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));
            elseActivity.Status.Returns(ActivityStatus.Completed);

            var elseActivityBuilder = Substitute.For<IActivityBuilder>();
            elseActivityBuilder.Build().Returns(new ActivityBuilderResult
            {
                Activity = elseActivity,
                ChildActivities = new List<IActivity>(),
                Connections = new List<IWorkflowConnection<IActivity, IActivity>>()
            });

            var workflowBuilder = new WorkflowBuilder();
            var workflow = workflowBuilder
                .With(new Dictionary<string, object>
                {
                    ["expressionVariable"] = true
                })
                .With(new WorkflowDefinitionBuilder(Guid.NewGuid(), workflowVersion)
                    .StartWith(new ExclusiveGatewayBuilder(Guid.NewGuid())
                            .Condition(conditionBuilder)
                            .Then(thenActivityBuilder)
                            .Else(elseActivityBuilder)))
                .Build(Guid.NewGuid());

            // Act
            await workflow.Run();

            // Assert
            _ = activity.Received(0).ExecuteAsync(Arg.Any<IContext>());
            _ = elseActivity.Received(1).ExecuteAsync(Arg.Any<IContext>());
        }


    }
}