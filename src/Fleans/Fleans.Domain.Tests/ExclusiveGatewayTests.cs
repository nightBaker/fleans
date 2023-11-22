using Fleans.Domain.Connections;
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
            var conditionRunner = Substitute.For<IConditionExpressionRunner>();
            conditionRunner.Evaluate(Arg.Any<IContext>()).Returns(true);
            
            var exclusiveGateway = new ExclusiveGatewayActivity(Guid.NewGuid(), conditionRunner);

            var thenActivity = Substitute.For<IActivity>();
            thenActivity.Id.Returns(Guid.NewGuid());
            thenActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));
            thenActivity.Status.Returns(ActivityStatus.Completed);            
            
            var elseActivity = Substitute.For<IActivity>();
            elseActivity.Id.Returns(Guid.NewGuid());
            elseActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));
           
            var workflowDefinition = new WorkflowDefinition(Guid.NewGuid(), 1,
                new[] { exclusiveGateway, thenActivity , elseActivity },
                new Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]>
                {
                    [exclusiveGateway.Id] = new[] { 
                        new ExclusiveGatewayConnection(exclusiveGateway, thenActivity, true),
                        new ExclusiveGatewayConnection(exclusiveGateway, elseActivity, false) 
                    },                    
                });
            var workflow = new Workflow(Guid.NewGuid(), new(), exclusiveGateway, workflowDefinition);

            // Act
            await workflow.Run();

            // Assert
            _ = thenActivity.Received(1).ExecuteAsync(Arg.Any<IContext>());
            _ = elseActivity.Received(0).ExecuteAsync(Arg.Any<IContext>());
        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ElseBranchNotThen()
        {
            // Arrange           
            var conditionRunner = Substitute.For<IConditionExpressionRunner>();
            conditionRunner.Evaluate(Arg.Any<IContext>()).Returns(false);

            var exclusiveGateway = new ExclusiveGatewayActivity(Guid.NewGuid(), conditionRunner);

            var thenActivity = Substitute.For<IActivity>();
            thenActivity.Id.Returns(Guid.NewGuid());
            thenActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));            

            var elseActivity = Substitute.For<IActivity>();
            elseActivity.Id.Returns(Guid.NewGuid());
            elseActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));
            elseActivity.Status.Returns(ActivityStatus.Completed);

            var workflowDefinition = new WorkflowDefinition(Guid.NewGuid(), 1,
                new[] { exclusiveGateway, thenActivity, elseActivity },
                new Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]>
                {
                    [exclusiveGateway.Id] = new[] {
                        new ExclusiveGatewayConnection(exclusiveGateway, thenActivity, true),
                        new ExclusiveGatewayConnection(exclusiveGateway, elseActivity, false)
                    },
                });
            var workflow = new Workflow(Guid.NewGuid(), new(), exclusiveGateway, workflowDefinition);

            // Act
            await workflow.Run();

            // Assert
            _ = thenActivity.Received(0).ExecuteAsync(Arg.Any<IContext>());
            _ = elseActivity.Received(1).ExecuteAsync(Arg.Any<IContext>());
        }


    }
}