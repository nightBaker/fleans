using System.Collections;
using System.Dynamic;
using System.Reflection;
using Fleans.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

namespace Fleans.Persistence.Tests;

/// <summary>
/// The journal stores events with the Orleans serializer (#867). An event type without a codec, or
/// a member whose runtime type has none, would only fail when an instance first journals it — so
/// every <see cref="WorkflowEvent"/> type is round-tripped here, filled with sample values.
/// </summary>
[TestClass]
public class JournalEventSerializationTests
{
    private static readonly Serializer Serializer =
        new ServiceCollection().AddSerializer().BuildServiceProvider().GetRequiredService<Serializer>();

    private static readonly Assembly DomainAssembly = typeof(WorkflowEvent).Assembly;

    public static IEnumerable<object[]> EventTypes() =>
        DomainAssembly.GetTypes()
            .Where(t => typeof(WorkflowEvent).IsAssignableFrom(t) && !t.IsAbstract)
            .OrderBy(t => t.Name)
            .Select(t => new object[] { t });

    [TestMethod]
    [DynamicData(nameof(EventTypes))]
    public void EveryEventType_RoundTripsThroughTheJournalSerializer(Type eventType)
    {
        // Arrange
        var original = (WorkflowEvent)Sample(eventType, depth: 0)!
            with { OccurredAt = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero) };

        // Act
        var bytes = Serializer.SerializeToArray(new List<WorkflowEvent> { original });
        var loaded = Serializer.Deserialize<List<WorkflowEvent>>(bytes).Single();

        // Assert — same type and members: serializing it again yields the same bytes
        Assert.AreEqual(eventType, loaded.GetType());
        Assert.AreEqual(original.OccurredAt, loaded.OccurredAt);
        CollectionAssert.AreEqual(bytes, Serializer.SerializeToArray(new List<WorkflowEvent> { loaded }));
    }

    [TestMethod]
    public void EveryEventType_HasAUniqueShortAlias()
    {
        var aliases = EventTypes()
            .Select(row => (Type)row[0])
            .Select(t => t.GetCustomAttribute<AliasAttribute>()?.Alias)
            .ToList();

        Assert.IsTrue(aliases.All(a => a is not null && a.StartsWith("ev.", StringComparison.Ordinal)),
            "every journaled event needs an [Alias(\"ev.<Name>\")]");
        Assert.HasCount(aliases.Count, aliases.Distinct());
    }

    private static object? Sample(Type type, int depth)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return "s";
        if (underlying == typeof(Guid)) return Guid.NewGuid();
        if (underlying == typeof(int)) return 3;
        if (underlying == typeof(long)) return 3L;
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(DateTimeOffset)) return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (underlying == typeof(TimeSpan)) return TimeSpan.FromMinutes(1);
        if (underlying.IsEnum) return Enum.GetValues(underlying).GetValue(Enum.GetValues(underlying).Length - 1);
        if (underlying == typeof(ExpandoObject))
        {
            var vars = new ExpandoObject();
            var dict = (IDictionary<string, object?>)vars;
            dict["amount"] = 449L;
            dict["items"] = new List<object> { "i1", 2L };
            return vars;
        }
        if (underlying.IsArray)
        {
            var array = Array.CreateInstance(underlying.GetElementType()!, 1);
            array.SetValue(Sample(underlying.GetElementType()!, depth + 1), 0);
            return array;
        }
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (IList)Activator.CreateInstance(underlying)!;
            list.Add(Sample(underlying.GetGenericArguments()[0], depth + 1));
            return list;
        }
        if (depth > 4)
            return null;
        if (underlying.IsAbstract)
        {
            var concrete = DomainAssembly.GetTypes()
                .First(t => underlying.IsAssignableFrom(t) && !t.IsAbstract && t.IsPublic);
            return Sample(concrete, depth + 1);
        }

        var ctor = underlying.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => Sample(p.ParameterType, depth + 1)).ToArray();
        return ctor.Invoke(args);
    }
}
