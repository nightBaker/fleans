# Gateway Redesign Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Restructure gateway hierarchy (Gateway → ConditionalGateway → ExclusiveGateway), add default flow support, and make ExclusiveGateway auto-complete when conditions are evaluated.

**Architecture:** Thin `Gateway` base (no methods), new `ConditionalGateway` abstract class with condition evaluation and auto-completion logic, `DefaultSequenceFlow` type for fallback routing. `SetConditionResult` returns `bool` so `WorkflowInstance.CompleteConditionSequence` knows when to resume the workflow.

**Tech Stack:** .NET 10, C# 14, Orleans 9.2.1, MSTest, Orleans.TestingHost

**Design doc:** `docs/plans/2026-02-09-gateway-redesign.md`

---

### Task 1: Add `IsEvaluated` to `ConditionSequenceState`

**Files:**
- Modify: `src/Fleans/Fleans.Domain/States/ConditionSequenceState.cs`
- Test: `src/Fleans/Fleans.Domain.Tests/ConditionSequenceStateTests.cs`

**Step 1: Write the failing tests**

Create `src/Fleans/Fleans.Domain.Tests/ConditionSequenceStateTests.cs`:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class ConditionSequenceStateTests
{
    [TestMethod]
    public void IsEvaluated_ShouldBeFalse_WhenCreated()
    {
        // Arrange
        var source = new ExclusiveGateway("gw1");
        var target = new EndEvent("end1");
        var flow = new ConditionalSequenceFlow("seq1", source, target, "x > 5");

        // Act
        var state = new ConditionSequenceState(flow);

        // Assert
        Assert.IsFalse(state.IsEvaluated);
        Assert.IsFalse(state.Result);
    }

    [TestMethod]
    public void IsEvaluated_ShouldBeTrue_AfterSetResultTrue()
    {
        // Arrange
        var source = new ExclusiveGateway("gw1");
        var target = new EndEvent("end1");
        var flow = new ConditionalSequenceFlow("seq1", source, target, "x > 5");
        var state = new ConditionSequenceState(flow);

        // Act
        state.SetResult(true);

        // Assert
        Assert.IsTrue(state.IsEvaluated);
        Assert.IsTrue(state.Result);
    }

    [TestMethod]
    public void IsEvaluated_ShouldBeTrue_AfterSetResultFalse()
    {
        // Arrange
        var source = new ExclusiveGateway("gw1");
        var target = new EndEvent("end1");
        var flow = new ConditionalSequenceFlow("seq1", source, target, "x > 5");
        var state = new ConditionSequenceState(flow);

        // Act
        state.SetResult(false);

        // Assert
        Assert.IsTrue(state.IsEvaluated);
        Assert.IsFalse(state.Result);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~ConditionSequenceStateTests" --no-restore`
Expected: FAIL — `IsEvaluated` property does not exist.

**Step 3: Add `IsEvaluated` property**

Modify `src/Fleans/Fleans.Domain/States/ConditionSequenceState.cs`:

```csharp
using Fleans.Domain.Sequences;

namespace Fleans.Domain.States;

[GenerateSerializer]
public class ConditionSequenceState
{
    public ConditionSequenceState(ConditionalSequenceFlow conditionalSequence)
    {
        ConditionalSequence = conditionalSequence;
    }

    [Id(0)]
    public Guid ConditionSequenceStateId { get; } = Guid.NewGuid();

    [Id(1)]
    public ConditionalSequenceFlow ConditionalSequence { get; set; }

    [Id(2)]
    public bool Result { get; private set; }

    [Id(3)]
    public bool IsEvaluated { get; private set; }

    internal void SetResult(bool result)
    {
        Result = result;
        IsEvaluated = true;
    }
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~ConditionSequenceStateTests" --no-restore`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/States/ConditionSequenceState.cs src/Fleans/Fleans.Domain.Tests/ConditionSequenceStateTests.cs
git commit -m "feat: add IsEvaluated property to ConditionSequenceState"
```

---

### Task 2: Create `DefaultSequenceFlow`

**Files:**
- Create: `src/Fleans/Fleans.Domain/Sequences/DefaultSequenceFlow.cs`

**Step 1: Create the type**

Create `src/Fleans/Fleans.Domain/Sequences/DefaultSequenceFlow.cs`:

```csharp
using Fleans.Domain.Activities;

namespace Fleans.Domain.Sequences;

[GenerateSerializer]
public record DefaultSequenceFlow(string SequenceFlowId, Activity Source, Activity Target)
    : SequenceFlow(SequenceFlowId, Source, Target);
```

**Step 2: Build to verify it compiles**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Fleans/Fleans.Domain/Sequences/DefaultSequenceFlow.cs
git commit -m "feat: add DefaultSequenceFlow type for gateway default routing"
```

---

### Task 3: Restructure Gateway hierarchy — thin `Gateway`, new `ConditionalGateway`

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/Gateway.cs`
- Create: `src/Fleans/Fleans.Domain/Activities/ConditionalGateway.cs`
- Modify: `src/Fleans/Fleans.Domain/Activities/ExclusiveGateway.cs`

**Step 1: Make `Gateway` a thin marker class**

Replace `src/Fleans/Fleans.Domain/Activities/Gateway.cs` with:

```csharp
namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Gateway : Activity
{
    protected Gateway(string ActivityId) : base(ActivityId)
    {
    }
}
```

**Step 2: Create `ConditionalGateway` with `SetConditionResult` returning `bool`**

Create `src/Fleans/Fleans.Domain/Activities/ConditionalGateway.cs`:

```csharp
using Fleans.Domain.Sequences;
using Microsoft.Extensions.Logging;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract partial record ConditionalGateway : Gateway
{
    protected ConditionalGateway(string ActivityId) : base(ActivityId)
    {
    }

    internal async Task<bool> SetConditionResult(
        IWorkflowInstance workflowInstance,
        IActivityInstance activityInstance,
        string conditionSequenceFlowId,
        bool result)
    {
        // 1. Store the result in state
        var state = await workflowInstance.GetState();
        var activityInstanceId = await activityInstance.GetActivityInstanceId();
        await state.SetCondigitionSequencesResult(activityInstanceId, conditionSequenceFlowId, result);

        // 2. Short-circuit: first true → done
        if (result)
            return true;

        // 3. Check if all conditions are now evaluated
        var sequences = await state.GetConditionSequenceStates();
        var mySequences = sequences[activityInstanceId];

        if (mySequences.All(s => s.IsEvaluated))
        {
            // All false — need a default flow
            var definition = await workflowInstance.GetWorkflowDefinition();
            var hasDefault = definition.SequenceFlows
                .OfType<DefaultSequenceFlow>()
                .Any(sf => sf.Source.ActivityId == ActivityId);

            if (!hasDefault)
                throw new InvalidOperationException(
                    $"Gateway {ActivityId}: all conditions evaluated to false and no default flow exists");

            return true;
        }

        // 4. Still waiting on more conditions
        return false;
    }
}
```

**Step 3: Update `ExclusiveGateway` to extend `ConditionalGateway`**

Replace `src/Fleans/Fleans.Domain/Activities/ExclusiveGateway.cs` with:

```csharp
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Orleans.Runtime;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ExclusiveGateway : ConditionalGateway
{
    public ExclusiveGateway(string activityId) : base(activityId)
    {
        ActivityId = activityId;
    }

    internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        await base.ExecuteAsync(workflowInstance, activityInstance);

        var sequences = await AddConditionalSequencesToWorkflowInstance(workflowInstance, activityInstance);

        await QueueEvaluateConditionEvents(workflowInstance, activityInstance, sequences);
    }

    private static async Task<IEnumerable<ConditionalSequenceFlow>> AddConditionalSequencesToWorkflowInstance(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        var currentActivity = await activityInstance.GetCurrentActivity();

        var sequences = (await workflowInstance.GetWorkflowDefinition()).SequenceFlows.OfType<ConditionalSequenceFlow>()
                                .Where(sf => sf.Source.ActivityId == currentActivity.ActivityId)
                                .ToArray();

        var state = await workflowInstance.GetState();
        await state.AddConditionSequenceStates(await activityInstance.GetActivityInstanceId(), sequences);
        return sequences;
    }

    private async Task QueueEvaluateConditionEvents(IWorkflowInstance workflowInstance, IActivityInstance activityInstance, IEnumerable<ConditionalSequenceFlow> sequences)
    {
        var definition = await workflowInstance.GetWorkflowDefinition();
        foreach (var sequence in sequences)
        {
            await activityInstance.PublishEvent(new EvaluateConditionEvent(workflowInstance.GetGrainId().GetGuidKey(),
                                                               definition.WorkflowId,
                                                                definition.ProcessDefinitionId,
                                                                await activityInstance.GetActivityInstanceId(),
                                                                ActivityId,
                                                                sequence.SequenceFlowId,
                                                                sequence.Condition));
        }
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        var state = await workflowInstance.GetState();
        var sequencesState = await state.GetConditionSequenceStates();
        var activitySequencesState = sequencesState[await activityInstance.GetActivityInstanceId()];

        // Check for a true condition first (short-circuit result)
        var trueTargets = activitySequencesState
            .Where(x => x.Result)
            .Select(x => x.ConditionalSequence.Target)
            .ToList();

        if (trueTargets.Count > 0)
            return trueTargets;

        // All conditions false — take the default flow
        var definition = await workflowInstance.GetWorkflowDefinition();
        var defaultFlow = definition.SequenceFlows
            .OfType<DefaultSequenceFlow>()
            .FirstOrDefault(sf => sf.Source.ActivityId == ActivityId);

        if (defaultFlow is not null)
            return [defaultFlow.Target];

        throw new InvalidOperationException(
            $"ExclusiveGateway {ActivityId}: no true condition and no default flow");
    }
}
```

**Step 4: Build to verify it compiles**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Fleans/Fleans.Domain/Activities/Gateway.cs src/Fleans/Fleans.Domain/Activities/ConditionalGateway.cs src/Fleans/Fleans.Domain/Activities/ExclusiveGateway.cs
git commit -m "feat: restructure gateway hierarchy with ConditionalGateway base"
```

---

### Task 4: Update `WorkflowInstance.CompleteConditionSequence` to auto-complete

**Files:**
- Modify: `src/Fleans/Fleans.Domain/WorkflowInstance.cs`

**Step 1: Update `CompleteConditionSequence`**

In `src/Fleans/Fleans.Domain/WorkflowInstance.cs`, replace the `CompleteConditionSequence` method (lines 151-162) with:

```csharp
    public async Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogConditionResult(conditionSequenceId, result);

        var activityInstance = await State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        var gateway = await activityInstance.GetCurrentActivity() as ConditionalGateway
            ?? throw new InvalidOperationException("Activity is not a conditional gateway");

        var isDecisionMade = await gateway.SetConditionResult(
            this, activityInstance, conditionSequenceId, result);

        if (isDecisionMade)
        {
            LogGatewayAutoCompleting(activityId);
            await activityInstance.Complete();
            await ExecuteWorkflow();
        }
    }
```

**Step 2: Add the new log method**

Add after the existing `LogTransition` method (around line 257) in the same file:

```csharp
    [LoggerMessage(EventId = 1007, Level = LogLevel.Information, Message = "Gateway {ActivityId} decision made, auto-completing and resuming workflow")]
    private partial void LogGatewayAutoCompleting(string activityId);
```

**Step 3: Build to verify it compiles**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/WorkflowInstance.cs
git commit -m "feat: auto-complete conditional gateways in CompleteConditionSequence"
```

---

### Task 5: Update ExclusiveGateway tests — remove manual `CompleteActivity`, add new scenarios

**Files:**
- Modify: `src/Fleans/Fleans.Domain.Tests/ExclusiveGatewayTests.cs`

**Step 1: Rewrite the test file**

Replace `src/Fleans/Fleans.Domain.Tests/ExclusiveGatewayTests.cs` with:

```csharp
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.Serialization;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Domain.Tests;

[TestClass]
public class ExclusiveGatewayTests
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
    public async Task ExclusiveGateway_ShouldTakeTrueBranch_WhenFirstConditionIsTrue()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition true, gateway should auto-complete
        await workflowInstance.CompleteConditionSequence("if", "seq2", true);

        // Assert — workflow completed via end1 (true branch)
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var completedIds = await GetCompletedActivityIds(state);
        CollectionAssert.Contains(completedIds, "end1");
        CollectionAssert.DoesNotContain(completedIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldShortCircuit_OnFirstTrueCondition()
    {
        // Arrange — gateway with two conditional flows, first returns true
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition is true → should complete immediately
        // Second condition is never evaluated
        await workflowInstance.CompleteConditionSequence("if", "seq2", true);

        // Assert — workflow completed without needing seq3
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var activeActivities = await state.GetActiveActivities();
        Assert.HasCount(0, activeActivities);
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeSecondBranch_WhenFirstIsFalseSecondIsTrue()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition false, second true
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);
        await workflowInstance.CompleteConditionSequence("if", "seq3", true);

        // Assert — workflow completed via end2
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var completedIds = await GetCompletedActivityIds(state);
        CollectionAssert.Contains(completedIds, "end2");
        CollectionAssert.DoesNotContain(completedIds, "end1");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldTakeDefaultFlow_WhenAllConditionsAreFalse()
    {
        // Arrange
        var workflow = CreateWorkflowWithDefaultFlow();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — all conditions false
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);
        await workflowInstance.CompleteConditionSequence("if", "seq3", false);

        // Assert — workflow completed via endDefault (default flow)
        var state = await workflowInstance.GetState();
        Assert.IsTrue(await state.IsCompleted());

        var completedIds = await GetCompletedActivityIds(state);
        CollectionAssert.Contains(completedIds, "endDefault");
        CollectionAssert.DoesNotContain(completedIds, "end1");
        CollectionAssert.DoesNotContain(completedIds, "end2");
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldThrow_WhenAllConditionsFalse_AndNoDefaultFlow()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches(); // no default flow
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition false
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);

        // Assert — second condition false should throw
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await workflowInstance.CompleteConditionSequence("if", "seq3", false);
        });
    }

    [TestMethod]
    public async Task ExclusiveGateway_ShouldNotAutoComplete_WhenConditionsStillPending()
    {
        // Arrange
        var workflow = CreateWorkflowWithTwoBranches();
        var workflowInstance = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);
        await workflowInstance.StartWorkflow();

        // Act — first condition false, second not yet evaluated
        await workflowInstance.CompleteConditionSequence("if", "seq2", false);

        // Assert — workflow not completed, gateway still active
        var state = await workflowInstance.GetState();
        Assert.IsFalse(await state.IsCompleted());

        var activeActivities = await state.GetActiveActivities();
        Assert.IsTrue(activeActivities.Count > 0);
    }

    private static async Task<List<string>> GetCompletedActivityIds(IWorkflowInstanceState state)
    {
        var completed = await state.GetCompletedActivities();
        var ids = new List<string>();
        foreach (var activity in completed)
        {
            var current = await activity.GetCurrentActivity();
            ids.Add(current.ActivityId);
        }
        return ids;
    }

    private static IWorkflowDefinition CreateWorkflowWithTwoBranches()
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
                new ConditionalSequenceFlow("seq2", ifActivity, end1, "trueCondition"),
                new ConditionalSequenceFlow("seq3", ifActivity, end2, "falseCondition")
            ]
        };
    }

    private static IWorkflowDefinition CreateWorkflowWithDefaultFlow()
    {
        var start = new StartEvent("start");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var endDefault = new EndEvent("endDefault");
        var ifActivity = new ExclusiveGateway("if");

        return new WorkflowDefinition
        {
            WorkflowId = "workflow-default",
            Activities = [start, ifActivity, end1, end2, endDefault],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, ifActivity),
                new ConditionalSequenceFlow("seq2", ifActivity, end1, "condition1"),
                new ConditionalSequenceFlow("seq3", ifActivity, end2, "condition2"),
                new DefaultSequenceFlow("seqDefault", ifActivity, endDefault)
            ]
        };
    }

    private static TestCluster CreateCluster()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        var cluster = builder.Build();
        cluster.Deploy();
        return cluster;
    }

    class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
           hostBuilder.ConfigureServices(services => services.AddSerializer(serializerBuilder =>
           {
               serializerBuilder.AddNewtonsoftJsonSerializer(
                   isSupported: type => type == typeof(ExpandoObject),
                   new Newtonsoft.Json.JsonSerializerSettings
                   {
                       TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                   });
           }));
    }
}
```

**Step 2: Run all tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~ExclusiveGatewayTests" --no-restore`
Expected: PASS (6 tests)

**Step 3: Run the full test suite to check for regressions**

Run: `dotnet test src/Fleans/ --no-restore`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain.Tests/ExclusiveGatewayTests.cs
git commit -m "test: rewrite ExclusiveGateway tests for auto-completion and default flow"
```

---

### Task 6: Update `BpmnConverter` to parse `default` attribute and create `DefaultSequenceFlow`

**Files:**
- Modify: `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs`
- Modify: `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverterTests.cs`

**Step 1: Write the failing test**

Add to `src/Fleans/Fleans.Infrastructure.Tests/BpmnConverterTests.cs`:

A new test method:

```csharp
    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseDefaultFlow_OnExclusiveGateway()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""workflow-default"">
    <startEvent id=""start"" />
    <exclusiveGateway id=""gw1"" default=""flowDefault"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <endEvent id=""endDefault"" />
    <sequenceFlow id=""flow0"" sourceRef=""start"" targetRef=""gw1"" />
    <sequenceFlow id=""flow1"" sourceRef=""gw1"" targetRef=""end1"">
      <conditionExpression>${x > 10}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""flow2"" sourceRef=""gw1"" targetRef=""end2"">
      <conditionExpression>${x > 5}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""flowDefault"" sourceRef=""gw1"" targetRef=""endDefault"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var defaultFlow = workflow.SequenceFlows.OfType<DefaultSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(defaultFlow, "Should have a DefaultSequenceFlow");
        Assert.AreEqual("flowDefault", defaultFlow.SequenceFlowId);
        Assert.AreEqual("gw1", defaultFlow.Source.ActivityId);
        Assert.AreEqual("endDefault", defaultFlow.Target.ActivityId);

        // Conditional flows should still be ConditionalSequenceFlow
        var conditionalFlows = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().ToList();
        Assert.AreEqual(2, conditionalFlows.Count);
    }
```

**Step 2: Run test to verify it fails**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~ConvertFromXmlAsync_ShouldParseDefaultFlow" --no-restore`
Expected: FAIL — the flow is parsed as a plain `SequenceFlow`, not `DefaultSequenceFlow`.

**Step 3: Update `BpmnConverter` to collect gateway defaults and produce `DefaultSequenceFlow`**

In `src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs`:

Add a field to `ParseActivities` and pass it through. The simplest approach: collect default flow IDs during activity parsing and use them during sequence flow parsing.

Change the `ConvertFromXml` method (lines 24-51) to pass a `defaultFlowIds` set:

```csharp
    private WorkflowDefinition ConvertFromXml(string bpmnXml)
    {
        var doc = XDocument.Parse(bpmnXml);
        var process = doc.Descendants(Bpmn + "process").FirstOrDefault()
            ?? throw new InvalidOperationException("BPMN file must contain a process element");

        var workflowId = process.Attribute("id")?.Value
            ?? throw new InvalidOperationException("Process must have an id attribute");

        var activities = new List<Activity>();
        var sequenceFlows = new List<SequenceFlow>();
        var activityMap = new Dictionary<string, Activity>();
        var defaultFlowIds = new HashSet<string>();

        // Parse activities
        ParseActivities(process, activities, activityMap, defaultFlowIds);

        // Parse sequence flows
        ParseSequenceFlows(process, sequenceFlows, activityMap, defaultFlowIds);

        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = activities,
            SequenceFlows = sequenceFlows
        };

        return workflow;
    }
```

Update `ParseActivities` signature (line 53) to accept `defaultFlowIds` and collect them from exclusive gateways:

```csharp
    private void ParseActivities(XElement process, List<Activity> activities, Dictionary<string, Activity> activityMap, HashSet<string> defaultFlowIds)
```

In the exclusive gateway parsing block (lines 114-121), add default attribute collection:

```csharp
        // Parse exclusive gateways
        foreach (var gateway in process.Descendants(Bpmn + "exclusiveGateway"))
        {
            var id = GetId(gateway);
            var defaultFlowId = gateway.Attribute("default")?.Value;
            if (defaultFlowId is not null)
                defaultFlowIds.Add(defaultFlowId);

            var activity = new ExclusiveGateway(id);
            activities.Add(activity);
            activityMap[id] = activity;
        }
```

Update `ParseSequenceFlows` signature (line 144) to accept `defaultFlowIds`:

```csharp
    private void ParseSequenceFlows(XElement process, List<SequenceFlow> sequenceFlows, Dictionary<string, Activity> activityMap, HashSet<string> defaultFlowIds)
```

In the flow creation logic (lines 161-176), add the `DefaultSequenceFlow` check before the conditional check:

```csharp
            // Check for condition expression
            var conditionExpression = flow.Descendants(Bpmn + "conditionExpression").FirstOrDefault();

            if (conditionExpression != null)
            {
                var condition = conditionExpression.Value.Trim();
                condition = ConvertBpmnCondition(condition);
                sequenceFlows.Add(new ConditionalSequenceFlow(flowId, source, target, condition));
            }
            else if (defaultFlowIds.Contains(flowId))
            {
                sequenceFlows.Add(new DefaultSequenceFlow(flowId, source, target));
            }
            else
            {
                sequenceFlows.Add(new SequenceFlow(flowId, source, target));
            }
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test src/Fleans/ --filter "FullyQualifiedName~BpmnConverterTests" --no-restore`
Expected: All pass (including the new test)

**Step 5: Run the full test suite**

Run: `dotnet test src/Fleans/ --no-restore`
Expected: All tests pass

**Step 6: Commit**

```bash
git add src/Fleans/Fleans.Infrastructure/Bpmn/BpmnConverter.cs src/Fleans/Fleans.Infrastructure.Tests/BpmnConverterTests.cs
git commit -m "feat: parse BPMN default attribute on exclusive gateways as DefaultSequenceFlow"
```

---

### Task 7: Add logging to `ConditionalGateway`

**Files:**
- Modify: `src/Fleans/Fleans.Domain/Activities/ConditionalGateway.cs`

**Step 1: Add `[LoggerMessage]` methods to `ConditionalGateway`**

`ConditionalGateway` is a `record`, not a `Grain`, so it doesn't have an `ILogger` injected. The logging must use `ILogger` obtained from the workflow instance context. However, records don't have DI.

Alternative: log from `WorkflowInstance.CompleteConditionSequence` which already has `_logger`. The gateway returns `bool` and the WorkflowInstance logs accordingly. We already added `LogGatewayAutoCompleting` in Task 4.

Add additional log calls in `WorkflowInstance.CompleteConditionSequence` to cover all cases per the design doc:

In `src/Fleans/Fleans.Domain/WorkflowInstance.cs`, update `CompleteConditionSequence`:

```csharp
    public async Task CompleteConditionSequence(string activityId, string conditionSequenceId, bool result)
    {
        SetWorkflowRequestContext();
        using var scope = BeginWorkflowScope();
        LogConditionResult(conditionSequenceId, result);

        var activityInstance = await State.GetFirstActive(activityId)
            ?? throw new InvalidOperationException("Active activity not found");

        var gateway = await activityInstance.GetCurrentActivity() as ConditionalGateway
            ?? throw new InvalidOperationException("Activity is not a conditional gateway");

        var isDecisionMade = await gateway.SetConditionResult(
            this, activityInstance, conditionSequenceId, result);

        if (isDecisionMade)
        {
            if (result)
                LogGatewayShortCircuited(activityId, conditionSequenceId);
            else
                LogGatewayTakingDefaultFlow(activityId);

            LogGatewayAutoCompleting(activityId);
            await activityInstance.Complete();
            await ExecuteWorkflow();
        }
    }
```

Add the new log methods:

```csharp
    [LoggerMessage(EventId = 1007, Level = LogLevel.Information, Message = "Gateway {ActivityId} decision made, auto-completing and resuming workflow")]
    private partial void LogGatewayAutoCompleting(string activityId);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Information, Message = "Gateway {ActivityId} short-circuited: condition {ConditionSequenceFlowId} is true")]
    private partial void LogGatewayShortCircuited(string activityId, string conditionSequenceFlowId);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Information, Message = "Gateway {ActivityId} all conditions false, taking default flow")]
    private partial void LogGatewayTakingDefaultFlow(string activityId);
```

**Step 2: Build to verify it compiles**

Run: `dotnet build src/Fleans/`
Expected: Build succeeded

**Step 3: Run the full test suite**

Run: `dotnet test src/Fleans/ --no-restore`
Expected: All tests pass

**Step 4: Commit**

```bash
git add src/Fleans/Fleans.Domain/WorkflowInstance.cs src/Fleans/Fleans.Domain/Activities/ConditionalGateway.cs
git commit -m "feat: add structured logging for gateway condition evaluation and auto-completion"
```

---

### Task 8: Final verification — run all tests, clean up

**Step 1: Run the full test suite**

Run: `dotnet test src/Fleans/ --no-restore`
Expected: All tests pass

**Step 2: Verify no compilation warnings**

Run: `dotnet build src/Fleans/ --no-restore`
Expected: Build succeeded with no warnings related to gateway code

**Step 3: Review the `EventPublisherMock` / `SiloConfigurator` duplication**

The old `ExclusiveGatewayTests.cs` had `EventPublisherMock` and `SiloConfigurator` that were also used by other test files. Verify these still exist where needed (they're defined in each test file independently). No action needed — each test file has its own.

**Step 4: Commit (if any cleanup was needed)**

Only if cleanup was done. Otherwise, this task is just verification.
