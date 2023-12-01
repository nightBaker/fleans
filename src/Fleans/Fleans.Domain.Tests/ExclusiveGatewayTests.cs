using NSubstitute;
using System.Diagnostics;
using Fleans.Domain.Activities;
using Fleans.Domain.Connections;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ExclusiveGatewayTests
    {
        //todo create end process activity
        [TestMethod]
        public async Task IfStatement_ShouldRun_ThenBranchNotElse()
        {
            // Arrange
            var workflowVersion = 1;

            var conditionRunner = Substitute.For<IConditionExpressionRunner>();
            conditionRunner.Evaluate(Arg.Any<IContext>()).Returns(true);

            var conditionBuilder = Substitute.For<IConditionBuilder>();
            conditionBuilder.Build().Returns(conditionRunner);

            var activity = new ExclusiveGatewayActivity(Guid. NewGuid(), conditionRunner);
            
            var thenActivity = Substitute.For<IExecutableActivity>();
                thenActivity.Id.Returns(Guid.NewGuid());
                thenActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));
                thenActivity.Status.Returns(ActivityStatus.Completed);
            
            var elseActivity = Substitute.For<IExecutableActivity>();
            elseActivity.Id.Returns(Guid.NewGuid());
            
            var thenConnection = new ExclusiveGatewayConnection(activity, thenActivity, true);
            var elseConnection = new ExclusiveGatewayConnection(activity, elseActivity, false);
            
            var startProcessId = Guid.NewGuid();
            var startProcessEvent = Substitute.For<IStartProcessEventActivity>();
            startProcessEvent.IsDefault.Returns(true);
            startProcessEvent.Id.Returns(startProcessId);
            
            var connection = Substitute.For<IWorkflowConnection<IActivity, IActivity>>();
            connection.From.Returns( startProcessEvent);
            connection.To.Returns(activity);

            var workflow = new Workflow(Guid.NewGuid(), new(),
                new WorkflowDefinition(Guid.NewGuid(), workflowVersion,
                    new[] { startProcessEvent },
                    new[] { (IExecutableActivity)activity, elseActivity },
                    new Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]>()
                    {
                        [startProcessId] = new []{ connection },
                        [activity.Id] = new IWorkflowConnection<IActivity, IActivity>[]{ elseConnection, thenConnection }
                    }));

            // Act
            await workflow.Start();

            // Assert
            _ = thenActivity.Received(1).ExecuteAsync(Arg.Any<IContext>());
            _ = elseActivity.Received(0).ExecuteAsync(Arg.Any<IContext>());
        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ElseBranchNotThen()
        {
            // Arrange
            var workflowVersion = 1;

            var conditionRunner = Substitute.For<IConditionExpressionRunner>();
            conditionRunner.Evaluate(Arg.Any<IContext>()).Returns(false);

            var conditionBuilder = Substitute.For<IConditionBuilder>();
            conditionBuilder.Build().Returns(conditionRunner);

            var activity = new ExclusiveGatewayActivity(Guid. NewGuid(), conditionRunner);
            
            var thenActivity = Substitute.For<IExecutableActivity>();
                thenActivity.Id.Returns(Guid.NewGuid());
            
            var elseActivity = Substitute.For<IExecutableActivity>();
            elseActivity.Id.Returns(Guid.NewGuid());
            elseActivity.ExecuteAsync(Arg.Any<IContext>()).Returns(new ActivityExecutionResult(ActivityResultStatus.Completed));
            elseActivity.Status.Returns(ActivityStatus.Completed);
            
            var thenConnection = new ExclusiveGatewayConnection(activity, thenActivity, true);
            var elseConnection = new ExclusiveGatewayConnection(activity, elseActivity, false);
            
            var startProcessId = Guid.NewGuid();
            var startProcessEvent = Substitute.For<IStartProcessEventActivity>();
            startProcessEvent.IsDefault.Returns(true);
            startProcessEvent.Id.Returns(startProcessId);
            
            var connection = Substitute.For<IWorkflowConnection<IActivity, IActivity>>();
            connection.From.Returns( startProcessEvent);
            connection.To.Returns(activity);

            var workflow = new Workflow(Guid.NewGuid(), new(),
                new WorkflowDefinition(Guid.NewGuid(), workflowVersion,
                    new[] { startProcessEvent },
                    new[] { (IExecutableActivity)activity, elseActivity },
                    new Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]>()
                    {
                        [startProcessId] = new []{ connection },
                        [activity.Id] = new IWorkflowConnection<IActivity, IActivity>[]{ elseConnection, thenConnection }
                    }));

            // Act
            await workflow.Start();

            // Assert
            _ = thenActivity.Received(0).ExecuteAsync(Arg.Any<IContext>());
            _ = elseActivity.Received(1).ExecuteAsync(Arg.Any<IContext>());
        }


    }
}