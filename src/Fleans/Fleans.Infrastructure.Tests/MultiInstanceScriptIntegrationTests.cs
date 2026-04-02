using Fleans.Application;
using Fleans.Application.Events;
using Fleans.Application.Scripts;
using Fleans.Application.Conditions;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Persistence;
using Fleans.Domain.Sequences;
using Fleans.Infrastructure.Scripts;
using Fleans.Infrastructure.Conditions;
using Fleans.Persistence;
using Fleans.Persistence.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Sieve.Models;
using Sieve.Services;
using Orleans.TestingHost.InProcess;
using System.Dynamic;

namespace Fleans.Infrastructure.Tests;

[TestClass]
public class MultiInstanceScriptIntegrationTests
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

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        _cluster = builder.Build();
        _cluster.Deploy();

        _queryService = ((InProcessSiloHandle)_cluster.Primary).SiloHost.Services
            .GetRequiredService<IWorkflowQueryService>();
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
    public async Task ParallelCardinality_ShouldSetLoopCounterAndProduceResults()
    {
        // Arrange — script uses loopCounter to produce per-iteration result
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"done-\" + _context.loopCounter"),
            IsSequential: false,
            LoopCardinality: 3,
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-script-cardinality",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-script-cardinality");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.AreEqual(4, scriptCompletions, "3 iterations + 1 host");

        // Verify output aggregation via grain API
        var rootVarsSnapshot = snapshot.VariableStates.FirstOrDefault();
        Assert.IsNotNull(rootVarsSnapshot, "Root variable state should exist");

        var resultsObj = await instance.GetVariable(rootVarsSnapshot.VariablesId, "results");
        Assert.IsNotNull(resultsObj, "Output collection 'results' should be present");

        var results = ((IEnumerable<object?>)resultsObj).Select(v => v?.ToString()).ToList();
        Assert.AreEqual(3, results.Count, "Should have 3 output items");
        CollectionAssert.Contains(results, "done-0", "Should contain done-0");
        CollectionAssert.Contains(results, "done-1", "Should contain done-1");
        CollectionAssert.Contains(results, "done-2", "Should contain done-2");
    }

    [TestMethod]
    public async Task ParallelCollection_ShouldSetItemVariableAndAggregateOutput()
    {
        // Arrange — script processes each item from InputCollection
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"processed-\" + _context.item"),
            IsSequential: false,
            InputCollection: "items",
            InputDataItem: "item",
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-script-collection",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-script-collection");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
        var instanceId = instance.GetPrimaryKey();

        dynamic initVars = new ExpandoObject();
        initVars.items = new List<object> { "A", "B", "C" };
        await instance.SetInitialVariables(initVars);

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        // Verify actual aggregated values via grain API (raw ExpandoObject)
        var rootVarsSnapshot = snapshot.VariableStates.FirstOrDefault();
        Assert.IsNotNull(rootVarsSnapshot, "Root variable state should exist");

        var resultsObj = await instance.GetVariable(rootVarsSnapshot.VariablesId, "results");
        Assert.IsNotNull(resultsObj, "Output collection 'results' should be present");

        var results = ((IEnumerable<object?>)resultsObj).Select(v => v?.ToString()).ToList();
        Assert.AreEqual(3, results.Count, "Should have 3 output items");
        CollectionAssert.Contains(results, "processed-A", "Should contain processed-A");
        CollectionAssert.Contains(results, "processed-B", "Should contain processed-B");
        CollectionAssert.Contains(results, "processed-C", "Should contain processed-C");
    }

    [TestMethod]
    public async Task SequentialCollection_ShouldProduceOrderedOutput()
    {
        // Arrange — sequential MI processes items one at a time
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = \"seq-\" + _context.item"),
            IsSequential: true,
            InputCollection: "items",
            InputDataItem: "item",
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-script-seq-collection",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-script-seq-collection");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
        var instanceId = instance.GetPrimaryKey();

        dynamic initVars = new ExpandoObject();
        initVars.items = new List<object> { "X", "Y", "Z" };
        await instance.SetInitialVariables(initVars);

        // Act
        await instance.StartWorkflow();

        // Assert
        var snapshot = await PollForCompletion(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted, "Workflow should be completed");

        // Sequential MI preserves order — verify via grain API
        var rootVarsSnapshot = snapshot.VariableStates.FirstOrDefault();
        Assert.IsNotNull(rootVarsSnapshot, "Root variable state should exist");

        var resultsObj = await instance.GetVariable(rootVarsSnapshot.VariablesId, "results");
        Assert.IsNotNull(resultsObj, "Output collection 'results' should be present");

        var results = ((IEnumerable<object?>)resultsObj).Select(v => v?.ToString()).ToList();
        Assert.AreEqual(3, results.Count, "Should have 3 output items");
        // Sequential ensures ordering
        Assert.AreEqual("seq-X", results[0], "First item should be seq-X");
        Assert.AreEqual("seq-Y", results[1], "Second item should be seq-Y");
        Assert.AreEqual("seq-Z", results[2], "Third item should be seq-Z");
    }

    [TestMethod]
    public async Task ParallelCardinality_WhenScriptFails_ShouldFailHostAndCancelSiblings()
    {
        // Arrange — iteration 1 (loopCounter=1) divides by zero
        var start = new StartEvent("start");
        var script = new MultiInstanceActivity(
            "script",
            new ScriptTask("script", "_context.result = 1 / (_context.loopCounter - 1)"),
            IsSequential: false,
            LoopCardinality: 3,
            OutputCollection: "results",
            OutputDataItem: "result");
        var end = new EndEvent("end");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "mi-script-fail",
            Activities = [start, script, end],
            SequenceFlows =
            [
                new SequenceFlow("s1", start, script),
                new SequenceFlow("s2", script, end)
            ]
        };

        var processGrain = _cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("mi-script-fail");
        await processGrain.DeployVersion(workflow, "<xml/>");

        var instance = await processGrain.CreateInstance();
        var instanceId = instance.GetPrimaryKey();

        // Act
        await instance.StartWorkflow();

        // Assert — poll until no active activities remain
        var snapshot = await PollForNoActiveActivities(instanceId);
        Assert.IsNotNull(snapshot);

        var failedIterations = snapshot.CompletedActivities
            .Where(a => a.ActivityId == "script" && a.ErrorState is not null)
            .ToList();
        Assert.IsTrue(failedIterations.Count >= 1,
            $"At least one script iteration should have failed, got {failedIterations.Count}");

        var errorState = failedIterations.First().ErrorState!;
        Assert.AreEqual(500, errorState.Code, "Generic exception should produce error code 500");

        // MI host should also be completed (failed) — host + iterations = 4 total
        var scriptCompletions = snapshot.CompletedActivities.Count(a => a.ActivityId == "script");
        Assert.AreEqual(4, scriptCompletions, "All 3 iterations + host should be in completed list");

        // Siblings should be cancelled
        var cancelledSiblings = snapshot.CompletedActivities
            .Where(a => a.ActivityId == "script" && a.IsCancelled)
            .ToList();
        Assert.IsTrue(cancelledSiblings.Count >= 1,
            $"At least one sibling should be cancelled, got {cancelledSiblings.Count}");
    }

    private async Task<Application.QueryModels.InstanceStateSnapshot?> PollForCompletion(
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

    private async Task<Application.QueryModels.InstanceStateSnapshot?> PollForNoActiveActivities(
        Guid instanceId, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await _queryService.GetStateSnapshot(instanceId);
            if (snapshot is not null && snapshot.ActiveActivities.Count == 0)
                return snapshot;
            await Task.Delay(100);
        }
        return await _queryService.GetStateSnapshot(instanceId);
    }

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder
                .AddMemoryStreams(WorkflowEventsPublisher.StreamProvider)
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryGrainStorage(GrainStorageNames.MessageCorrelations)
                .AddMemoryGrainStorage(GrainStorageNames.SignalCorrelations)
                .AddCustomStorageBasedLogConsistencyProviderAsDefault()
                .UseInMemoryReminderService()
                .ConfigureServices(services =>
                {
                    services.AddDbContextFactory<FleanCommandDbContext>(options =>
                        options.UseSqlite("DataSource=file::memory:?cache=shared"));

                    services.AddDbContextFactory<FleanQueryDbContext>(options =>
                        options.UseSqlite("DataSource=file::memory:?cache=shared"));

                    services.AddSingleton<IWorkflowStateProjection, EfCoreWorkflowStateProjection>();
                    services.AddSingleton<EfCoreEventStore>();
                    services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<EfCoreEventStore>());

                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.TimerSchedulers,
                        (sp, _) => new EfCoreTimerSchedulerGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    services.AddSingleton<IProcessDefinitionRepository, EfCoreProcessDefinitionRepository>();
                    services.AddSingleton<ISieveProcessor, ApplicationSieveProcessor>();
                    services.Configure<SieveOptions>(options =>
                    {
                        options.DefaultPageSize = 20;
                        options.MaxPageSize = 100;
                    });
                    services.AddSingleton<IWorkflowQueryService, WorkflowQueryService>();
                    services.AddSingleton<IScriptExpressionExecutor, DynamicExpressoScriptExpressionExecutor>();
                    services.AddSingleton<IConditionExpressionEvaluator, DynamicExpressoConditionExpressionEvaluator>();

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
}
