using System.Collections.Concurrent;
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

public abstract class UserTaskRegistrationFaultTestBase : WorkflowTestBase
{
    // Register, check write count and the persisted task, then claim + complete to prove the
    // recovered task is fully usable.
    protected async Task AssertRegistrationRecovers(string workflowId, int expectedWrites)
    {
        var start = new StartEvent("start");
        var task = new UserTask("approval", "alice", [], [], null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        await instance.StartWorkflow(workflow);

        var waiting = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(waiting);
        Assert.IsFalse(waiting.IsFailed);
        Assert.IsFalse(waiting.IsCompleted);
        var taskId = waiting.ActiveActivities.Single(a => a.ActivityId == "approval").ActivityInstanceId;
        Assert.AreEqual(expectedWrites, FaultyUserTaskStorage.WriteAttemptsFor(Cluster, taskId));
        var registered = await QueryService.GetUserTask(taskId);
        Assert.IsNotNull(registered, "The task must be persisted despite the injected failures.");
        Assert.AreEqual("alice", registered.Assignee);

        await instance.ClaimUserTask(taskId, "alice", []);
        await instance.CompleteUserTask(taskId, "alice", new ExpandoObject());

        var completed = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(completed);
        Assert.IsTrue(completed.IsCompleted);
        Assert.IsFalse(completed.IsFailed);
    }
}

[TestClass]
public class UserTaskRegistrationRetryTests : UserTaskRegistrationFaultTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<FailBeforeCommitConfigurator>();

    [TestMethod]
    public async Task StorageWriteFailsTwice_OrleansWrapsTheFailureAndRegistrationRecovers()
        => await AssertRegistrationRecovers("user-task-storage-retry", expectedWrites: 3);

    private sealed class FailBeforeCommitConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) => FaultyUserTaskStorage.Register(hostBuilder, commitBeforeThrow: false);
    }
}

[TestClass]
public class UserTaskRegistrationCommitThenThrowTests : UserTaskRegistrationFaultTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<CommitThenThrowConfigurator>();

    [TestMethod]
    public async Task FirstWriteCommitsThenThrows_RetryIsANoOpAndTaskSurvives()
        // Without the grain's post-failure re-read, the retry would see RecordExists=false and
        // a stale ETag, and every attempt would fail with an ETag mismatch. Only the first write
        // reaches storage: the retry hits the RecordExists guard.
        => await AssertRegistrationRecovers("user-task-commit-then-throw", expectedWrites: 1);

    private sealed class CommitThenThrowConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) => FaultyUserTaskStorage.Register(hostBuilder, commitBeforeThrow: true);
    }
}

/// <summary>
/// Fails the first two writes per task grain (failures thrown below IPersistentState, so Orleans
/// wraps them as it would a real provider failure). With <c>commitBeforeThrow</c> the first write
/// commits and then throws without updating the grain's ETag/RecordExists — a lost commit ack.
/// </summary>
internal sealed class FaultyUserTaskStorage(IGrainStorage inner, bool commitBeforeThrow) : IGrainStorage
{
    private readonly ConcurrentDictionary<Guid, int> _writes = new();

    public static void Register(ISiloBuilder hostBuilder, bool commitBeforeThrow)
        => hostBuilder.ConfigureServices(services =>
            services.AddKeyedSingleton<IGrainStorage>(GrainStorageNames.UserTasks,
                (sp, _) => new FaultyUserTaskStorage(new EfCoreUserTaskGrainStorage(
                    sp.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>()), commitBeforeThrow)));

    public static int WriteAttemptsFor(TestCluster cluster, Guid activityInstanceId)
        => cluster.Silos.Cast<InProcessSiloHandle>()
            .Select(silo => (FaultyUserTaskStorage)silo.SiloHost.Services
                .GetRequiredKeyedService<IGrainStorage>(GrainStorageNames.UserTasks))
            .Sum(storage => storage._writes.GetValueOrDefault(activityInstanceId));

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => inner.ReadStateAsync(stateName, grainId, grainState);

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        var attempt = _writes.AddOrUpdate(grainId.GetGuidKey(), 1, (_, n) => n + 1);
        if (attempt > 2 || (commitBeforeThrow && attempt > 1))
        {
            await inner.WriteStateAsync(stateName, grainId, grainState);
            return;
        }

        if (commitBeforeThrow)
        {
            var (etag, exists) = (grainState.ETag, grainState.RecordExists);
            await inner.WriteStateAsync(stateName, grainId, grainState);
            (grainState.ETag, grainState.RecordExists) = (etag, exists);
        }
        throw new InvalidOperationException("injected storage write failure");
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => inner.ClearStateAsync(stateName, grainId, grainState);
}
