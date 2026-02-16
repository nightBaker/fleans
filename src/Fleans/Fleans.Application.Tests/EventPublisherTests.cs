using Fleans.Application;
using Fleans.Application.Conditions;
using Fleans.Application.Events;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Application.Scripts;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Persistence;
using Fleans.Domain.Sequences;
using Fleans.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Orleans.TestingHost.InProcess;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EventPublisherTests
{
    private static SqliteConnection? _sharedConnection;
    private static readonly object _lock = new();

    private TestCluster _cluster = null!;
    private IWorkflowQueryService _queryService = null!;

    [TestInitialize]
    public void Setup()
    {
        lock (_lock)
        {
            _sharedConnection = new SqliteConnection("DataSource=file::memory:?cache=shared");
            _sharedConnection.Open();
        }

        _cluster = CreateCluster();
        _queryService = ((InProcessSiloHandle)_cluster.Primary).SiloHost.Services.GetRequiredService<IWorkflowQueryService>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _cluster?.StopAllSilos();

        lock (_lock)
        {
            _sharedConnection?.Close();
            _sharedConnection?.Dispose();
            _sharedConnection = null;
        }
    }

    [TestMethod]
    public async Task ConsumeEvent_ConditionHandler_ShouldEvaluateAndCompleteWorkflow()
    {
        // Arrange
        var workflow = CreateWorkflowWithExclusiveGateway();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        var instanceId = workflowInstance.GetPrimaryKey();

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — stream handler evaluates "true" condition and completes the workflow
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
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
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        var instanceId = workflowInstance.GetPrimaryKey();

        // Act
        await workflowInstance.StartWorkflow();

        // Assert — stream handler executes script and completes the workflow
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "start");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "script1");
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "end");
        Assert.AreEqual(0, snapshot.ActiveActivities.Count);
    }

    private async Task<InstanceStateSnapshot?> PollForCompletion(
        Guid instanceId, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await _queryService.GetStateSnapshot(instanceId);
            if (snapshot is not null && snapshot.IsCompleted)
                return snapshot;
            await Task.Delay(100);
        }

        return await _queryService.GetStateSnapshot(instanceId);
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
                .ConfigureServices(services =>
                {
                    services.AddDbContextFactory<FleanCommandDbContext>(options =>
                        options.UseSqlite("DataSource=file::memory:?cache=shared"));

                    services.AddDbContextFactory<FleanQueryDbContext>(options =>
                        options.UseSqlite("DataSource=file::memory:?cache=shared"));

                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.WorkflowInstances,
                        (sp, _) => new EfCoreWorkflowInstanceGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.ActivityInstances,
                        (sp, _) => new EfCoreActivityInstanceGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    services.AddSingleton<IProcessDefinitionRepository, EfCoreProcessDefinitionRepository>();
                    services.AddSingleton<IWorkflowQueryService, WorkflowQueryService>();

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

                    // Ensure DB schema is created
                    var sp = services.BuildServiceProvider();
                    using var db = sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>().CreateDbContext();
                    db.Database.EnsureCreated();
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
