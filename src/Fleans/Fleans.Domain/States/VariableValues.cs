using System.Collections;

namespace Fleans.Domain.States;

/// <summary>
/// Structural equality for workflow variable values, used to journal only the variables a merge
/// changes (#867).
/// </summary>
internal static class VariableValues
{
    /// <summary>
    /// True when replacing <paramref name="a"/> with <paramref name="b"/> changes nothing: both null,
    /// or the same runtime type and equal — dictionaries (<see cref="System.Dynamic.ExpandoObject"/>)
    /// by their entries, lists by their items, anything else by <see cref="object.Equals(object)"/>.
    /// Errs towards "different": <c>1</c> and <c>1L</c> differ, and so does an unknown reference type
    /// that is not the same instance — the value is then journaled, which is merely larger.
    /// </summary>
    public static bool AreEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.GetType() != b.GetType()) return false;

        switch (a)
        {
            case IDictionary<string, object?> left:
                var right = (IDictionary<string, object?>)b;
                if (left.Count != right.Count) return false;
                foreach (var (key, value) in left)
                {
                    if (!right.TryGetValue(key, out var other) || !AreEqual(value, other))
                        return false;
                }
                return true;

            case IList leftList:
                var rightList = (IList)b;
                if (leftList.Count != rightList.Count) return false;
                for (var i = 0; i < leftList.Count; i++)
                {
                    if (!AreEqual(leftList[i], rightList[i]))
                        return false;
                }
                return true;

            default:
                return a.Equals(b);
        }
    }
}
