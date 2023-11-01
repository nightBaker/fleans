using NSubstitute;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ExclusiveGatewayTests
    {
        [TestMethod]
        public async Task IfStatement_ShouldRun_TrueBranch()
        {
            // Arrange
            var workflowVersion = 1;

            var conditionRunner = Substitute.For<IConditionExpressionRunner>();
            conditionRunner.Evaluate(Arg.Any<IContext>()).Returns(true);

            var conditionBuilder = Substitute.For<IConditionBuilder>();
            conditionBuilder.Build().Returns(conditionRunner);

            var activity = Substitute.For<IActivity>();
            activity.ExecuteAsync(Arg.Any<IContext>()).Returns(Task.CompletedTask);

            var thenActivityBuilder = Substitute.For<IActivityBuilder>();
            thenActivityBuilder.WithId(Arg.Any<Guid>()).Returns(thenActivityBuilder);
            thenActivityBuilder.Build().Returns(activity);

            var elseActivityBuilder = Substitute.For<IActivityBuilder>();
            elseActivityBuilder.WithId(Arg.Any<Guid>()).Returns(elseActivityBuilder);

            var workflowBuilder = new WorkflowBuilder();
            var workflow = workflowBuilder
                .StartWith(new Dictionary<string, object>
                {
                    ["expressionVariable"] = true
                }, new ExclusiveGatewayBuilder()
                    .Condition(conditionBuilder)
                    .Then(thenActivityBuilder, Guid.NewGuid())
                    .Else(elseActivityBuilder, Guid.NewGuid())
                    .WithId(Guid.NewGuid()))
                .Build(Guid.NewGuid(), workflowVersion);

            // Act
            await workflow.Run();

            // Assert
            _ = activity.Received(1).ExecuteAsync(Arg.Any<IContext>());
        }

        [TestMethod]
        public async Task IfStatement_ShouldNotRun_FalseBranch()
        {
            // Arrange
            var workflowVersion = 1;

            var conditionRunner = Substitute.For<IConditionExpressionRunner>();
            conditionRunner.Evaluate(Arg.Any<IContext>()).Returns(true);

            var conditionBuilder = Substitute.For<IConditionBuilder>();
            conditionBuilder.Build().Returns(conditionRunner);

            var thenActivity = Substitute.For<IActivity>();
            thenActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(Task.CompletedTask);

            var thenActivityBuilder = Substitute.For<IActivityBuilder>();
            thenActivityBuilder.WithId(Arg.Any<Guid>()).Returns(thenActivityBuilder);
            thenActivityBuilder.Build().Returns(thenActivity);


            var elseActivity = Substitute.For<IActivity>();
            elseActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(Task.CompletedTask);

            var elseActivityBuilder = Substitute.For<IActivityBuilder>();
            elseActivityBuilder.WithId(Arg.Any<Guid>()).Returns(elseActivityBuilder);
            elseActivityBuilder.Build().Returns(elseActivity);

            var workflowBuilder = new WorkflowBuilder();
            var workflow = workflowBuilder
                .StartWith(new Dictionary<string, object>
                {
                    ["expressionVariable"] = true
                }, new ExclusiveGatewayBuilder()
                    .Condition(conditionBuilder)
                    .Then(thenActivityBuilder, Guid.NewGuid())
                    .Else(elseActivityBuilder, Guid.NewGuid())
                    .WithId(Guid.NewGuid()))
                .Build(Guid.NewGuid(), workflowVersion);

            // Act
            await workflow.Run();

            // Assert
            _ = elseActivity.Received(0).ExecuteAsync(Arg.Any<IContext>());
        }
    }
}