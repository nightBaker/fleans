using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
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
            var trueCondition = Substitute.For<ICondition>();
            trueCondition.Evaluate().Returns(true);

            var falseCondition = Substitute.For<ICondition>();
            falseCondition.Evaluate().Returns(false);

            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");
            ifActivity.AddConditionalFlow(new ConditionalSequenceFlow(ifActivity, end1, trueCondition));
            ifActivity.AddConditionalFlow(new ConditionalSequenceFlow(ifActivity, end2, falseCondition));

            var workflow = Substitute.For<Workflow>();
            workflow.Activities.Add(start);
            workflow.Activities.Add(end1);
            workflow.Activities.Add(end2);
            workflow.Activities.Add(ifActivity);

            workflow.SequenceFlows.Add(new SequenceFlow(start, ifActivity));
            workflow.SequenceFlows.Add(new SequenceFlow(ifActivity, end1));
            workflow.SequenceFlows.Add(new SequenceFlow(ifActivity, end2));

            var testWF = new WorkflowInstance(workflow);

            // Act
            testWF.StartWorkflow();

            // Assert           
            Assert.IsTrue(testWF.State.IsCompleted);

            Assert.IsTrue(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end1"));
            Assert.IsFalse(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end2"));

        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ElseBranchNotThen()
        {
            // Arrange
            var trueCondition = Substitute.For<ICondition>();
            trueCondition.Evaluate().Returns(true);

            var falseCondition = Substitute.For<ICondition>();
            falseCondition.Evaluate().Returns(false);

            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");
            ifActivity.AddConditionalFlow(new ConditionalSequenceFlow(ifActivity, end1, falseCondition));
            ifActivity.AddConditionalFlow(new ConditionalSequenceFlow(ifActivity, end2, trueCondition));

            var workflow = Substitute.For<Workflow>();
            workflow.Activities.Add(start);
            workflow.Activities.Add(end1);
            workflow.Activities.Add(end2);
            workflow.Activities.Add(ifActivity);

            workflow.SequenceFlows.Add(new SequenceFlow(start, ifActivity));
            workflow.SequenceFlows.Add(new SequenceFlow(ifActivity, end1));
            workflow.SequenceFlows.Add(new SequenceFlow(ifActivity, end2));

            var testWF = new WorkflowInstance(workflow);

            // Act
            testWF.StartWorkflow();

            // Assert           
            Assert.IsTrue(testWF.State.IsCompleted);

            Assert.IsFalse(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end1"));
            Assert.IsTrue(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end2"));
        }

    }
}