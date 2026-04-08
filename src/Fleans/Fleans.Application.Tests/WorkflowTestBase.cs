using Fleans.Application;
using Fleans.Application.Events;
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
using Microsoft.Data.Sqlite;
using Fleans.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sieve.Models;
using Sieve.Services;
using Orleans.EventSourcing.CustomStorage;
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
        Cluster = builder.Build();
        Cluster.Deploy();

        QueryService = ((InProcessSiloHandle)Cluster.Primary).SiloHost.Services.GetRequiredService<IWorkflowQueryService>();
    }

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

    protected async Task ForceAllGrainDeactivation()
    {
        var managementGrain = Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
        await managementGrain.ForceActivationCollection(TimeSpan.Zero);
        await Task.Delay(TimeSpan.FromSeconds(3));
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
            return Task.FromResult(string.Equals(expression, "true", StringComparison.OrdinalIgnoreCase));
        }
    }

    private class EfCoreSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder
                .AddMemoryStreams(WorkflowEventsPublisher.StreamProvider)
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryGrainStorage(GrainStorageNames.MessageCorrelations)
                .AddMemoryGrainStorage(GrainStorageNames.SignalCorrelations)
                .AddMemoryGrainStorage(GrainStorageNames.MessageStartEventListeners)
                .AddMemoryGrainStorage(GrainStorageNames.SignalStartEventListeners)
                .AddMemoryGrainStorage(GrainStorageNames.UserTasks)
                .AddCustomStorageBasedLogConsistencyProviderAsDefault()
                .UseInMemoryReminderService()
                .ConfigureServices(services =>
                {
                    services.AddDbContextFactory<FleanCommandDbContext>(options =>
                        options.UseFleansSqlite("DataSource=file::memory:?cache=shared"));

                    services.AddDbContextFactory<FleanQueryDbContext>(options =>
                        options.UseFleansSqlite("DataSource=file::memory:?cache=shared"));

                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.TimerSchedulers,
                        (sp, _) => new EfCoreTimerSchedulerGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.ProcessDefinitions,
                        (sp, _) => new EfCoreProcessDefinitionGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    services.AddSingleton<IProcessDefinitionRepository, EfCoreProcessDefinitionRepository>();
                    services.AddSingleton<ISieveProcessor, ApplicationSieveProcessor>();
                    services.Configure<SieveOptions>(options =>
                    {
                        options.DefaultPageSize = 20;
                        options.MaxPageSize = 100;
                    });
                    services.AddSingleton<IWorkflowQueryService, WorkflowQueryService>();
                    services.AddSingleton<IScriptExpressionExecutor, SimpleScriptExecutor>();
                    services.AddSingleton<IConditionExpressionEvaluator, SimpleConditionEvaluator>();
                    services.AddSingleton<IWorkflowStateProjection, EfCoreWorkflowStateProjection>();
                    services.AddSingleton<EfCoreEventStore>();
                    services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<EfCoreEventStore>());
                    services.AddApplication();

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
