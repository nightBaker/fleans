using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Orleans.Runtime;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreUserTaskGrainStorageTests : PersistenceTestBase
{
    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_BeforeTheInstanceRowIsProjected_Succeeds(PersistenceProvider provider)
    {
        // Arrange — a user task registers in the turn that starts its instance, while the
        // read-model row is still on its way from the asynchronous projector (#861). The task
        // table must not depend on the read model.
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreUserTaskGrainStorage(fixture.CommandFactory);
        var taskId = Guid.NewGuid();
        var grainId = GrainId.Create("usertask", taskId.ToString());
        var write = new TestGrainState<UserTaskState>
        {
            State = new UserTaskState
            {
                WorkflowInstanceId = Guid.NewGuid(), // no WorkflowInstances row
                ActivityId = "approve",
                TaskState = UserTaskLifecycleState.Created,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        // Act
        await storage.WriteStateAsync("state", grainId, write);

        // Assert
        var read = new TestGrainState<UserTaskState> { State = new UserTaskState() };
        await storage.ReadStateAsync("state", grainId, read);
        Assert.IsTrue(read.RecordExists);
        Assert.AreEqual("approve", read.State.ActivityId);
        Assert.AreEqual(write.State.WorkflowInstanceId, read.State.WorkflowInstanceId);
    }
}
