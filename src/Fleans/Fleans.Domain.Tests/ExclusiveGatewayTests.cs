using Moq;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ExclusiveGatewayTests
    {
        [TestMethod]
        public async Task IfStatement_ShouldRun_TrueBranch()
        {
            var conditionRunner = new Mock<IConditionExpressionRunner>();
            conditionRunner.Setup(x => x.Evaluate(It.IsAny<IContext>())).Returns(true);

            var conditionBuilder = new Mock<IConditionBuilder>();
            conditionBuilder.Setup(x => x.Build()).Returns(conditionRunner.Object);

            var activity = new Mock<IActivity>();
            activity.Setup(x => x.ExecuteAsync(It.IsAny<IContext>())).Returns(Task.CompletedTask).Verifiable();

            var activityBuilder = new Mock<IActivityBuilder>();
            activityBuilder.Setup(x => x.WithId(It.IsAny<Guid>())).Returns(activityBuilder.Object);
            activityBuilder.Setup(x => x.Build()).Returns(activity.Object);

            var elseActivityBuilder = new Mock<IActivityBuilder>();
            elseActivityBuilder.Setup(x => x.WithId(It.IsAny<Guid>())).Returns(elseActivityBuilder.Object);

            var workflow = new WorkflowBuilder()
                .StartWith(new Dictionary<string, object>
                            {
                                ["expressionVariable"] = true
                            },
                        new ExclusiveGatewayBuilder()
                            .Condition(conditionBuilder.Object)
                            .Then(activityBuilder.Object, Guid.NewGuid())
                            .Else(elseActivityBuilder.Object, Guid.NewGuid())
                            .WithId(Guid.NewGuid()))
                .Build(Guid.NewGuid(), 1);

            await workflow.Run();

            activity.Verify(x => x.ExecuteAsync(It.IsAny<IContext>()), Times.Once);
        }
    }
}