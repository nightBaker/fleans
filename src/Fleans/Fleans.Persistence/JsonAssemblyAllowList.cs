using System.Reflection;

namespace Fleans.Persistence;

/// <summary>
/// Allowlist of BCL assemblies whose types are permitted through Newtonsoft.Json's
/// <c>TypeNameHandling.Auto</c> path. Used by both serialization binders in this
/// project (<see cref="Events.EventStoreSerializationBinder"/> and
/// <see cref="DomainAssemblySerializationBinder"/>) to keep the allow check in one
/// place and to avoid the historical "any assembly whose simple name starts with
/// 'System'" pattern — which would let <c>System.Diagnostics.Process</c> and other
/// classic Newtonsoft-gadget types through.
///
/// The list is intentionally narrow: only the BCL assemblies that are actually
/// reachable from domain events / workflow state on .NET 10. Adding new entries
/// here is a security-sensitive change; prefer keeping domain types inside
/// Fleans.Domain.
/// </summary>
internal static class JsonAssemblyAllowList
{
    /// <summary>
    /// Names of BCL assemblies whose types may appear in TypeNameHandling.Auto
    /// payloads. Comparison is ordinal — match the runtime's <c>AssemblyName.Name</c>
    /// exactly, including capitalisation.
    /// </summary>
    private static readonly HashSet<string> AllowedBclAssemblies = new(StringComparer.Ordinal)
    {
        // BCL core: primitives, Guid, DateTime, DateTimeOffset, List<T>, Dictionary<,>,
        // arrays, Tuple, etc. on .NET Core 3+ and .NET 5+.
        "System.Private.CoreLib",
        // Legacy fallbacks — preserved for compatibility with old payloads written
        // against .NET Framework / .NET Standard. Modern runs use System.Private.CoreLib.
        "mscorlib",
        "netstandard",
        // ExpandoObject + DynamicObject backing types — workflow variables ship here.
        "System.Linq.Expressions",
        // Collection types not folded into CoreLib: NameValueCollection, etc.
        "System.Collections",
        "System.Collections.Specialized",
        // Runtime-resolved name of typeof(object).Assembly — captured at first use to
        // catch any rename in future .NET versions without re-deploying the allowlist.
        typeof(object).Assembly.GetName().Name ?? "System.Private.CoreLib",
    };

    /// <summary>Returns true iff <paramref name="assembly"/> is one of the allowlisted BCL assemblies.</summary>
    public static bool IsAllowedBclAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return name is not null && AllowedBclAssemblies.Contains(name);
    }
}
