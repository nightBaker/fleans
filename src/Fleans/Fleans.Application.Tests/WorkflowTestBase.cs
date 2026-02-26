using Fleans.Application;
using Fleans.Application.Events;
using Fleans.Application.QueryModels;
using Fleans.Application.Services;
using Fleans.Domain;
using Fleans.Domain.Persistence;
using Fleans.Infrastructure;
using Fleans.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    protected async Task<InstanceStateSnapshot?> PollForCompletion(
        Guid instanceId, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await QueryService.GetStateSnapshot(instanceId);
            if (snapshot is not null && snapshot.IsCompleted)
                return snapshot;
            await Task.Delay(100);
        }

        return await QueryService.GetStateSnapshot(instanceId);
    }

    private class EfCoreSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder
                .AddMemoryStreams(WorkflowEventsPublisher.StreamProvider)
                .AddMemoryGrainStorage("PubSubStore")
                .AddMemoryGrainStorage(GrainStorageNames.MessageCorrelations)
                .AddMemoryGrainStorage(GrainStorageNames.SignalCorrelations)
                .UseInMemoryReminderService()
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

                    services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.TimerSchedulers,
                        (sp, _) => new EfCoreTimerSchedulerGrainStorage(
                            sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()));

                    services.AddSingleton<IProcessDefinitionRepository, EfCoreProcessDefinitionRepository>();
                    services.AddSingleton<IWorkflowQueryService, WorkflowQueryService>();
                    services.AddTransient<IBoundaryEventHandler, BoundaryEventHandler>();
                    services.AddInfrastructure();

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
