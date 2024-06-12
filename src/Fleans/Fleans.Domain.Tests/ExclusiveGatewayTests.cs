using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using NSubstitute;
using Orleans.TestingHost;
using System.Diagnostics;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ExclusiveGatewayTests
    {
        private IWorkflowDefinition _workflow = null!;

        [TestInitialize]
        public void Setup()
        {
            _workflow = CreateSimpleWorkflowWithExclusiveGateway();
        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ThenBranchNotElse()
        {
            // Arrange
            // 
            var builder = new TestClusterBuilder();
            var cluster = builder.Build();
            cluster.Deploy();

            var testWF = cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());

            // Act
            await testWF.StartWorkflow(Substitute.For<IEventPublisher>());
            await testWF.CompleteConditionSequence("if", "seq2", true);
            await testWF.CompleteConditionSequence("if", "seq3", false);
            await testWF.CompleteActivity("if", new Dictionary<string, object>(), Substitute.For<IEventPublisher>());

            // Assert           
            Assert.IsTrue(testWF.GetState().IsCompleted);

            var completed = await testWF.GetState().Result.GetCompletedActivities();

            Assert.IsTrue(completed.Any(x => x.CurrentActivity.ActivityId == "end1"));
            Assert.IsFalse(completed.Any(x => x.CurrentActivity.ActivityId == "end2"));

            cluster.StopAllSilos();

        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ElseBranchNotThen()
        {
            // Arrange                        
            var testWF = new WorkflowInstance();
            testWF.SetWorkflow(_workflow, new WorkflowInstanceState());

            // Act
            testWF.StartWorkflow(Substitute.For<IEventPublisher>());
            testWF.CompleteConditionSequence("if", "seq2", false);
            testWF.CompleteConditionSequence("if", "seq3", true);
            testWF.CompleteActivity("if", new Dictionary<string, object>(), Substitute.For<IEventPublisher>());

            // Assert           
            Assert.IsTrue(await (await testWF.GetState()).IsCompleted());

            var completed = await testWF.State.GetCompletedActivities();

            Assert.IsFalse(completed.Any(x => x.CurrentActivity.ActivityId == "end1"));
            Assert.IsTrue(completed.Any(x => x.CurrentActivity.ActivityId == "end2"));
        }

        [TestMethod]
        public async Task IfStatement_Should_Publish_Event()
        {
            // Arrange                        
            var testWF = new WorkflowInstance();
            testWF.SetWorkflow(_workflow, new WorkflowInstanceState());

            var eventPublisher = Substitute.For<IEventPublisher>();            

            // Act
            testWF.StartWorkflow(eventPublisher);
            testWF.CompleteConditionSequence("if", "seq2", false);
            testWF.CompleteConditionSequence("if", "seq3", true);
            testWF.CompleteActivity("if", new Dictionary<string, object>(), eventPublisher);

            // Assert           
            eventPublisher.Received(5).Publish(Arg.Any<IDomainEvent>());
            eventPublisher.Received(2).Publish(Arg.Any<EvaluateConditionEvent>());
        }

        private static IWorkflowDefinition CreateSimpleWorkflowWithExclusiveGateway()
        {
            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");

            var workflow = new WorkflowDefinition("workflow1", new List<Activities.Activity>(), new List<SequenceFlow>());
            workflow.Activities.Add(start);
            workflow.Activities.Add(end1);
            workflow.Activities.Add(end2);
            workflow.Activities.Add(ifActivity);

            workflow.SequenceFlows.Add(new SequenceFlow("seq1", source: start, target: ifActivity));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq2", ifActivity, end1, "trueCondition"));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq3", ifActivity, end2, "falseCondition"));
            return workflow;
        }
    }
}