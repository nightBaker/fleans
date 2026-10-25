using Fleans.Application;
using Fleans.Application.Events;
using Fleans.Application.Grains;
using Fleans.Application.Placement;
using Fleans.Application.QueryModels;
using Fleans.Application.Scripts;
using Fleans.Application.Conditions;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Persistence;
using Fleans.Domain.Sequences;
using Fleans.Persistence;
using Fleans.Persistence.Events;
using Fleans.Worker.Placement;
using Microsoft.Data.Sqlite;
using Fleans.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sieve.Models;
using Sieve.Services;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Runtime.Placement;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Orleans.TestingHost.InProcess;
using System.Dynamic;

namespace Fleans.Application.Tests;

public abstract class WorkflowTestBase
{
    private static SqliteConnection? _sharedConnection;
    private static readonly object _lock = new();

    protected TestCluster Cluster { get; private set; } = null!;
    protected IWorkflowQueryService QueryService { get; private set; } = null!;

    /// <summary>Persistence:SnapshotInterval of the test silo.</summary>
    protected const int TestSnapshotInterval = 5;

    /// <summary>Fleans:Variables:MaxSizeBytes of the test silo — small, so a test can exceed it cheaply.</summary>
    protected const int TestMaxVariablesSizeBytes = 16 * 1024;

    [TestInitialize]
    public void BaseSetup()
    {
        lock (_lock)
        {
            _sharedConnection = new SqliteConnection("DataSource=file::memory:?cache=shared");
            _sharedConnection.Open();
        }

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<EfCoreSiloConfigurator>();
        ConfigureCluster(builder);
        Cluster = builder.Build();
        Cluster.Deploy();

        QueryService = ((InProcessSiloHandle)Cluster.Primary).SiloHost.Services.GetRequiredService<IWorkflowQueryService>();
    }

    /// <summary>Lets a test class add silo configurators (e.g. a grain call filter) to its cluster.</summary>
    protected virtual void ConfigureCluster(TestClusterBuilder builder) { }

    /// <summary>
    /// Forces deactivation of all grain activations in the test cluster
    /// and waits for deactivation to complete. After this call, the next
    /// method call on any grain will trigger reactivation from the event store
    /// (snapshot + event replay).
    /// Note: This deactivates ALL grains globally, not a specific grain.
    /// </summary>
    /// <summary>
    /// Polls the query service until the workflow instance has no active activities,
    /// or until the timeout expires. Shared across integration test classes.
    /// </summary>
    protected async Task<InstanceStateSnapshot?> PollForNoActiveActivities(
        Guid instanceId, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            if (snapshot is not null && snapshot.ActiveActivities.Count == 0)
                return snapshot;
            await Task.Delay(100);
        }
        return await QueryService.GetStateSnapshot(instanceId);
    }

    /// <summary>
    /// Waits until the read model reflects the instance's current state. The projector runs behind
    /// the journal (#861), so a test that reads the read model directly — instance lists, the
    /// WorkflowInstances tables — waits here first instead of sleeping. <c>GetStateSnapshot</c>
    /// does not need it: until the row says completed, the grain answers.
    /// </summary>
    protected async Task WaitForProjection(params Guid[] instanceIds)
    {
        // Generous: a failed projection is redelivered by the stream with back-off.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        foreach (var instanceId in instanceIds)
        {
            var inspector = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId)
                .AsReference<IWorkflowInstanceInspector>();
            while (!await inspector.IsProjectionCaughtUp())
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, $"The read model of {instanceId} did not catch up");
                await Task.Delay(25);
            }
        }
    }

    /// <summary>
    /// Deactivates idle grains and polls until no workflow instance is active, for at most 3s (an
    /// instance woken by a timer may come straight back). Instances in <paramref name="mustDeactivate"/>
    /// must be gone — the call fails after 30s otherwise — so a test that relies on reactivation
    /// does not depend on timing.
    /// </summary>
    protected async Task ForceAllGrainDeactivation(params Guid[] mustDeactivate)
    {
        var managementGrain = Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
        await managementGrain.ForceActivationCollection(TimeSpan.Zero);

        var keys = mustDeactivate.Select(id => id.ToString("N")).ToList();
        var settleBy = DateTime.UtcNow.AddSeconds(3);
        var failBy = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var active = (await managementGrain.GetDetailedGrainStatistics([typeof(WorkflowInstance).FullName!]))
                .Select(s => s.GrainId.ToString()).ToList();
            var required = keys.Where(k => active.Any(a => a.Contains(k))).ToList();
            var done = keys.Count > 0 ? required.Count == 0 : DateTime.UtcNow >= settleBy;
            if (active.Count == 0 || done)
                return;
            Assert.IsTrue(DateTime.UtcNow < failBy,
                $"Workflow instances did not deactivate: {string.Join(", ", required)}");
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Gets a service registered in the silo's DI container.
    /// Useful for accessing infrastructure services like EfCoreEventStore
    /// or IDbContextFactory directly in tests.
    /// </summary>
    protected T GetSiloService<T>() where T : notnull =>
        ((InProcessSiloHandle)Cluster.Primary).SiloHost.Services.GetRequiredService<T>();

    [TestCleanup]
    public void BaseCleanup()
    {
        Cluster?.StopAllSilos();

        lock (_lock)
        {
            _sharedConnection?.Close();
            _sharedConnection?.Dispose();
            _sharedConnection = null;
        }
    }

    /// <summary>
    /// Polls for a state snapshot satisfying <paramref name="condition"/>.
    /// </summary>
    protected async Task<InstanceStateSnapshot> WaitForCondition(
        Guid instanceId, Func<InstanceStateSnapshot, bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            if (snapshot != null && condition(snapshot))
                return snapshot;
            await Task.Delay(50);
        }

        var finalSnapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(finalSnapshot, "Snapshot was null after timeout");
        Assert.IsTrue(condition(finalSnapshot),
            $"Condition not met after {timeoutMs}ms. Active: [{string.Join(", ", finalSnapshot.ActiveActivities.Select(a => $"{a.ActivityId}({a.ActivityType})"))}], Completed: [{string.Join(", ", finalSnapshot.CompletedActivityIds)}], IsCompleted: {finalSnapshot.IsCompleted}");
        return finalSnapshot;
    }

    /// <summary>
    /// Delivers a correlated message once the subscription is registered. Subscription
    /// registration is an effect of activating the catch/boundary/ESP, so it can land after
    /// the grain call that started the workflow returns. <c>DeliverMessage</c> on an
    /// unsubscribed key is a no-op returning <c>false</c>, so retrying is side-effect free.
    /// Returns <c>false</c> only if nothing subscribed within the timeout.
    /// </summary>
    protected async Task<bool> DeliverMessageWhenSubscribed(
        string messageName, string correlationValue, ExpandoObject? variables = null, int timeoutMs = 10000)
    {
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(
            MessageCorrelationKey.Build(messageName, correlationValue));
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (await correlationGrain.DeliverMessage(variables ?? new ExpandoObject()))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Broadcasts a signal to all its buckets, the way the engine does (default bucket count),
    /// and returns how many subscribers it reached. Fails the test if a bucket call failed.
    /// </summary>
    protected async Task<int> BroadcastSignal(string signalName)
    {
        var result = await Cluster.GrainFactory.BroadcastSignal(signalName, new SignalCorrelationOptions());
        if (result.Failure is not null)
            throw result.Failure;
        return result.DeliveredCount;
    }

    /// <summary>
    /// Broadcasts a signal once at least <paramref name="minSubscribers"/> subscriptions are
    /// registered (see <see cref="DeliverMessageWhenSubscribed"/>). A broadcast with zero
    /// subscribers is a no-op returning 0, so retrying is side-effect free. Note a broadcast
    /// that reaches fewer than <paramref name="minSubscribers"/> still consumes them.
    /// </summary>
    protected async Task<int> BroadcastSignalWhenSubscribed(
        string signalName, int minSubscribers = 1, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var delivered = 0;
        while (true)
        {
            delivered += await BroadcastSignal(signalName);
            if (delivered >= minSubscribers || DateTime.UtcNow >= deadline)
                return delivered;
            await Task.Delay(50);
        }
    }

    /// <summary>
    /// Creates a minimal workflow: start → task → end.
    /// Shared across test classes to avoid duplication.
    /// </summary>
    protected static IWorkflowDefinition CreateSimpleWorkflow(string workflowId = "simple-workflow")
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, task, end],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, task),
                new SequenceFlow("seq2", task, end)
            ]
        };
    }

    private class SimpleScriptExecutor : IScriptExpressionExecutor
    {
        public Task<ExpandoObject> Execute(string script, ExpandoObject variables, string scriptFormat)
        {
            if (script == "FAIL")
                throw new Exception("Simulated script failure");
            return Task.FromResult(variables);
        }
    }

    private class SimpleConditionEvaluator : IConditionExpressionEvaluator
    {
        public Task<bool> Evaluate(string expression, ExpandoObject variables)
        {
            if (expression == "THROW")
                throw new InvalidOperationException("Simulated condition failure");
            return Task.FromResult(string.Equals(expression, "true", StringComparison.OrdinalIgnoreCase));
        }
    }

    private class EfCoreSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder
                .Configure<Orleans.Configuration.SiloOptions>(o =>
                    o.SiloName = "combined-test-" + Guid.NewGuid().ToString("N"))
                .AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>()
                .AddPlacementDirector<ProjectorPlacementStrategy, ProjectorPlacementDirector>()
                .AddWorkerPlacementFilter()
                .AddMemoryStreams(WorkflowEventsPublisher.StreamProvider)
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryGrainStorage(GrainStorageNames.MessageCorrelations)
                .AddMemoryGrainStorage(GrainStorageNames.SignalCorrelations)
                .AddMemoryGrainStorage(GrainStorageNames.MessageStartEventListeners)
                .AddMemoryGrainStorage(GrainStorageNames.SignalStartEventListeners)
                .AddMemoryGrainStorage(GrainStorageNames.ConditionalStartEventListeners)
                .AddMemoryGrainStorage(GrainStorageNames.ConditionalStartEventRegistry)
                .AddMemoryGrainStorage(GrainStorageNames.CustomTaskCatalog)
                .AddCustomStorageBasedLogConsistencyProviderAsDefault()
                .AddGrainExtension<IWorkflowInstanceInspector, WorkflowInstanceInspector>()
                .AddIncomingGrainCallFilter<SignalDeliveryFaults>()
                .UseInMemoryReminderService()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ILoggerProvider, CapturedLogEvents.Provider>();
                    // Fleans' Debug entries reach only the capture, so tests can wait on them as sync points.
                    services.Configure<LoggerFilterOptions>(o =>
                        o.AddFilter<CapturedLogEvents.Provider>("Fleans", LogLevel.Debug));

                    services.AddDbContextFactory<FleanCommandDbContext>(options =>
                        options.UseFleansSqlite("DataSource=file::memory:?cache=shared"));

                    services.AddDbContextFactory<FleanQueryDbContext>(options =>
                        options.UseFleansSqlite("DataSource=file::memory:?cache=shared"));

                    services.AddDbContextFactory<FleanJournalDbContext>(options =>
                        options.UseFleansSqlite("DataSource=file::memory:?cache=shared"));

                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.TimerSchedulers,
                        (sp, _) => new EfCoreTimerSchedulerGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    // The production storage, so tests see that a user task registers before its
                    // instance's read-model row is projected (#863, #861): UserTasks must not
                    // depend on that row.
                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.UserTasks,
                        (sp, _) => new EfCoreUserTaskGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.ProcessDefinitions,
                        (sp, _) => new EfCoreProcessDefinitionGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.DeploymentRequests,
                        (sp, _) => new EfCoreDeploymentRequestGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    services.AddSingleton<IFleanQueryContextFactory, FleanQueryContextFactory>();
                    services.AddSingleton<IProcessDefinitionRepository, EfCoreProcessDefinitionRepository>();
                    services.AddSingleton<ISieveProcessor, ApplicationSieveProcessor>();
                    services.Configure<SieveOptions>(options =>
                    {
                        options.DefaultPageSize = 20;
                        options.MaxPageSize = 100;
                    });
                    services.AddSingleton<IUserTaskFilterStrategy, InMemoryUserTaskFilterStrategy>();
                    services.AddSingleton<IWorkflowQueryService, WorkflowQueryService>();
                    services.AddSingleton<IScriptExpressionExecutor, SimpleScriptExecutor>();
                    services.AddSingleton<IConditionExpressionEvaluator, SimpleConditionEvaluator>();
                    services.AddSingleton<EfCoreWorkflowStateProjection>();
                    services.AddSingleton<IWorkflowStateProjection>(sp =>
                        new FaultInjectingWorkflowStateProjection(sp.GetRequiredService<EfCoreWorkflowStateProjection>()));
                    // Small interval: most multi-step tests then reactivate from a snapshot plus a
                    // journal tail, not only from a full replay.
                    services.Configure<FleansPersistenceOptions>(o => o.SnapshotInterval = TestSnapshotInterval);
                    services.AddSingleton<EfCoreEventStore>();
                    services.AddSingleton<IEventStore>(sp =>
                        new FaultInjectingEventStore(sp.GetRequiredService<EfCoreEventStore>()));
                    services.AddApplication();
                    services.Configure<WorkflowVariablesOptions>(o => o.MaxSizeBytes = TestMaxVariablesSizeBytes);

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
                    // The journal shares the database, as by default (#879).
                    using var journal = sp.GetRequiredService<IDbContextFactory<FleanJournalDbContext>>().CreateDbContext();
                    SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(journal.Database);
                });
    }
}
