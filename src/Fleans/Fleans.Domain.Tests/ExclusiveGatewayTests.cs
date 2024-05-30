using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using NSubstitute;
using System.Diagnostics;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ExclusiveGatewayTests
    {
        private Workflow _workflow = null!;

        [TestInitialize]
        public void Setup()
        {
            _workflow = CreateSimpleWorkflowWithExclusiveGateway();
        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ThenBranchNotElse()
        {
            // Arrange            
            var testWF = new WorkflowInstance(Guid.NewGuid(), workflow: _workflow);

            // Act
            testWF.StartWorkflow(Substitute.For<IEventPublisher>());
            testWF.CompleteConditionSequence("if", "seq2", true);
            testWF.CompleteConditionSequence("if", "seq3", false);
            testWF.CompleteActivity("if", new Dictionary<string, object>(), Substitute.For<IEventPublisher>());

            // Assert           
            Assert.IsTrue(testWF.State.IsCompleted);

            Assert.IsTrue(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end1"));
            Assert.IsFalse(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end2"));

        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ElseBranchNotThen()
        {
            // Arrange                        
            var testWF = new WorkflowInstance(Guid.NewGuid(), workflow: _workflow);

            // Act
            testWF.StartWorkflow(Substitute.For<IEventPublisher>());
            testWF.CompleteConditionSequence("if", "seq2", false);
            testWF.CompleteConditionSequence("if", "seq3", true);
            testWF.CompleteActivity("if", new Dictionary<string, object>(), Substitute.For<IEventPublisher>());

            // Assert           
            Assert.IsTrue(testWF.State.IsCompleted);

            Assert.IsFalse(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end1"));
            Assert.IsTrue(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end2"));
        }

        [TestMethod]
        public async Task IfStatement_Should_Publish_Event()
        {
            // Arrange                        
            var testWF = new WorkflowInstance(Guid.NewGuid(), workflow: _workflow);
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

        private static Workflow CreateSimpleWorkflowWithExclusiveGateway()
        {
            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");

            var workflow = Substitute.For<Workflow>();
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