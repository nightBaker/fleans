using Moq;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ExclusiveGatewayTests
    {
        [TestMethod]
        public async Task IfStatement_ShouldRun_TrueBranch()
        {
            var workflow = new WorkflowBuilder()
                .StartWith(new Dictionary<string, object>
                {
                    ["expressionVariable"] = true
                })
                .AddActivity(Guid.NewGuid(), new ExclusiveGatewayBuilder()
                    .Condition(Mock.Of<IConditionBuilder>())
                    .Then(Mock.Of<IActivityBuilder>(), Guid.NewGuid())
                    .Else(Mock.Of<IActivityBuilder>(), Guid.NewGuid())
                )
                .Build(Guid.NewGuid(), 1);

            await workflow.Run();
        }
    }
}