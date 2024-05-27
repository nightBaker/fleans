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
                 
            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");
            

            var workflow = Substitute.For<Workflow>();
            workflow.Activities.Add(start);
            workflow.Activities.Add(end1);
            workflow.Activities.Add(end2);
            workflow.Activities.Add(ifActivity);

            workflow.SequenceFlows.Add(new SequenceFlow(start, ifActivity));            
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow(ifActivity, end1, "truCondition"));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow(ifActivity, end2, "falseCondition"));

            var testWF = new WorkflowInstance(workflow);

            // Act
            testWF.StartWorkflow();
            var activityInstance = testWF.State.ActiveActivities.First(x => x.CurrentActivity.ActivityId == "if");
            testWF.CompleteActivity("if", new Dictionary<string, object> { [activityInstance.ActivityInstanceId + ExclusiveGateway.NextActivityIdKey] = "end1" });

            // Assert           
            Assert.IsTrue(testWF.State.IsCompleted);

            Assert.IsTrue(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end1"));
            Assert.IsFalse(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end2"));

        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ElseBranchNotThen()
        {
            // Arrange
            
            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");          

            var workflow = Substitute.For<Workflow>();
            workflow.Activities.Add(start);
            workflow.Activities.Add(end1);
            workflow.Activities.Add(end2);
            workflow.Activities.Add(ifActivity);

            workflow.SequenceFlows.Add(new SequenceFlow(start, ifActivity));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow(ifActivity, end1, "trueCondition"));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow(ifActivity, end2, "falseCondition"));

            var testWF = new WorkflowInstance(workflow);

            // Act
            testWF.StartWorkflow();

            var activityInstance = testWF.State.ActiveActivities.First(x=>x.CurrentActivity.ActivityId == "if");

            testWF.CompleteActivity("if", new Dictionary<string, object> { [activityInstance.ActivityInstanceId + ExclusiveGateway.NextActivityIdKey] = "end2" });

            // Assert           
            Assert.IsTrue(testWF.State.IsCompleted);

            Assert.IsFalse(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end1"));
            Assert.IsTrue(testWF.State.CompletedActivities.Any(x => x.CurrentActivity.ActivityId == "end2"));
        }

    }
}