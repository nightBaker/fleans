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

    // --- Fork-join variable scope merge ---

    /// <summary>
    /// Helper: sets up a fork-join scenario with two completed branches.
    /// Returns (execution, state, forkEntry, branchEntries with their variable scopes).
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state,
        ActivityInstanceEntry joinEntry, Guid originalScopeId,
        Guid branchScope1Id, Guid branchScope2Id)
        CreateForkJoinWithCompletedBranches(
            Action<IDictionary<string, object?>>? setBranch1Vars = null,
            Action<IDictionary<string, object?>>? setBranch2Vars = null)
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

        var originalScopeId = startEntry.VariablesId;

        // Set up fork state with two branches
        var forkInstanceId = Guid.NewGuid();
        var parentTokenId = Guid.NewGuid();
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();

        var fork = state.CreateGatewayFork(forkInstanceId, parentTokenId);
        fork.CreatedTokenIds.Add(token1);
        fork.CreatedTokenIds.Add(token2);

        // Create cloned variable scopes for each branch
        var branchScope1Id = Guid.NewGuid();
        state.AddCloneOfVariableState(branchScope1Id, originalScopeId);
        var branchScope2Id = Guid.NewGuid();
        state.AddCloneOfVariableState(branchScope2Id, originalScopeId);

        // Optionally set variables on branch scopes
        if (setBranch1Vars is not null)
        {
            var branch1Vars = new ExpandoObject();
            setBranch1Vars((IDictionary<string, object?>)branch1Vars);
            state.MergeState(branchScope1Id, branch1Vars);
        }
        if (setBranch2Vars is not null)
        {
            var branch2Vars = new ExpandoObject();
            setBranch2Vars((IDictionary<string, object?>)branch2Vars);
            state.MergeState(branchScope2Id, branch2Vars);
        }

        // Create fork entry (completed)
        var forkEntry = new ActivityInstanceEntry(forkInstanceId, "fork1", state.Id, null);
        forkEntry.SetActivityType("ParallelGateway");
        forkEntry.SetVariablesId(originalScopeId);
        forkEntry.Execute();
        forkEntry.Complete();
        state.AddEntries([forkEntry]);

        // Create completed branch entries with their tokens and scopes
        var branch1Entry = new ActivityInstanceEntry(Guid.NewGuid(), "task1", state.Id, null);
        branch1Entry.SetActivityType("ScriptTask");
        branch1Entry.SetVariablesId(branchScope1Id);
        branch1Entry.SetTokenId(token1);
        branch1Entry.Execute();
        branch1Entry.Complete();
        state.AddEntries([branch1Entry]);

        var branch2Entry = new ActivityInstanceEntry(Guid.NewGuid(), "task2", state.Id, null);
        branch2Entry.SetActivityType("ScriptTask");
        branch2Entry.SetVariablesId(branchScope2Id);
        branch2Entry.SetTokenId(token2);
        branch2Entry.Execute();
        branch2Entry.Complete();
        state.AddEntries([branch2Entry]);

        // Create join entry (completed, arrives with token1 — the first branch's token)
        var joinEntry = new ActivityInstanceEntry(Guid.NewGuid(), "join1", state.Id, null);
        joinEntry.SetActivityType("ParallelGateway");
        joinEntry.SetVariablesId(branchScope1Id);
        joinEntry.SetTokenId(token1);
        joinEntry.Execute();
        joinEntry.Complete();
        state.AddEntries([joinEntry]);

        return (execution, state, joinEntry, originalScopeId, branchScope1Id, branchScope2Id);
    }

    [TestMethod]
    public void ResolveForkJoinTransition_ShouldMergeBranchVariablesIntoOriginalScope()
    {
        var (execution, state, joinEntry, originalScopeId, _, _) =
            CreateForkJoinWithCompletedBranches(
                setBranch1Vars: d => d["varA"] = "fromBranch1",
                setBranch2Vars: d => d["varB"] = "fromBranch2");

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        // Original scope should have both branch variables
        var originalScope = state.GetVariableState(originalScopeId);
        var vars = (IDictionary<string, object?>)originalScope.Variables;
        Assert.AreEqual("fromBranch1", vars["varA"]);
        Assert.AreEqual("fromBranch2", vars["varB"]);
    }

    [TestMethod]
    public void ResolveForkJoinTransition_ShouldRemoveBranchScopes()
    {
        var (execution, state, joinEntry, originalScopeId, branchScope1Id, branchScope2Id) =
            CreateForkJoinWithCompletedBranches(
                setBranch1Vars: d => d["x"] = 1,
                setBranch2Vars: d => d["y"] = 2);

        var initialScopeCount = state.VariableStates.Count;

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        // Branch scopes should be removed
        Assert.IsNull(state.VariableStates.FirstOrDefault(v => v.Id == branchScope1Id));
        Assert.IsNull(state.VariableStates.FirstOrDefault(v => v.Id == branchScope2Id));

        // Only original scope remains (from the initial set)
        Assert.IsNotNull(state.VariableStates.FirstOrDefault(v => v.Id == originalScopeId));
    }

    [TestMethod]
    public void ResolveForkJoinTransition_ConflictingVariables_LastCreatedBranchWins()
    {
        // Both branches set "shared" — branch2 (created second in token order) should win
        var (execution, state, joinEntry, originalScopeId, _, _) =
            CreateForkJoinWithCompletedBranches(
                setBranch1Vars: d => d["shared"] = "from-branch-1",
                setBranch2Vars: d => d["shared"] = "from-branch-2");

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        var originalScope = state.GetVariableState(originalScopeId);
        var vars = (IDictionary<string, object?>)originalScope.Variables;
        Assert.AreEqual("from-branch-2", vars["shared"]);
    }

    [TestMethod]
    public void ResolveForkJoinTransition_DisjointVariables_AllPreserved()
    {
        var (execution, state, joinEntry, originalScopeId, _, _) =
            CreateForkJoinWithCompletedBranches(
                setBranch1Vars: d => d["onlyInBranch1"] = 100,
                setBranch2Vars: d => d["onlyInBranch2"] = 200);

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        var originalScope = state.GetVariableState(originalScopeId);
        var vars = (IDictionary<string, object?>)originalScope.Variables;
        Assert.AreEqual(100, vars["onlyInBranch1"]);
        Assert.AreEqual(200, vars["onlyInBranch2"]);
    }

    [TestMethod]
    public void ResolveForkJoinTransition_BranchWithNoVariableChanges_OriginalValuesPreserved()
    {
        // Set initial variable on the original scope, then only branch1 modifies
        var (execution, state, joinEntry, originalScopeId, _, _) =
            CreateForkJoinWithCompletedBranches(
                setBranch1Vars: d => d["newVar"] = "added",
                setBranch2Vars: null); // branch2 is passthrough

        // Set an initial variable on the original scope
        var initVars = new ExpandoObject();
        ((IDictionary<string, object?>)initVars)["original"] = "value";
        state.MergeState(originalScopeId, initVars);

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        var originalScope = state.GetVariableState(originalScopeId);
        var vars = (IDictionary<string, object?>)originalScope.Variables;
        Assert.AreEqual("value", vars["original"]);
        Assert.AreEqual("added", vars["newVar"]);
    }

    [TestMethod]
    public void ResolveForkJoinTransition_SpawnedActivityUsesOriginalScope()
    {
        var (execution, state, joinEntry, originalScopeId, _, _) =
            CreateForkJoinWithCompletedBranches();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();
        Assert.AreEqual(originalScopeId, spawned.VariablesId);
    }

    [TestMethod]
    public void ResolveForkJoinTransition_EmitsCorrectEventOrder()
    {
        var (execution, state, joinEntry, originalScopeId, _, _) =
            CreateForkJoinWithCompletedBranches(
                setBranch1Vars: d => d["a"] = 1,
                setBranch2Vars: d => d["b"] = 2);

        execution.ClearUncommittedEvents();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();

        // Expected order: VariablesMerged (branch1) -> VariablesMerged (branch2)
        //                 -> VariableScopesRemoved -> GatewayForkRemoved -> ActivitySpawned
        var mergedEvents = events.OfType<VariablesMerged>().ToList();
        var removedEvents = events.OfType<VariableScopesRemoved>().ToList();
        var forkRemovedEvents = events.OfType<GatewayForkRemoved>().ToList();
        var spawnedEvents = events.OfType<ActivitySpawned>().ToList();

        Assert.AreEqual(2, mergedEvents.Count);
        Assert.AreEqual(1, removedEvents.Count);
        Assert.AreEqual(1, forkRemovedEvents.Count);
        Assert.AreEqual(1, spawnedEvents.Count);

        // Verify ordering via indices
        var eventList = events.ToList();
        var firstMergeIdx = eventList.IndexOf(mergedEvents[0]);
        var lastMergeIdx = eventList.IndexOf(mergedEvents[1]);
        var removeIdx = eventList.IndexOf(removedEvents[0]);
        var forkRemoveIdx = eventList.IndexOf(forkRemovedEvents[0]);
        var spawnIdx = eventList.IndexOf(spawnedEvents[0]);

        Assert.IsTrue(firstMergeIdx < lastMergeIdx, "Merges should be in order");
        Assert.IsTrue(lastMergeIdx < removeIdx, "Scope removal after merges");
        Assert.IsTrue(removeIdx < forkRemoveIdx, "Fork removal after scope removal");
        Assert.IsTrue(forkRemoveIdx < spawnIdx, "Spawn after fork removal");
    }

    [TestMethod]
    public void ResolveForkJoinTransition_RestoresParentToken()
    {
        var (execution, state, joinEntry, _, _, _) =
            CreateForkJoinWithCompletedBranches();

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();
        var spawned = events.OfType<ActivitySpawned>().Single();

        // The fork was created with a parentTokenId — spawned activity should have it
        var fork = events.OfType<GatewayForkRemoved>().Single();
        Assert.IsNotNull(spawned.TokenId);
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

    // --- Fork-join failure path ---

    [TestMethod]
    public void ParallelFork_OneBranchFails_JoinNeverFires_NoMerge()
    {
        // Arrange: same as standard fork-join but branch2 fails instead of completing
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

        var originalScopeId = startEntry.VariablesId;

        // Set up fork state with two branches
        var forkInstanceId = Guid.NewGuid();
        var parentTokenId = Guid.NewGuid();
        var token1 = Guid.NewGuid();
        var token2 = Guid.NewGuid();

        var fork = state.CreateGatewayFork(forkInstanceId, parentTokenId);
        fork.CreatedTokenIds.Add(token1);
        fork.CreatedTokenIds.Add(token2);

        // Create cloned variable scopes
        var branchScope1Id = Guid.NewGuid();
        state.AddCloneOfVariableState(branchScope1Id, originalScopeId);
        var branchScope2Id = Guid.NewGuid();
        state.AddCloneOfVariableState(branchScope2Id, originalScopeId);

        // Branch 1 completes with a variable
        var branch1Entry = new ActivityInstanceEntry(Guid.NewGuid(), "task1", state.Id, null);
        branch1Entry.SetActivityType("ScriptTask");
        branch1Entry.SetVariablesId(branchScope1Id);
        branch1Entry.SetTokenId(token1);
        branch1Entry.Execute();
        branch1Entry.Complete();
        state.AddEntries([branch1Entry]);

        // Branch 2 fails
        var branch2Entry = new ActivityInstanceEntry(Guid.NewGuid(), "task2", state.Id, null);
        branch2Entry.SetActivityType("ScriptTask");
        branch2Entry.SetVariablesId(branchScope2Id);
        branch2Entry.SetTokenId(token2);
        branch2Entry.Execute();
        branch2Entry.Fail("500", "Something went wrong");
        state.AddEntries([branch2Entry]);

        // Create join entry
        var joinEntry = new ActivityInstanceEntry(Guid.NewGuid(), "join1", state.Id, null);
        joinEntry.SetActivityType("ParallelGateway");
        joinEntry.SetVariablesId(branchScope1Id);
        joinEntry.SetTokenId(token1);
        joinEntry.Execute();
        // Join does NOT complete because not all tokens have arrived (branch2 failed)
        state.AddEntries([joinEntry]);

        // Failed entries don't produce transitions — ResolveTransitions skips them
        var transitions = new List<CompletedActivityTransitions>
        {
            new(branch2Entry.ActivityInstanceId, "task2",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ClearUncommittedEvents();
        execution.ResolveTransitions(transitions);

        var events = execution.GetUncommittedEvents();

        // No merge, no spawn — failed entries are skipped
        Assert.AreEqual(0, events.OfType<VariablesMerged>().Count(),
            "No variables should be merged when a branch fails");
        Assert.AreEqual(0, events.OfType<VariableScopesRemoved>().Count(),
            "No scopes should be removed when a branch fails");
        Assert.AreEqual(0, events.OfType<ActivitySpawned>().Count(),
            "No activity should be spawned from a failed branch transition");

        // Branch scopes should still exist (no cleanup)
        Assert.IsNotNull(state.VariableStates.FirstOrDefault(v => v.Id == branchScope1Id));
        Assert.IsNotNull(state.VariableStates.FirstOrDefault(v => v.Id == branchScope2Id));
    }

    // --- Inclusive join variable merge (domain-level) ---

    [TestMethod]
    public void InclusiveJoin_ShouldMergeBranchVariables()
    {
        // Arrange: same fork-join helper but with InclusiveGateway type
        var (execution, state, joinEntry, originalScopeId, _, _) =
            CreateForkJoinWithCompletedBranches(
                setBranch1Vars: d => d["fromInc1"] = "val1",
                setBranch2Vars: d => d["fromInc2"] = "val2");

        // Mark the join entry as InclusiveGateway type
        joinEntry.SetActivityType("InclusiveGateway");

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        // Original scope should have both branch variables — same merge logic as ParallelGateway
        var originalScope = state.GetVariableState(originalScopeId);
        var vars = (IDictionary<string, object?>)originalScope.Variables;
        Assert.AreEqual("val1", vars["fromInc1"]);
        Assert.AreEqual("val2", vars["fromInc2"]);
    }

    // --- Inclusive join partial branches ---

    /// <summary>
    /// Helper: creates a fork-join where only some branches are taken (inclusive gateway behavior).
    /// Branch 1 is completed, branch 2 is never spawned (condition was false).
    /// </summary>
    private static (WorkflowExecution execution, WorkflowInstanceState state,
        ActivityInstanceEntry joinEntry, Guid originalScopeId, Guid branchScope1Id)
        CreateInclusiveForkJoinWithPartialBranches(
            Action<IDictionary<string, object?>>? setBranch1Vars = null)
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

        var originalScopeId = startEntry.VariablesId;

        // Set up fork state with only ONE branch token (inclusive: only one condition was true)
        var forkInstanceId = Guid.NewGuid();
        var parentTokenId = Guid.NewGuid();
        var token1 = Guid.NewGuid();

        var fork = state.CreateGatewayFork(forkInstanceId, parentTokenId);
        fork.CreatedTokenIds.Add(token1);

        // Create cloned variable scope for the single active branch
        var branchScope1Id = Guid.NewGuid();
        state.AddCloneOfVariableState(branchScope1Id, originalScopeId);

        if (setBranch1Vars is not null)
        {
            var branch1Vars = new ExpandoObject();
            setBranch1Vars((IDictionary<string, object?>)branch1Vars);
            state.MergeState(branchScope1Id, branch1Vars);
        }

        // Create fork entry (completed)
        var forkEntry = new ActivityInstanceEntry(forkInstanceId, "fork1", state.Id, null);
        forkEntry.SetActivityType("InclusiveGateway");
        forkEntry.SetVariablesId(originalScopeId);
        forkEntry.Execute();
        forkEntry.Complete();
        state.AddEntries([forkEntry]);

        // Create completed branch entry
        var branch1Entry = new ActivityInstanceEntry(Guid.NewGuid(), "task1", state.Id, null);
        branch1Entry.SetActivityType("ScriptTask");
        branch1Entry.SetVariablesId(branchScope1Id);
        branch1Entry.SetTokenId(token1);
        branch1Entry.Execute();
        branch1Entry.Complete();
        state.AddEntries([branch1Entry]);

        // Create join entry
        var joinEntry = new ActivityInstanceEntry(Guid.NewGuid(), "join1", state.Id, null);
        joinEntry.SetActivityType("InclusiveGateway");
        joinEntry.SetVariablesId(branchScope1Id);
        joinEntry.SetTokenId(token1);
        joinEntry.Execute();
        joinEntry.Complete();
        state.AddEntries([joinEntry]);

        return (execution, state, joinEntry, originalScopeId, branchScope1Id);
    }

    [TestMethod]
    public void InclusiveJoin_PartialBranches_OnlyExecutedBranchesMerged()
    {
        var (execution, state, joinEntry, originalScopeId, branchScope1Id) =
            CreateInclusiveForkJoinWithPartialBranches(
                setBranch1Vars: d => d["onlyBranch"] = "merged");

        var transitions = new List<CompletedActivityTransitions>
        {
            new(joinEntry.ActivityInstanceId, "join1",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(transitions);

        // Only the executed branch's scope should be merged
        var originalScope = state.GetVariableState(originalScopeId);
        var vars = (IDictionary<string, object?>)originalScope.Variables;
        Assert.AreEqual("merged", vars["onlyBranch"]);

        // Branch scope should be removed
        Assert.IsNull(state.VariableStates.FirstOrDefault(v => v.Id == branchScope1Id));

        // Original scope remains
        Assert.IsNotNull(state.VariableStates.FirstOrDefault(v => v.Id == originalScopeId));
    }

    // --- Nested parallel join ---

    [TestMethod]
    public void NestedParallelJoin_ShouldMergeAtEachLevel()
    {
        // Arrange: outer fork → inner fork → two branches → inner join → outer join
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

        var rootScopeId = startEntry.VariablesId;

        // --- Outer fork: token splits into outerToken1 and outerToken2 ---
        var outerForkInstanceId = Guid.NewGuid();
        var rootTokenId = Guid.NewGuid();
        var outerToken1 = Guid.NewGuid();
        var outerToken2 = Guid.NewGuid();

        var outerFork = state.CreateGatewayFork(outerForkInstanceId, rootTokenId);
        outerFork.CreatedTokenIds.Add(outerToken1);
        outerFork.CreatedTokenIds.Add(outerToken2);

        var outerScope1Id = Guid.NewGuid();
        state.AddCloneOfVariableState(outerScope1Id, rootScopeId);
        var outerScope2Id = Guid.NewGuid();
        state.AddCloneOfVariableState(outerScope2Id, rootScopeId);

        // Outer fork entry
        var outerForkEntry = new ActivityInstanceEntry(outerForkInstanceId, "outerFork", state.Id, null);
        outerForkEntry.SetActivityType("ParallelGateway");
        outerForkEntry.SetVariablesId(rootScopeId);
        outerForkEntry.Execute();
        outerForkEntry.Complete();
        state.AddEntries([outerForkEntry]);

        // --- Inner fork on branch 1: outerToken1 splits into innerToken1/innerToken2 ---
        var innerForkInstanceId = Guid.NewGuid();
        var innerToken1 = Guid.NewGuid();
        var innerToken2 = Guid.NewGuid();

        var innerFork = state.CreateGatewayFork(innerForkInstanceId, outerToken1);
        innerFork.CreatedTokenIds.Add(innerToken1);
        innerFork.CreatedTokenIds.Add(innerToken2);

        var innerScope1Id = Guid.NewGuid();
        state.AddCloneOfVariableState(innerScope1Id, outerScope1Id);
        var innerScope2Id = Guid.NewGuid();
        state.AddCloneOfVariableState(innerScope2Id, outerScope1Id);

        var innerForkEntry = new ActivityInstanceEntry(innerForkInstanceId, "innerFork", state.Id, null);
        innerForkEntry.SetActivityType("ParallelGateway");
        innerForkEntry.SetVariablesId(outerScope1Id);
        innerForkEntry.Execute();
        innerForkEntry.Complete();
        state.AddEntries([innerForkEntry]);

        // Inner branch entries (completed with variables)
        var innerBranch1 = new ActivityInstanceEntry(Guid.NewGuid(), "innerTask1", state.Id, null);
        innerBranch1.SetActivityType("ScriptTask");
        innerBranch1.SetVariablesId(innerScope1Id);
        innerBranch1.SetTokenId(innerToken1);
        innerBranch1.Execute();
        innerBranch1.Complete();
        state.AddEntries([innerBranch1]);

        var innerBranch2 = new ActivityInstanceEntry(Guid.NewGuid(), "innerTask2", state.Id, null);
        innerBranch2.SetActivityType("ScriptTask");
        innerBranch2.SetVariablesId(innerScope2Id);
        innerBranch2.SetTokenId(innerToken2);
        innerBranch2.Execute();
        innerBranch2.Complete();
        state.AddEntries([innerBranch2]);

        // Set variables on inner branches
        var inner1Vars = new ExpandoObject();
        ((IDictionary<string, object?>)inner1Vars)["innerVar1"] = "from-inner-1";
        state.MergeState(innerScope1Id, inner1Vars);

        var inner2Vars = new ExpandoObject();
        ((IDictionary<string, object?>)inner2Vars)["innerVar2"] = "from-inner-2";
        state.MergeState(innerScope2Id, inner2Vars);

        // Inner join entry
        var innerJoinEntry = new ActivityInstanceEntry(Guid.NewGuid(), "innerJoin", state.Id, null);
        innerJoinEntry.SetActivityType("ParallelGateway");
        innerJoinEntry.SetVariablesId(innerScope1Id);
        innerJoinEntry.SetTokenId(innerToken1);
        innerJoinEntry.Execute();
        innerJoinEntry.Complete();
        state.AddEntries([innerJoinEntry]);

        // --- Act: Resolve inner join ---
        var innerTransitions = new List<CompletedActivityTransitions>
        {
            new(innerJoinEntry.ActivityInstanceId, "innerJoin",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(innerTransitions);

        // Assert: inner merge happened — outerScope1 now has inner variables
        var outerScope1 = state.GetVariableState(outerScope1Id);
        var outerScope1Vars = (IDictionary<string, object?>)outerScope1.Variables;
        Assert.AreEqual("from-inner-1", outerScope1Vars["innerVar1"]);
        Assert.AreEqual("from-inner-2", outerScope1Vars["innerVar2"]);

        // Inner scopes should be removed
        Assert.IsNull(state.VariableStates.FirstOrDefault(v => v.Id == innerScope1Id));
        Assert.IsNull(state.VariableStates.FirstOrDefault(v => v.Id == innerScope2Id));

        // --- Now set up outer branch 2 completion ---
        var outerBranch2 = new ActivityInstanceEntry(Guid.NewGuid(), "outerTask", state.Id, null);
        outerBranch2.SetActivityType("ScriptTask");
        outerBranch2.SetVariablesId(outerScope2Id);
        outerBranch2.SetTokenId(outerToken2);
        outerBranch2.Execute();
        outerBranch2.Complete();
        state.AddEntries([outerBranch2]);

        var outer2Vars = new ExpandoObject();
        ((IDictionary<string, object?>)outer2Vars)["outerVar"] = "from-outer-2";
        state.MergeState(outerScope2Id, outer2Vars);

        // Need a completed entry for the inner join's spawned activity (on outerToken1)
        // The inner join spawned an activity on outerScope1 with outerToken1 restored
        var postInnerJoinEntry = new ActivityInstanceEntry(Guid.NewGuid(), "postInner", state.Id, null);
        postInnerJoinEntry.SetActivityType("ScriptTask");
        postInnerJoinEntry.SetVariablesId(outerScope1Id);
        postInnerJoinEntry.SetTokenId(outerToken1);
        postInnerJoinEntry.Execute();
        postInnerJoinEntry.Complete();
        state.AddEntries([postInnerJoinEntry]);

        // Outer join entry
        var outerJoinEntry = new ActivityInstanceEntry(Guid.NewGuid(), "outerJoin", state.Id, null);
        outerJoinEntry.SetActivityType("ParallelGateway");
        outerJoinEntry.SetVariablesId(outerScope1Id);
        outerJoinEntry.SetTokenId(outerToken1);
        outerJoinEntry.Execute();
        outerJoinEntry.Complete();
        state.AddEntries([outerJoinEntry]);

        execution.ClearUncommittedEvents();

        // --- Act: Resolve outer join ---
        var outerTransitions = new List<CompletedActivityTransitions>
        {
            new(outerJoinEntry.ActivityInstanceId, "outerJoin",
                [new ActivityTransition(new EndEvent("end1"), Token: TokenAction.RestoreParent)])
        };

        execution.ResolveTransitions(outerTransitions);

        // Assert: root scope has variables from both levels
        var rootScope = state.GetVariableState(rootScopeId);
        var rootVars = (IDictionary<string, object?>)rootScope.Variables;
        Assert.AreEqual("from-inner-1", rootVars["innerVar1"],
            "Inner branch 1 variables should propagate through nested merge");
        Assert.AreEqual("from-inner-2", rootVars["innerVar2"],
            "Inner branch 2 variables should propagate through nested merge");
        Assert.AreEqual("from-outer-2", rootVars["outerVar"],
            "Outer branch 2 variables should be merged");

        // Outer scopes should be removed
        Assert.IsNull(state.VariableStates.FirstOrDefault(v => v.Id == outerScope1Id));
        Assert.IsNull(state.VariableStates.FirstOrDefault(v => v.Id == outerScope2Id));
    }
}
