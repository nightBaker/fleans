using Fleans.Application.Placement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Worker.Placement;

/// <summary>
/// Silo-startup assertion (#457) that fails fast on Fleans:Role / placement-attribute
/// mismatch. Scans loaded assemblies for grain implementations carrying
/// <see cref="WorkerPlacementAttribute"/> or <see cref="CorePlacementAttribute"/> and
/// verifies each is hosted on a silo whose role matches.
///
/// Other <c>PlacementAttribute</c> subclasses (Orleans built-ins, custom placement
/// strategies) are ignored — only the two Fleans attributes are checked.
/// </summary>
public sealed partial class PlacementRoleAssertion : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IConfiguration _configuration;
    private readonly ILocalSiloDetails _siloDetails;
    private readonly ILogger<PlacementRoleAssertion> _logger;

    public PlacementRoleAssertion(
        IConfiguration configuration,
        ILocalSiloDetails siloDetails,
        ILogger<PlacementRoleAssertion> logger)
    {
        _configuration = configuration;
        _siloDetails = siloDetails;
        _logger = logger;
    }

    public void Participate(ISiloLifecycle observer) =>
        observer.Subscribe(
            nameof(PlacementRoleAssertion),
            ServiceLifecycleStage.RuntimeInitialize,
            OnStart);

    private Task OnStart(CancellationToken ct)
    {
        var role = _configuration["Fleans:Role"] ?? "Combined";
        var siloName = _siloDetails.Name;
        var grains = DiscoverPlacementGrains(AppDomain.CurrentDomain.GetAssemblies());
        var checkedCount = AssertPlacements(siloName, role, grains, LogViolation);
        LogAssertionRan(role, siloName, checkedCount);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Testable seam: validates every grain in <paramref name="grains"/> against the given
    /// <paramref name="siloName"/>. Throws <see cref="InvalidOperationException"/> with the
    /// verbatim AC2 message on the first violation. Returns the number of grains checked.
    /// </summary>
    public static int AssertPlacements(
        string siloName,
        string role,
        IEnumerable<(Type Type, PlacementKind Kind)> grains,
        Action<string, string, string, string>? onViolation = null)
    {
        var checkedCount = 0;
        foreach (var (type, kind) in grains)
        {
            checkedCount++;
            if (kind == PlacementKind.Worker && !WorkerPlacementDirector.HasWorkerRole(siloName))
            {
                onViolation?.Invoke(type.FullName ?? type.Name, "[WorkerPlacement]", role, "Worker or Combined");
                throw new InvalidOperationException(
                    $"Plugin grain '{type.FullName}' carries [WorkerPlacement] but the current silo's\n" +
                    $"Fleans:Role is '{role}'. Plugin grains can only run on silos with role 'Worker' or\n" +
                    $"'Combined'. Either configure 'Fleans:Role=Worker' on this silo, or remove the\n" +
                    $"plugin's DI registration from this silo's host.");
            }
            if (kind == PlacementKind.Core && !CorePlacementDirector.HasCoreRole(siloName))
            {
                onViolation?.Invoke(type.FullName ?? type.Name, "[CorePlacement]", role, "Core or Combined");
                throw new InvalidOperationException(
                    $"Plugin grain '{type.FullName}' carries [CorePlacement] but the current silo's\n" +
                    $"Fleans:Role is '{role}'. Plugin grains can only run on silos with role 'Core' or\n" +
                    $"'Combined'. Either configure 'Fleans:Role=Core' on this silo, or remove the\n" +
                    $"plugin's DI registration from this silo's host.");
            }
        }
        return checkedCount;
    }

    /// <summary>
    /// Scans the given assemblies for grain implementations carrying either the
    /// <see cref="WorkerPlacementAttribute"/> or the <see cref="CorePlacementAttribute"/>.
    /// Other <c>PlacementAttribute</c> subclasses are not inspected.
    /// </summary>
    public static IEnumerable<(Type Type, PlacementKind Kind)> DiscoverPlacementGrains(
        IEnumerable<System.Reflection.Assembly> assemblies)
    {
        foreach (var asm in assemblies)
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            catch { continue; }

            foreach (var t in types)
            {
                if (t is null || t.IsAbstract || t.IsInterface) continue;
                if (!typeof(IGrain).IsAssignableFrom(t)) continue;

                if (t.GetCustomAttributes(typeof(WorkerPlacementAttribute), inherit: false).Length > 0)
                    yield return (t, PlacementKind.Worker);
                else if (t.GetCustomAttributes(typeof(CorePlacementAttribute), inherit: false).Length > 0)
                    yield return (t, PlacementKind.Core);
            }
        }
    }

    public enum PlacementKind { Worker, Core }

    [LoggerMessage(EventId = 11200, Level = LogLevel.Information,
        Message = "PlacementRoleAssertion: Fleans:Role='{Role}' on silo '{SiloName}' — checked {Count} placement-attributed grain(s); no violations.")]
    private partial void LogAssertionRan(string role, string siloName, int count);

    [LoggerMessage(EventId = 11201, Level = LogLevel.Error,
        Message = "PlacementRoleAssertion VIOLATION: grain '{GrainType}' carries {Attribute} but Fleans:Role='{Role}' (requires {Expected}).")]
    private partial void LogViolation(string grainType, string attribute, string role, string expected);
}
