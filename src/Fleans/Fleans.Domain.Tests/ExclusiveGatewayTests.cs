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
        private TestCluster _cluster = null!;

        [TestInitialize]
        public void Setup()
        {
            _workflow = CreateSimpleWorkflowWithExclusiveGateway();
            _cluster = CreateCluster();
        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ThenBranchNotElse()
        {
            // Arrange
            //             
            var testWF = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());

            // Act
            await testWF.SetWorkflow(_workflow);
            await testWF.StartWorkflow();
            await testWF.CompleteConditionSequence("if", "seq2", true);
            await testWF.CompleteConditionSequence("if", "seq3", false);
            await testWF.CompleteActivity("if", new Dictionary<string, object>());

            // Assert           
            Assert.IsTrue(await (await testWF.GetState()).IsCompleted());

            var completed = await (await testWF.GetState()).GetCompletedActivities();
            var completedActivities = new List<Activities.Activity>();
            foreach (var complete in completed)
            {
                var activity = await complete.GetCurrentActivity();
                completedActivities.Add(activity);
            }

            Assert.IsTrue(completedActivities.Any(x => x.ActivityId == "end1"));
            Assert.IsFalse(completedActivities.Any(x => x.ActivityId == "end2"));

            _cluster.StopAllSilos();

        }
        
        [TestMethod]
        public async Task IfStatement_ShouldRun_ElseBranchNotThen()
        {
            // Arrange                        
            var testWF = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());

            // Act
            await testWF.SetWorkflow(_workflow);
            await testWF.StartWorkflow();
            await testWF.CompleteConditionSequence("if", "seq2", false);
            await testWF.CompleteConditionSequence("if", "seq3", true);
            await testWF.CompleteActivity("if", new Dictionary<string, object>());

            // Assert           
            Assert.IsTrue(await (await testWF.GetState()).IsCompleted());

            var completed = await (await testWF.GetState()).GetCompletedActivities();
            var completedActivities = new List<Activities.Activity>();
            foreach (var complete in completed)
            {
                var activity = await complete.GetCurrentActivity();
                completedActivities.Add(activity);
            }

            Assert.IsTrue(completedActivities.Any(x => x.ActivityId == "end2"));
            Assert.IsFalse(completedActivities.Any(x => x.ActivityId == "end1"));

            _cluster.StopAllSilos();
        }

        private static TestCluster CreateCluster()
        {
            var builder = new TestClusterBuilder();
            var cluster = builder.Build();
            cluster.Deploy();
            return cluster;
        }

        private static IWorkflowDefinition CreateSimpleWorkflowWithExclusiveGateway()
        {
            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");

            var workflow = new WorkflowDefinition { WorkflowId = "workflow1", Activities = new List<Activities.Activity>(), SequenceFlows = new List<SequenceFlow>() };
            workflow.Activities.Add(start);
            workflow.Activities.Add(end1);
            workflow.Activities.Add(end2);
            workflow.Activities.Add(ifActivity);

            workflow.SequenceFlows.Add(new SequenceFlow( "seq1", start, ifActivity ));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq2", ifActivity, end1, "trueCondition"));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq3", ifActivity, end2, "falseCondition"));
            return workflow;
        }
    }

    public class EventPublisherMock : Grain, IEventPublisher
    {
        public Task Publish(IDomainEvent domainEvent)
        {
            Debug.WriteLine($"Publishing event {domainEvent.GetType().Name}");
            return Task.CompletedTask;
        }
    }
}