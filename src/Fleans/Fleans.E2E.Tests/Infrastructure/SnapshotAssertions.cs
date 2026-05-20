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
        foreach (var scope in snapshot.VariableStates)
        {
            if (scope.Variables.TryGetValue(name, out var v))
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
        foreach (var scope in snapshot.VariableStates)
        {
            if (scope.Variables.TryGetValue(name, out var v))
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
        var actual = snapshot.GetVariable(name);
        Assert.AreEqual(expected, actual, $"Variable '{name}' mismatch.");
    }
}
