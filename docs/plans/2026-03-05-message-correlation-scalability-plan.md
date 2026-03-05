# Message Correlation Scalability & Reliability Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Partition MessageCorrelationGrain by correlation key and switch delivery to confirm-then-remove for at-least-once semantics.

**Architecture:** Change grain key from `messageName` to `messageName/encodedCorrelationKey`. Each grain holds at most one subscription. Deliver message first, clear state only on success. Add idempotency guard to HandleMessageDelivery.

**Tech Stack:** Orleans 10.0.1, EF Core 10.0.1 (SQLite), MSTest + Orleans.TestingHost

---

### Task 1: Add MessageCorrelationKey helper

**Files:**
- Create: `src/Fleans/Fleans.Application/Grains/MessageCorrelationKey.cs`
- Test: `src/Fleans/Fleans.Application.Tests/MessageCorrelationKeyTests.cs`

**Step 1: Write the failing test**

Create `src/Fleans/Fleans.Application.Tests/MessageCorrelationKeyTests.cs`:

```csharp
using Fleans.Application.Grains;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageCorrelationKeyTests
{
    [TestMethod]
    public void Build_SimpleKey_ReturnsCompositeKey()
    {
        var result = MessageCorrelationKey.Build("paymentReceived", "order-123");
        Assert.AreEqual("paymentReceived/order-123", result);
    }

    [TestMethod]
    public void Build_KeyWithSlash_EncodesSlash()
    {
        var result = MessageCorrelationKey.Build("msg", "region/order-123");
        Assert.AreEqual("msg/region%2Forder-123", result);
    }

    [TestMethod]
    public void Build_KeyWithSpecialChars_EncodesCorrectly()
    {
        var result = MessageCorrelationKey.Build("msg", "key?a=1&b=2");
        Assert.AreEqual("msg/key%3Fa%3D1%26b%3D2", result);
    }

    [TestMethod]
    public void Build_UnicodeKey_EncodesCorrectly()
    {
        var result = MessageCorrelationKey.Build("msg", "заказ-123");
        // Uri.EscapeDataString encodes unicode
        StringAssert.StartsWith(result, "msg/");
        Assert.AreNotEqual("msg/заказ-123", result);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Fleans/Fleans.Application.Tests --filter "FullyQualifiedName~MessageCorrelationKeyTests" --no-restore`
Expected: FAIL — `MessageCorrelationKey` does not exist.

**Step 3: Write minimal implementation**

Create `src/Fleans/Fleans.Application/Grains/MessageCorrelationKey.cs`:

```csharp
namespace Fleans.Application.Grains;

public static class MessageCorrelationKey
{
    public static string Build(string messageName, string correlationKey)
        => $"{messageName}/{Uri.EscapeDataString(correlationKey)}";
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test src/Fleans/Fleans.Application.Tests --filter "FullyQualifiedName~MessageCorrelationKeyTests" --no-restore`
Expected: PASS (all 4 tests)

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/MessageCorrelationKey.cs src/Fleans/Fleans.Application.Tests/MessageCorrelationKeyTests.cs
git commit -m "feat: add MessageCorrelationKey helper for composite grain keys"
```

---

### Task 2: Change MessageCorrelationState to single subscription

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/MessageCorrelationState.cs`

**Step 1: Write the failing test**

No new test needed — existing persistence tests will break once we change the state model. That's expected; we'll fix them in Task 5.

**Step 2: Change the state model**

Replace the full content of `src/Fleans/Fleans.Domain/States/MessageCorrelationState.cs`:

```csharp
namespace Fleans.Domain.States;

[GenerateSerializer]
public class MessageCorrelationState
{
    [Id(0)] public string Key { get; set; } = string.Empty;
    [Id(1)] public string? ETag { get; set; }
    [Id(2)] public MessageSubscription? Subscription { get; set; }
}

[GenerateSerializer]
public record MessageSubscription(
    [property: Id(0)] Guid WorkflowInstanceId,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] Guid HostActivityInstanceId,
    [property: Id(3)] string CorrelationKey)
{
    [Id(4)] public string MessageName { get; init; } = string.Empty;
}
```

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/MessageCorrelationState.cs
git commit -m "refactor: change MessageCorrelationState from List to single Subscription"
```

Note: The project will not compile at this point. Tasks 3-5 fix all callers.

---

### Task 3: Update IMessageCorrelationGrain interface and grain implementation

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/IMessageCorrelationGrain.cs`
- Modify: `src/Fleans/Fleans.Application/Grains/MessageCorrelationGrain.cs`

**Step 1: Update the interface**

Replace `src/Fleans/Fleans.Application/Grains/IMessageCorrelationGrain.cs`:

```csharp
using System.Dynamic;

namespace Fleans.Application.Grains;

public interface IMessageCorrelationGrain : IGrainWithStringKey
{
    ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId);
    ValueTask Unsubscribe();
    ValueTask<bool> DeliverMessage(ExpandoObject variables);
}
```

**Step 2: Update the grain implementation**

Replace `src/Fleans/Fleans.Application/Grains/MessageCorrelationGrain.cs`:

```csharp
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using System.Dynamic;

namespace Fleans.Application.Grains;

public partial class MessageCorrelationGrain : Grain, IMessageCorrelationGrain
{
    private readonly IPersistentState<MessageCorrelationState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<MessageCorrelationGrain> _logger;

    public MessageCorrelationGrain(
        [PersistentState("state", GrainStorageNames.MessageCorrelations)]
        IPersistentState<MessageCorrelationState> state,
        IGrainFactory grainFactory,
        ILogger<MessageCorrelationGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async ValueTask Subscribe(Guid workflowInstanceId, string activityId, Guid hostActivityInstanceId)
    {
        var grainKey = this.GetPrimaryKeyString();

        if (_state.State.Subscription is not null)
            throw new InvalidOperationException(
                $"Duplicate subscription: grain '{grainKey}' already has a subscriber.");

        _state.State.Subscription = new MessageSubscription(workflowInstanceId, activityId, hostActivityInstanceId, grainKey)
            { MessageName = grainKey };
        await _state.WriteStateAsync();
        LogSubscribed(grainKey, workflowInstanceId, activityId);
    }

    public async ValueTask Unsubscribe()
    {
        var grainKey = this.GetPrimaryKeyString();

        if (_state.State.Subscription is not null)
        {
            _state.State.Subscription = null;
            await _state.ClearStateAsync();
            LogUnsubscribed(grainKey);
        }
    }

    public async ValueTask<bool> DeliverMessage(ExpandoObject variables)
    {
        var grainKey = this.GetPrimaryKeyString();

        if (_state.State.Subscription is null)
        {
            LogDeliveryNoMatch(grainKey);
            return false;
        }

        var subscription = _state.State.Subscription;
        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceGrain>(subscription.WorkflowInstanceId);
        LogDelivery(grainKey, subscription.WorkflowInstanceId, subscription.ActivityId);

        // Deliver first, then clear — confirm-then-remove for at-least-once
        await workflowInstance.HandleMessageDelivery(subscription.ActivityId, subscription.HostActivityInstanceId, variables);

        _state.State.Subscription = null;
        await _state.ClearStateAsync();

        return true;
    }

    [LoggerMessage(EventId = 9000, Level = LogLevel.Information,
        Message = "Message correlation '{GrainKey}' subscription registered: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogSubscribed(string grainKey, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information,
        Message = "Message correlation '{GrainKey}' subscription removed")]
    private partial void LogUnsubscribed(string grainKey);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information,
        Message = "Message correlation '{GrainKey}' delivered: workflowInstanceId={WorkflowInstanceId}, activityId={ActivityId}")]
    private partial void LogDelivery(string grainKey, Guid workflowInstanceId, string activityId);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Debug,
        Message = "Message correlation '{GrainKey}' delivery failed: no active subscription")]
    private partial void LogDeliveryNoMatch(string grainKey);
}
```

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/IMessageCorrelationGrain.cs src/Fleans/Fleans.Application/Grains/MessageCorrelationGrain.cs
git commit -m "refactor: update MessageCorrelationGrain to single-subscription with confirm-then-remove"
```

---

### Task 4: Update all callers to use partitioned grain key

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.EventHandling.cs` (lines 129-199)
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.ActivityLifecycle.cs` (lines 123-135)
- Modify: `src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs` (lines 178-193)
- Modify: `src/Fleans/Fleans.Api/Controllers/WorkflowController.cs` (lines 65-68)

**Step 1: Update WorkflowInstance.EventHandling.cs — RegisterMessageSubscription**

In `RegisterMessageSubscription` (line 148), change:
```csharp
// OLD (line 148):
var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);
// OLD (line 152):
await correlationGrain.Subscribe(correlationKey, this.GetPrimaryKey(), activityId, entry.ActivityInstanceId);
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build(messageDef.Name, correlationKey);
var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
// ...
await correlationGrain.Subscribe(this.GetPrimaryKey(), activityId, entry.ActivityInstanceId);
```

**Step 2: Update WorkflowInstance.EventHandling.cs — RegisterBoundaryMessageSubscription**

In `RegisterBoundaryMessageSubscription` (line 186), change:
```csharp
// OLD (line 186):
var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);
// OLD (line 190):
await correlationGrain.Subscribe(correlationKey, this.GetPrimaryKey(), boundaryActivityId, hostActivityInstanceId);
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build(messageDef.Name, correlationKey);
var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
// ...
await correlationGrain.Subscribe(this.GetPrimaryKey(), boundaryActivityId, hostActivityInstanceId);
```

**Step 3: Update WorkflowInstance.ActivityLifecycle.cs — CancelEventBasedGatewaySiblings**

In the `MessageIntermediateCatchEvent` case (lines 131-132), change:
```csharp
// OLD:
var corrGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(msgDef.Name);
await corrGrain.Unsubscribe(corrValue.ToString()!);
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build(msgDef.Name, corrValue.ToString()!);
var corrGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
await corrGrain.Unsubscribe();
```

**Step 4: Update BoundaryEventHandler.cs — UnsubscribeBoundaryMessageSubscriptionsAsync**

In `UnsubscribeBoundaryMessageSubscriptionsAsync` (lines 190-191), change:
```csharp
// OLD:
var correlationGrain = _accessor.GrainFactory.GetGrain<IMessageCorrelationGrain>(messageDef.Name);
await correlationGrain.Unsubscribe(correlationValue.ToString()!);
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build(messageDef.Name, correlationValue.ToString()!);
var correlationGrain = _accessor.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
await correlationGrain.Unsubscribe();
```

**Step 5: Update WorkflowController.cs — SendMessage endpoint**

In `SendMessage` (lines 65-68), change:
```csharp
// OLD:
var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(request.MessageName);
var delivered = await correlationGrain.DeliverMessage(
    request.CorrelationKey,
    variables);
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build(request.MessageName, request.CorrelationKey);
var correlationGrain = _grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
var delivered = await correlationGrain.DeliverMessage(variables);
```

Add `using Fleans.Application.Grains;` at the top of the file if not already present.

**Step 6: Verify the project compiles**

Run: `dotnet build src/Fleans/`
Expected: Build succeeds (no compilation errors).

**Step 7: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.EventHandling.cs src/Fleans/Fleans.Application/Grains/WorkflowInstance.ActivityLifecycle.cs src/Fleans/Fleans.Application/Services/BoundaryEventHandler.cs src/Fleans/Fleans.Api/Controllers/WorkflowController.cs
git commit -m "refactor: update all message correlation callers to use partitioned grain key"
```

---

### Task 5: Update EfCoreMessageCorrelationGrainStorage for single subscription

**Files:**
- Modify: `src/Fleans/Fleans.Persistence/EfCoreMessageCorrelationGrainStorage.cs`
- Modify: `src/Fleans/Fleans.Persistence/FleanCommandDbContext.cs`
- Modify: `src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs`

**Step 1: Update FleanCommandDbContext**

Keep `DbSet<MessageSubscription> MessageSubscriptions` (needed for queryability).
No change needed to DbSets.

**Step 2: Update FleanModelConfiguration**

In the `MessageCorrelationState` configuration (lines 130-142), change the relationship:
```csharp
// OLD:
entity.HasMany(e => e.Subscriptions)
    .WithOne()
    .HasForeignKey(s => s.MessageName)
    .OnDelete(DeleteBehavior.Cascade);
```
To:
```csharp
entity.HasOne(e => e.Subscription)
    .WithOne()
    .HasForeignKey<MessageSubscription>(s => s.MessageName)
    .OnDelete(DeleteBehavior.Cascade);
```

In the `MessageSubscription` configuration (lines 144-151), change the primary key:
```csharp
// OLD:
sub.HasKey(s => new { s.MessageName, s.CorrelationKey });
```
To:
```csharp
sub.HasKey(s => s.MessageName);
```

Note: The composite key is no longer needed since each grain has at most one subscription. MessageName (which is actually the grain key `messageName/correlationKey`) is sufficient as PK. Increase MessageName max length to 1024 to accommodate the composite grain key:
```csharp
sub.Property(s => s.MessageName).HasMaxLength(1024);
```

Also increase the Key max length for MessageCorrelationState:
```csharp
entity.Property(e => e.Key).HasMaxLength(1024);
```

**Step 3: Simplify EfCoreMessageCorrelationGrainStorage**

Replace `src/Fleans/Fleans.Persistence/EfCoreMessageCorrelationGrainStorage.cs`:

```csharp
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence;

public class EfCoreMessageCorrelationGrainStorage : IGrainStorage
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;

    public EfCoreMessageCorrelationGrainStorage(IDbContextFactory<FleanCommandDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();

        var state = await db.MessageCorrelations
            .Include(e => e.Subscription)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == id);

        if (state is not null)
        {
            grainState.State = (T)(object)state;
            grainState.ETag = state.ETag;
            grainState.RecordExists = true;
        }
    }

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var state = (MessageCorrelationState)(object)grainState.State!;
        var newETag = Guid.NewGuid().ToString("N");

        var existing = await db.MessageCorrelations
            .Include(e => e.Subscription)
            .FirstOrDefaultAsync(e => e.Key == id);

        if (existing is null)
        {
            state.Key = id;
            state.ETag = newETag;
            db.MessageCorrelations.Add(state);
            if (state.Subscription is not null)
                db.Entry(state.Subscription).Property(s => s.MessageName).CurrentValue = id;
        }
        else
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.Entry(existing).CurrentValues.SetValues(state);
            db.Entry(existing).Property(s => s.Key).IsModified = false;
            db.Entry(existing).Property(s => s.ETag).CurrentValue = newETag;

            // Handle subscription diff
            if (existing.Subscription is not null && state.Subscription is null)
            {
                db.MessageSubscriptions.Remove(existing.Subscription);
            }
            else if (existing.Subscription is null && state.Subscription is not null)
            {
                db.MessageSubscriptions.Add(state.Subscription);
                db.Entry(state.Subscription).Property(s => s.MessageName).CurrentValue = id;
            }
            else if (existing.Subscription is not null && state.Subscription is not null)
            {
                db.Entry(existing.Subscription).CurrentValues.SetValues(state.Subscription);
                db.Entry(existing.Subscription).Property(s => s.MessageName).IsModified = false;
            }
        }

        await db.SaveChangesAsync();

        grainState.ETag = newETag;
        grainState.RecordExists = true;
    }

    public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var id = grainId.Key.ToString();
        var existing = await db.MessageCorrelations.FindAsync(id);

        if (existing is not null)
        {
            if (existing.ETag != grainState.ETag)
                throw new InconsistentStateException(
                    $"ETag mismatch on clear: expected '{grainState.ETag}', stored '{existing.ETag}'");

            db.MessageCorrelations.Remove(existing);
            await db.SaveChangesAsync();
        }

        grainState.ETag = null;
        grainState.RecordExists = false;
    }
}
```

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Persistence/EfCoreMessageCorrelationGrainStorage.cs src/Fleans/Fleans.Persistence/FleanModelConfiguration.cs
git commit -m "refactor: simplify persistence for single-subscription MessageCorrelation"
```

---

### Task 6: Update persistence tests

**Files:**
- Modify: `src/Fleans/Fleans.Persistence.Tests/EfCoreMessageCorrelationGrainStorageTests.cs`

**Step 1: Rewrite tests for single-subscription model**

Replace the full content of `src/Fleans/Fleans.Persistence.Tests/EfCoreMessageCorrelationGrainStorageTests.cs`:

```csharp
using Fleans.Domain.States;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EfCoreMessageCorrelationGrainStorageTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreMessageCorrelationGrainStorage _storage = null!;
    private const string StateName = "state";

    [TestInitialize]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);
        _storage = new EfCoreMessageCorrelationGrainStorage(_dbContextFactory);

        using var db = _dbContextFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _connection.Dispose();
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_ReturnsStoredState()
    {
        var grainId = NewGrainId("paymentReceived/order-123");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "waitPayment", Guid.NewGuid(), "order-123"));

        await _storage.WriteStateAsync(StateName, grainId, state);
        Assert.IsNotNull(state.ETag);
        Assert.IsTrue(state.RecordExists);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.Subscription);
        Assert.AreEqual("order-123", readState.State.Subscription.CorrelationKey);
        Assert.AreEqual("waitPayment", readState.State.Subscription.ActivityId);
        Assert.AreEqual(state.ETag, readState.ETag);
        Assert.IsTrue(readState.RecordExists);
    }

    [TestMethod]
    public async Task WriteAndRead_RoundTrip_PreservesSubscriptionDetails()
    {
        var instanceId = Guid.NewGuid();
        var hostActivityInstanceId = Guid.NewGuid();
        var grainId = NewGrainId("orderCancelled/corr-key-1");
        var state = CreateGrainState(
            new MessageSubscription(instanceId, "activity-1", hostActivityInstanceId, "corr-key-1"));

        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        var sub = readState.State.Subscription!;
        Assert.AreEqual(instanceId, sub.WorkflowInstanceId);
        Assert.AreEqual("activity-1", sub.ActivityId);
        Assert.AreEqual(hostActivityInstanceId, sub.HostActivityInstanceId);
        Assert.AreEqual("orderCancelled/corr-key-1", sub.MessageName);
    }

    [TestMethod]
    public async Task Write_WithCorrectETag_Succeeds()
    {
        var grainId = NewGrainId("msg1/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await _storage.WriteStateAsync(StateName, grainId, state);
        var firstETag = state.ETag;

        // Update the subscription
        state.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act2", Guid.NewGuid(), "key1");
        await _storage.WriteStateAsync(StateName, grainId, state);

        Assert.AreNotEqual(firstETag, state.ETag);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.AreEqual("act2", readState.State.Subscription!.ActivityId);
    }

    [TestMethod]
    public async Task Write_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId("staleMsg/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act2", Guid.NewGuid(), "key1");
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        state.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act3", Guid.NewGuid(), "key1");
        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.WriteStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Write_RemoveSubscription_Succeeds()
    {
        var grainId = NewGrainId("diffRemove/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        state.State.Subscription = null;
        await _storage.WriteStateAsync(StateName, grainId, state);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsNull(readState.State.Subscription);
    }

    [TestMethod]
    public async Task Clear_RemovesState_SubsequentReadReturnsDefault()
    {
        var grainId = NewGrainId("clearMsg/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);
        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);
        Assert.IsNull(readState.ETag);
        Assert.IsFalse(readState.RecordExists);
    }

    [TestMethod]
    public async Task Clear_WithStaleETag_ThrowsInconsistentStateException()
    {
        var grainId = NewGrainId("clearStale/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        var concurrentState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, concurrentState);
        concurrentState.State.Subscription = new MessageSubscription(Guid.NewGuid(), "act2", Guid.NewGuid(), "key1");
        await _storage.WriteStateAsync(StateName, grainId, concurrentState);

        await Assert.ThrowsExactlyAsync<InconsistentStateException>(
            () => _storage.ClearStateAsync(StateName, grainId, state));
    }

    [TestMethod]
    public async Task Clear_NonExistentGrain_IsNoOp()
    {
        var grainId = NewGrainId("noop/key1");
        var state = CreateEmptyGrainState();

        await _storage.ClearStateAsync(StateName, grainId, state);

        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [TestMethod]
    public async Task Read_NonExistentKey_LeavesStateUnchanged()
    {
        var grainId = NewGrainId("missing/key1");
        var state = CreateEmptyGrainState();

        await _storage.ReadStateAsync(StateName, grainId, state);

        Assert.IsNull(state.ETag);
        Assert.IsFalse(state.RecordExists);
    }

    [TestMethod]
    public async Task DifferentGrainIds_AreIsolated()
    {
        var grainId1 = NewGrainId("msgA/keyA");
        var grainId2 = NewGrainId("msgB/keyB");

        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        var state1 = CreateGrainState(
            new MessageSubscription(id1, "actA", Guid.NewGuid(), "keyA"));
        var state2 = CreateGrainState(
            new MessageSubscription(id2, "actB", Guid.NewGuid(), "keyB"));

        await _storage.WriteStateAsync(StateName, grainId1, state1);
        await _storage.WriteStateAsync(StateName, grainId2, state2);

        var read1 = CreateEmptyGrainState();
        var read2 = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId1, read1);
        await _storage.ReadStateAsync(StateName, grainId2, read2);

        Assert.IsNotNull(read1.State.Subscription);
        Assert.AreEqual(id1, read1.State.Subscription.WorkflowInstanceId);
        Assert.IsNotNull(read2.State.Subscription);
        Assert.AreEqual(id2, read2.State.Subscription.WorkflowInstanceId);
    }

    [TestMethod]
    public async Task WriteClearWrite_ReCreatesSameGrainId()
    {
        var grainId = NewGrainId("recreate/key1");
        var state = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act1", Guid.NewGuid(), "key1"));
        await _storage.WriteStateAsync(StateName, grainId, state);

        await _storage.ClearStateAsync(StateName, grainId, state);

        var newState = CreateGrainState(
            new MessageSubscription(Guid.NewGuid(), "act2", Guid.NewGuid(), "key2"));
        await _storage.WriteStateAsync(StateName, grainId, newState);

        var readState = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, readState);

        Assert.IsNotNull(readState.State.Subscription);
        Assert.AreEqual("key2", readState.State.Subscription.CorrelationKey);
        Assert.IsTrue(readState.RecordExists);
    }

    private static GrainId NewGrainId(string key)
        => GrainId.Create("messagecorrelation", key);

    private static TestGrainState<MessageCorrelationState> CreateGrainState(
        MessageSubscription subscription)
    {
        var state = new TestGrainState<MessageCorrelationState>
        {
            State = new MessageCorrelationState { Subscription = subscription }
        };
        return state;
    }

    private static TestGrainState<MessageCorrelationState> CreateEmptyGrainState()
        => new()
        {
            State = new MessageCorrelationState()
        };
}
```

**Step 2: Run persistence tests**

Run: `dotnet test src/Fleans/Fleans.Persistence.Tests --no-restore`
Expected: All tests pass.

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Persistence.Tests/EfCoreMessageCorrelationGrainStorageTests.cs
git commit -m "test: update persistence tests for single-subscription MessageCorrelation"
```

---

### Task 7: Add idempotency guard to HandleMessageDelivery

**Files:**
- Modify: `src/Fleans/Fleans.Application/Grains/WorkflowInstance.EventHandling.cs` (lines 51-74)

**Step 1: Add idempotency guard**

In `HandleMessageDelivery` (line 51), add an early return if the activity is no longer active. The change is to add a guard after `EnsureWorkflowDefinitionAsync()`:

```csharp
public async Task HandleMessageDelivery(string activityId, Guid hostActivityInstanceId, ExpandoObject variables)
{
    await EnsureWorkflowDefinitionAsync();
    SetWorkflowRequestContext();
    using var scope = BeginWorkflowScope();

    // Idempotency guard: if activity already completed/failed, silently ignore
    var activeEntry = State.Entries.FirstOrDefault(e =>
        e.ActivityInstanceId == hostActivityInstanceId && !e.IsCompleted);
    if (activeEntry is null)
    {
        LogStaleMessageDeliveryIgnored(activityId, hostActivityInstanceId);
        return;
    }

    var definition = await GetWorkflowDefinition();
    var scopeDef = definition.GetScopeForActivity(activityId);
    var activity = scopeDef.GetActivity(activityId);

    if (activity is MessageBoundaryEvent boundaryMessage)
    {
        LogMessageDeliveryBoundary(activityId);
        await _boundaryHandler.HandleBoundaryMessageFiredAsync(boundaryMessage, hostActivityInstanceId, definition);
    }
    else
    {
        LogMessageDeliveryComplete(activityId);
        await CompleteActivityState(activityId, variables);
        await ExecuteWorkflow();
    }

    await _state.WriteStateAsync();
}
```

**Step 2: Add the log method declaration**

In `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs`, add a new log method. Check the existing EventId range (1000-1099 for WorkflowInstance) and use the next available ID:

```csharp
[LoggerMessage(EventId = 10XX, Level = LogLevel.Warning,
    Message = "Stale message delivery ignored for activityId='{ActivityId}', hostActivityInstanceId={HostActivityInstanceId} — activity already completed")]
private partial void LogStaleMessageDeliveryIgnored(string activityId, Guid hostActivityInstanceId);
```

Note: Check the next available EventId in the 1000-1099 range in `WorkflowInstance.Logging.cs` before using `10XX`. Use the next sequential ID.

**Step 3: Run all tests**

Run: `dotnet test src/Fleans/ --no-restore`
Expected: All tests pass.

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application/Grains/WorkflowInstance.EventHandling.cs src/Fleans/Fleans.Application/Grains/WorkflowInstance.Logging.cs
git commit -m "fix: add idempotency guard to HandleMessageDelivery for at-least-once safety"
```

---

### Task 8: Update integration tests for new grain key

**Files:**
- Modify: `src/Fleans/Fleans.Application.Tests/MessageIntermediateCatchEventTests.cs`
- Modify: `src/Fleans/Fleans.Application.Tests/MessageBoundaryEventTests.cs`

**Step 1: Update MessageIntermediateCatchEventTests**

In `MessageCatch_ShouldSuspendWorkflow_UntilMessageDelivered` (line 52), change:
```csharp
// OLD:
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("paymentReceived");
dynamic msgVars = new ExpandoObject();
msgVars.paymentStatus = "confirmed";
var delivered = await correlationGrain.DeliverMessage("order-123", (ExpandoObject)msgVars);
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build("paymentReceived", "order-123");
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
dynamic msgVars = new ExpandoObject();
msgVars.paymentStatus = "confirmed";
var delivered = await correlationGrain.DeliverMessage((ExpandoObject)msgVars);
```

In `MessageCatch_WrongCorrelationKey_ShouldNotDeliver` (line 155), change:
```csharp
// OLD:
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("paymentReceived");
var delivered = await correlationGrain.DeliverMessage("order-999", new ExpandoObject());
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build("paymentReceived", "order-999");
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
var delivered = await correlationGrain.DeliverMessage(new ExpandoObject());
```

The `DuplicateCorrelationKey` test (line 64) does not directly call the correlation grain — it tests via `CompleteActivity` which triggers the grain internally. It should continue to work unchanged since the `Subscribe` call inside `RegisterMessageSubscription` now uses `MessageCorrelationKey.Build`. However, the duplicate error message text will change from including the correlation key to using the grain key. Update the assertion:
```csharp
// OLD:
StringAssert.Contains(failedActivity.ErrorState.Message, "Duplicate subscription");
// This assertion is still correct — the error message still contains "Duplicate subscription"
```

**Step 2: Update MessageBoundaryEventTests**

In `BoundaryMessage_MessageArrivesFirst_ShouldFollowBoundaryFlow` (line 52), change:
```csharp
// OLD:
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("cancelOrder");
var delivered = await correlationGrain.DeliverMessage("order-456", new ExpandoObject());
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build("cancelOrder", "order-456");
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
var delivered = await correlationGrain.DeliverMessage(new ExpandoObject());
```

In `BoundaryMessage_TaskCompletesFirst_ShouldFollowNormalFlow` (lines 119-120), change:
```csharp
// OLD:
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("cancelOrder");
var delivered = await correlationGrain.DeliverMessage("order-789", new ExpandoObject());
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build("cancelOrder", "order-789");
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
var delivered = await correlationGrain.DeliverMessage(new ExpandoObject());
```

In `NonInterruptingBoundaryMessage_AttachedActivityContinues` (lines 164-165), change:
```csharp
// OLD:
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("cancelOrder");
var delivered = await correlationGrain.DeliverMessage("order-ni-msg", new ExpandoObject());
```
To:
```csharp
var grainKey = MessageCorrelationKey.Build("cancelOrder", "order-ni-msg");
var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(grainKey);
var delivered = await correlationGrain.DeliverMessage(new ExpandoObject());
```

Add `using Fleans.Application.Grains;` if not already present in each test file (it should already be there).

**Step 3: Run all integration tests**

Run: `dotnet test src/Fleans/Fleans.Application.Tests --no-restore`
Expected: All tests pass.

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Application.Tests/MessageIntermediateCatchEventTests.cs src/Fleans/Fleans.Application.Tests/MessageBoundaryEventTests.cs
git commit -m "test: update integration tests for partitioned message correlation grain key"
```

---

### Task 9: Run full test suite and verify

**Step 1: Run all tests**

Run: `dotnet test src/Fleans/ --no-restore`
Expected: All tests pass (Application, Infrastructure, Persistence, Domain).

**Step 2: Verify no compilation warnings related to changes**

Run: `dotnet build src/Fleans/ --no-restore 2>&1 | grep -i "error\|MessageCorrelation"`
Expected: No errors. Warnings about the old `Subscriptions` property should be gone.

**Step 3: Commit (if any fixups needed)**

Only commit if there were fixups. Otherwise, this task is just verification.

---

### Task 10: Update MCP server if it references message correlation

**Files:**
- Check: `src/Fleans/Fleans.Mcp/` for any message delivery references

**Step 1: Search for message correlation usage**

Search for `IMessageCorrelationGrain` or `DeliverMessage` in the MCP project. If found, update the grain key construction the same way as the API controller.

If not found (likely — the MCP server currently only has deploy/list/get tools, not message delivery), no changes needed.

**Step 2: Commit if changes were made**

---

### Task 11: Final commit — squash or clean up if needed

**Step 1: Review all commits**

Run: `git log --oneline -10`
Verify the commit history tells a clean story.

**Step 2: Push to feature branch**

```bash
git push -u origin feature/message-correlation-scalability
```
