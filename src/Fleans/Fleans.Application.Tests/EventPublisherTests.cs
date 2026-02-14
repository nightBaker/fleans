using Fleans.Application.Conditions;
using Fleans.Application.Events;
using Fleans.Application.Scripts;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EventPublisherTests
{
    private TestCluster _cluster = null!;

    [TestInitialize]
    public void Setup()
    {
        _cluster = CreateCluster();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _cluster?.StopAllSilos();
    }

    [TestMethod]
    public async Task ConsumeEvent_ConditionHandler_ShouldEvaluateAndCompleteWorkflow()
    {
        // Arrange
        var workflow = CreateWorkflowWithExclusiveGateway();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — stream handler evaluates "true" condition and completes the workflow
        var snapshot = await PollForCompletion(workflowInstance);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "start");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "if");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end1");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);
    }

    [TestMethod]
    public async Task ConsumeEvent_ScriptHandler_ShouldExecuteAndCompleteWorkflow()
    {
        // Arrange
        var workflow = CreateWorkflowWithScriptTask();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — stream handler executes script and completes the workflow
        var snapshot = await PollForCompletion(workflowInstance);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "start");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "script1");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);
    }

    private static async Task<InstanceStateSnapshot> PollForCompletion(
        IWorkflowInstance workflowInstance, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await workflowInstance.GetStateSnapshot();
            if (snapshot.IsCompleted)
                return snapshot;
            await Task.Delay(100);
        }

        return await workflowInstance.GetStateSnapshot();
    }

    private static IWorkflowDefinition CreateWorkflowWithExclusiveGateway()
    {
        var start = new StartEvent("start");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var ifActivity = new ExclusiveGateway("if");

        return new WorkflowDefinition
        {
            WorkflowId = "workflow1",
            Activities = [start, ifActivity, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, ifActivity),
                new ConditionalSequenceFlow("seq2", ifActivity, end1, "true"),
                new DefaultSequenceFlow("seqDefault", ifActivity, end2)
            ]
        };
    }

    private static IWorkflowDefinition CreateWorkflowWithScriptTask()
    {
        var start = new StartEvent("start");
        var script = new ScriptTask("script1", "_context.x = 10");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = "workflow2",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, script),
                new SequenceFlow("seq2", script, end)
            ]
        };
    }

    private static TestCluster CreateCluster()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        builder.AddClientBuilderConfigurator<ClientConfigurator>();
        var cluster = builder.Build();
        cluster.Deploy();
        return cluster;
    }

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder
                .AddMemoryStreams(WorkflowEventsPublisher.StreamProvider)
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryGrainStorage("workflowInstances")
                .AddMemoryGrainStorage("activityInstances")
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IConditionExpressionEvaluator, SimpleConditionEvaluator>();
                    services.AddSingleton<IScriptExpressionExecutor, SimpleScriptExecutor>();
                    services.AddSerializer(serializerBuilder =>
                    {
                        serializerBuilder.AddNewtonsoftJsonSerializer(
                            isSupported: type => type == typeof(ExpandoObject),
                            new Newtonsoft.Json.JsonSerializerSettings
                            {
                                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                            });
                    });
                });
    }

    private class ClientConfigurator : IClientBuilderConfigurator
    {
        public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) =>
            clientBuilder.AddMemoryStreams(WorkflowEventsPublisher.StreamProvider);
    }

    private class SimpleConditionEvaluator : IConditionExpressionEvaluator
    {
        public Task<bool> Evaluate(string expression, ExpandoObject variables)
        {
            return Task.FromResult(string.Equals(expression, "true", StringComparison.OrdinalIgnoreCase));
        }
    }

    private class SimpleScriptExecutor : IScriptExpressionExecutor
    {
        public Task<ExpandoObject> Execute(string script, ExpandoObject variables, string scriptFormat)
        {
            return Task.FromResult(variables);
        }
    }
}
