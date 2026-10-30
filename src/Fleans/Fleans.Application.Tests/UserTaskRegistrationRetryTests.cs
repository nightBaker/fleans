using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using Orleans.TestingHost.InProcess;

namespace Fleans.Application.Tests;

[TestClass]
public class UserTaskRegistrationRetryTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<RetryStorageSiloConfigurator>();

    [TestMethod]
    public async Task StorageWriteFailsTwice_OrleansWrapsTheFailureAndRegistrationRecovers()
    {
        var start = new StartEvent("start");
        var task = new UserTask("approval", "alice", [], [], null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "user-task-storage-retry",
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        await instance.StartWorkflow(workflow);

        var writeAttempts = Cluster.Silos.Cast<InProcessSiloHandle>()
            .Select(silo => (FailFirstTwoWritesStorage)silo.SiloHost.Services
                .GetRequiredKeyedService<IGrainStorage>(GrainStorageNames.UserTasks))
            .Sum(storage => storage.WriteAttempts);
        Assert.AreEqual(3, writeAttempts, "Two failed storage writes must be followed by a successful retry.");

        var waiting = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(waiting);
        Assert.IsFalse(waiting.IsFailed);
        Assert.IsFalse(waiting.IsCompleted);
        var taskId = waiting.ActiveActivities.Single(a => a.ActivityId == "approval").ActivityInstanceId;
        var registered = await QueryService.GetUserTask(taskId);
        Assert.IsNotNull(registered, "The third write must persist the task despite the first two failures.");
        Assert.AreEqual("alice", registered.Assignee);

        await instance.ClaimUserTask(taskId, "alice", []);
        await instance.CompleteUserTask(taskId, "alice", new ExpandoObject());

        var completed = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(completed);
        Assert.IsTrue(completed.IsCompleted);
        Assert.IsFalse(completed.IsFailed);
    }

    private sealed class RetryStorageSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
            => hostBuilder.ConfigureServices(services =>
                services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.UserTasks,
                    (sp, _) => new FailFirstTwoWritesStorage(new EfCoreUserTaskGrainStorage(
                        sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()))));
    }

    private sealed class FailFirstTwoWritesStorage(IGrainStorage inner) : IGrainStorage
    {
        private int _writes;
        public int WriteAttempts => Volatile.Read(ref _writes);

        public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => inner.ReadStateAsync(stateName, grainId, grainState);

        public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            // Throw below IPersistentState, rather than mocking the grain call: Orleans
            // must wrap this non-Orleans exception for CallWithRetry to handle it.
            if (Interlocked.Increment(ref _writes) <= 2)
                throw new InvalidOperationException("injected storage write failure");
            return inner.WriteStateAsync(stateName, grainId, grainState);
        }

        public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
            => inner.ClearStateAsync(stateName, grainId, grainState);
    }
}
