using System.Dynamic;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Activities;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowExecutionTransitionTests
{
    /// <summary>
    /// Creates a started workflow execution with start activity already completed.
    /// Returns the execution, state, and start entry's instance ID.
    /// The start entry is already completed with MarkCompleted + variables merged.
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state, ActivityInstanceEntry completedEntry)
        CreateWithCompletedStart(
            List<Activity>? extraActivities = null,
            List<SequenceFlow>? extraFlows = null)
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");
        var activities = new List<Activity> { start, end };
        if (extraActivities is not null)
            activities.AddRange(extraActivities);

        var flows = new List<SequenceFlow> { new("seq1", start, end) };
        if (extraFlows is not null)
            flows.AddRange(extraFlows);

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = activities,
            SequenceFlows = flows,
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        return (execution, state, startEntry);
    }

    // --- Simple sequential flow ---

    [TestMethod]
    public void ResolveTransitions_SimpleSequentialFlow_ShouldSpawnNextActivity()
    {
        var (execution, state, completedEntry) = CreateWithCompletedStart();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(completedEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(new EndEvent("end1"))])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("end1", spawned.ActivityId);
        Assert.AreEqual("EndEvent", spawned.ActivityType);
        Assert.AreEqual(completedEntry.VariablesId, spawned.VariablesId);
        Assert.AreEqual(completedEntry.ScopeId, spawned.ScopeId);
        Assert.IsNull(spawned.TokenId);
    }

    [TestMethod]
    public void ResolveTransitions_SimpleSequentialFlow_ShouldCreateNewEntry()
    {
        var (execution, state, completedEntry) = CreateWithCompletedStart();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(completedEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(new EndEvent("end1"))])
        };

        execution.ResolveTransitions(transitions);

        // Start entry (completed) + new end entry
        var activeEntries = state.GetActiveActivities().ToList();
        Assert.AreEqual(1, activeEntries.Count);
        Assert.AreEqual("end1", activeEntries[0].ActivityId);
        Assert.IsFalse(activeEntries[0].IsExecuting);
        Assert.IsFalse(activeEntries[0].IsCompleted);
    }

    // --- Failed entry should not transition ---

    [TestMethod]
    public void ResolveTransitions_FailedEntry_ShouldNotSpawnNextActivity()
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("seq1", start, end)],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);

        // Simulate a failure: the entry has an error code set
        startEntry.Fail(new Exception("test error"));
        execution.ClearUncommittedEvents();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(new EndEvent("end1"))])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        Assert.AreEqual(0, events.OfType<ActivitySpawned>().Count());
    }

    // --- Cancelled entry should not transition ---

    [TestMethod]
    public void ResolveTransitions_CancelledEntry_ShouldNotSpawnNextActivity()
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("seq1", start, end)],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);

        // Cancel the entry
        startEntry.Cancel("test cancellation");
        execution.ClearUncommittedEvents();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(new EndEvent("end1"))])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        Assert.AreEqual(0, events.OfType<ActivitySpawned>().Count());
    }

    // --- Parallel gateway fork (TokenAction.CreateNew) ---

    [TestMethod]
    public void ResolveTransitions_ParallelGatewayFork_ShouldCreateNewTokensAndForkState()
    {
        var start = new StartEvent("start1");
        var fork = new ParallelGateway("fork1", IsFork: true);
        var task1 = new ScriptTask("task1", "return 1;");
        var task2 = new ScriptTask("task2", "return 2;");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, fork, task1, task2],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, fork),
                new SequenceFlow("seq2", fork, task1),
                new SequenceFlow("seq3", fork, task2)
            ],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        // Get the start entry, execute and complete it
        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());

        // Now spawn the fork gateway entry
        execution.ClearUncommittedEvents();
        execution.ResolveTransitions(
        [
            new CompletedActivityTransitions(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(fork)])
        ]);

        // Fork entry should be spawned
        var forkEntry = state.GetActiveActivities().First(e => e.ActivityId == "fork1");
        execution.MarkExecuting(forkEntry.ActivityInstanceId);
        execution.MarkCompleted(forkEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Now resolve transitions from the fork with CreateNew tokens
        var transitions = new List<CompletedActivityTransitions>
        {
            new(forkEntry.ActivityInstanceId, "fork1",
            [
                new ActivityTransition(task1, CloneVariables: true, Token: TokenAction.CreateNew),
                new ActivityTransition(task2, CloneVariables: true, Token: TokenAction.CreateNew)
            ])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();

        // Should have GatewayForkCreated
        var forkCreated = events.OfType<GatewayForkCreated>().Single();
        Assert.AreEqual(forkEntry.ActivityInstanceId, forkCreated.ForkInstanceId);

        // Should have two GatewayForkTokenAdded
        var tokensAdded = events.OfType<GatewayForkTokenAdded>().ToList();
        Assert.AreEqual(2, tokensAdded.Count);
        Assert.IsTrue(tokensAdded.All(t => t.ForkInstanceId == forkEntry.ActivityInstanceId));

        // Should have two ActivitySpawned with different tokens
        var spawned = events.OfType<ActivitySpawned>().ToList();
        Assert.AreEqual(2, spawned.Count);
        Assert.IsNotNull(spawned[0].TokenId);
        Assert.IsNotNull(spawned[1].TokenId);
        Assert.AreNotEqual(spawned[0].TokenId, spawned[1].TokenId);

        // Should have VariableScopeCloned for each branch
        var cloned = events.OfType<VariableScopeCloned>().ToList();
        Assert.AreEqual(2, cloned.Count);

        // Fork state should be created in state
        Assert.AreEqual(1, state.GatewayForks.Count);
        Assert.AreEqual(2, state.GatewayForks[0].CreatedTokenIds.Count);
    }

    // --- Parallel gateway join (TokenAction.RestoreParent) ---

    [TestMethod]
    public void ResolveTransitions_ParallelGatewayJoin_ShouldRestoreParentTokenAndRemoveFork()
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("seq1", start, end)],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Manually set up fork state: a fork was created previously with a consumed token
        var parentTokenId = Guid.NewGuid();
        var childToken1 = Guid.NewGuid();
        var forkId = Guid.NewGuid();
        var fork = state.CreateGatewayFork(forkId, parentTokenId);
        fork.CreatedTokenIds.Add(childToken1);

        // Set the completed entry's token to childToken1
        startEntry.SetTokenId(childToken1);

        var transitions = new List<CompletedActivityTransitions>
        {
            new(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();

        // Should have GatewayForkRemoved
        var forkRemoved = events.OfType<GatewayForkRemoved>().Single();
        Assert.AreEqual(forkId, forkRemoved.ForkInstanceId);

        // The new activity should get the parent token
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(parentTokenId, spawned.TokenId);

        // Fork should be removed from state
        Assert.AreEqual(0, state.GatewayForks.Count);
    }

    [TestMethod]
    public void ResolveTransitions_RestoreParent_NoFork_ShouldInheritToken()
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("seq1", start, end)],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Set a token but no fork state
        var tokenId = Guid.NewGuid();
        startEntry.SetTokenId(tokenId);

        var transitions = new List<CompletedActivityTransitions>
        {
            new(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();

        // Should inherit the token since no fork found
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(tokenId, spawned.TokenId);

        // No fork removed
        Assert.AreEqual(0, events.OfType<GatewayForkRemoved>().Count());
    }

    // --- Join gateway deduplication ---

    [TestMethod]
    public void ResolveTransitions_JoinGatewayDeduplication_ShouldResetExecutingAndNotCreateNew()
    {
        var start = new StartEvent("start1");
        var join = new ParallelGateway("join1", IsFork: false);
        var end = new EndEvent("end1");

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, join, end],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, join),
                new SequenceFlow("seq2", join, end)
            ],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Simulate: there is already an active entry for the join gateway in the same scope
        var existingJoinEntry = new ActivityInstanceEntry(
            Guid.NewGuid(), "join1", state.Id, startEntry.ScopeId);
        existingJoinEntry.SetActivityType("ParallelGateway");
        existingJoinEntry.SetVariablesId(startEntry.VariablesId);
        existingJoinEntry.Execute(); // mark as executing
        state.AddEntries([existingJoinEntry]);

        var transitions = new List<CompletedActivityTransitions>
        {
            new(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(join)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();

        // Should NOT create a new ActivitySpawned for the join
        Assert.AreEqual(0, events.OfType<ActivitySpawned>().Count());

        // The existing join entry should have IsExecuting reset
        Assert.IsFalse(existingJoinEntry.IsExecuting);
    }

    [TestMethod]
    public void ResolveTransitions_JoinGateway_NoExistingEntry_ShouldSpawnNew()
    {
        var start = new StartEvent("start1");
        var join = new ParallelGateway("join1", IsFork: false);

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, join],
            SequenceFlows = [new SequenceFlow("seq1", start, join)],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(startEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(join)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();

        // Should create new entry since no existing active entry for join
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual("join1", spawned.ActivityId);
    }

    // --- CloneVariables ---

    [TestMethod]
    public void ResolveTransitions_CloneVariables_ShouldCreateNewVariableScope()
    {
        var (execution, state, completedEntry) = CreateWithCompletedStart();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(completedEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(new EndEvent("end1"), CloneVariables: true)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();

        // Should have VariableScopeCloned
        var cloned = events.OfType<VariableScopeCloned>().Single();
        Assert.AreEqual(completedEntry.VariablesId, cloned.SourceScopeId);
        Assert.AreNotEqual(completedEntry.VariablesId, cloned.NewScopeId);

        // The spawned activity should use the new variable scope
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(cloned.NewScopeId, spawned.VariablesId);

        // State should have the new variable scope
        Assert.IsNotNull(state.VariableStates.FirstOrDefault(v => v.Id == cloned.NewScopeId));
    }

    // --- Token inherit ---

    [TestMethod]
    public void ResolveTransitions_TokenInherit_ShouldPassTokenToNext()
    {
        var (execution, state, completedEntry) = CreateWithCompletedStart();

        var tokenId = Guid.NewGuid();
        completedEntry.SetTokenId(tokenId);

        var transitions = new List<CompletedActivityTransitions>
        {
            new(completedEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.Inherit)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(tokenId, spawned.TokenId);
    }

    [TestMethod]
    public void ResolveTransitions_TokenInherit_NullToken_ShouldSpawnWithNullToken()
    {
        var (execution, state, completedEntry) = CreateWithCompletedStart();
        // completedEntry has no token (null)

        var transitions = new List<CompletedActivityTransitions>
        {
            new(completedEntry.ActivityInstanceId, "start1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.Inherit)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.IsNull(spawned.TokenId);
    }

    // --- Multiple transitions from one completed entry ---

    [TestMethod]
    public void ResolveTransitions_MultipleTransitions_ShouldSpawnAll()
    {
        var (execution, state, completedEntry) = CreateWithCompletedStart();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(completedEntry.ActivityInstanceId, "start1",
            [
                new ActivityTransition(new ScriptTask("task1", "return 1;")),
                new ActivityTransition(new ScriptTask("task2", "return 2;"))
            ])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().ToList();
        Assert.AreEqual(2, spawned.Count);
        Assert.AreEqual("task1", spawned[0].ActivityId);
        Assert.AreEqual("task2", spawned[1].ActivityId);
    }

    // --- Empty transitions ---

    [TestMethod]
    public void ResolveTransitions_EmptyTransitions_ShouldNotEmitEvents()
    {
        var (execution, state, completedEntry) = CreateWithCompletedStart();

        execution.ResolveTransitions([]);

        var events = execution.GetUncommittedEvents();
        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void ResolveTransitions_CompletedEntryWithNoTransitions_ShouldNotEmitEvents()
    {
        var (execution, state, completedEntry) = CreateWithCompletedStart();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(completedEntry.ActivityInstanceId, "start1", [])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        Assert.AreEqual(0, events.Count);
    }

    // --- ScopeId propagation ---

    [TestMethod]
    public void ResolveTransitions_ShouldPropagateScopeIdFromCompletedEntry()
    {
        var start = new StartEvent("start1");
        var end = new EndEvent("end1");
        var scopeId = Guid.NewGuid();

        var definition = new WorkflowDefinition
        {
            WorkflowId = "wf1",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("seq1", start, end)],
            ProcessDefinitionId = "pd1"
        };
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, definition);
        execution.Start();

        var startEntry = state.Entries.First();
        execution.MarkExecuting(startEntry.ActivityInstanceId);
        execution.MarkCompleted(startEntry.ActivityInstanceId, new ExpandoObject());
        execution.ClearUncommittedEvents();

        // Manually set scope on the completed entry to test propagation
        // We need a separate entry with a scope for this test
        var scopedEntry = new ActivityInstanceEntry(Guid.NewGuid(), "task1", state.Id, scopeId);
        scopedEntry.SetActivityType("ScriptTask");
        scopedEntry.SetVariablesId(state.VariableStates.First().Id);
        scopedEntry.Execute();
        scopedEntry.Complete();
        state.AddEntries([scopedEntry]);

        var transitions = new List<CompletedActivityTransitions>
        {
            new(scopedEntry.ActivityInstanceId, "task1",
                [new ActivityTransition(new EndEvent("end1"))])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(scopeId, spawned.ScopeId);
    }
}
