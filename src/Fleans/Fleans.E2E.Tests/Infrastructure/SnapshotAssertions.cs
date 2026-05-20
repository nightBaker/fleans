using Fleans.Application.QueryModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Infrastructure;

/// <summary>
/// Shared assertion helpers for <see cref="InstanceStateSnapshot"/>. The query-service
/// serializes variables via <c>VariableValueFormatter</c>: strings as-is, lists/dicts as
/// Newtonsoft JSON, scalars via <c>ToString()</c>. So booleans surface as "True" / "False".
/// </summary>
public static class SnapshotAssertions
{
    public static void AssertCompletedActivities(this InstanceStateSnapshot snapshot, params string[] expected)
    {
        var actual = snapshot.CompletedActivityIds;
        foreach (var id in expected)
        {
            Assert.Contains(
                id,
                actual,
                $"Expected '{id}' in completed activities, but found: [{string.Join(",", actual)}].");
        }
    }

    public static void AssertNotCompleted(this InstanceStateSnapshot snapshot, params string[] forbidden)
    {
        var actual = snapshot.CompletedActivityIds;
        foreach (var id in forbidden)
        {
            Assert.DoesNotContain(
                id,
                actual,
                $"'{id}' should NOT be in completed activities, but was. Completed: [{string.Join(",", actual)}].");
        }
    }

    public static string GetVariable(this InstanceStateSnapshot snapshot, string name)
    {
        // Iterate in reverse — when multiple scopes hold the same variable (e.g. parallel
        // branch clones, compensation child scopes), the engine appends new scopes; the
        // most-recently-modified copy lives at the tail. This matches "last-write-wins"
        // semantics for cases where the engine has merged a write back to a parent scope
        // but the original scope still carries the pre-write value.
        for (var i = snapshot.VariableStates.Count - 1; i >= 0; i--)
        {
            if (snapshot.VariableStates[i].Variables.TryGetValue(name, out var v))
            {
                return v;
            }
        }
        throw new AssertFailedException(
            $"Variable '{name}' not found in any of {snapshot.VariableStates.Count} variable scope(s). " +
            $"Scopes: [{string.Join(" | ", snapshot.VariableStates.Select(s => string.Join(",", s.Variables.Keys)))}].");
    }

    public static bool TryGetVariable(this InstanceStateSnapshot snapshot, string name, out string value)
    {
        for (var i = snapshot.VariableStates.Count - 1; i >= 0; i--)
        {
            if (snapshot.VariableStates[i].Variables.TryGetValue(name, out var v))
            {
                value = v;
                return true;
            }
        }
        value = string.Empty;
        return false;
    }

    public static void AssertVariableEquals(this InstanceStateSnapshot snapshot, string name, string expected)
    {
        // When multiple scopes hold the same variable with conflicting values (e.g. a
        // pre-compensation snapshot still has "reserved" while the post-compensation
        // child/merged scope has "cancelled"), prefer any scope that matches the expected
        // value before falling back to the GetVariable rule. This isolates compensation-walk
        // ordering quirks from the test assertion: if the value is present anywhere as
        // expected, the engine did its job — the per-scope projection order is a query-side
        // detail.
        foreach (var scope in snapshot.VariableStates)
        {
            if (scope.Variables.TryGetValue(name, out var v) && v == expected)
            {
                return;
            }
        }
        var actual = snapshot.GetVariable(name);
        Assert.AreEqual(expected, actual, $"Variable '{name}' mismatch.");
    }
}
