using Fleans.Application.Events.Handlers;
using Fleans.Domain.Events;
using Fleans.Worker.CustomTasks;
using Orleans.Streams;

namespace Fleans.Application.Tests;

/// <summary>
/// Every silo that registers the stream provider — external plugin hosts included — owns a
/// slice of the stream queues and must deserialize whatever lands on them. Plugin hosts only
/// carry Fleans.Worker's dependency closure, so an event type carried on a stream must live
/// in Fleans.Domain.Abstractions; one left in Fleans.Domain is silently dropped by the queue
/// receiver on a plugin host and the instance hangs (#866).
/// </summary>
[TestClass]
public class StreamEventAssemblyTests
{
    [TestMethod]
    public void StreamSubscriberEventTypes_LiveInDomainAbstractions()
    {
        // Arrange
        var subscribers = new[] { typeof(WorkflowExecuteScriptEventHandler).Assembly, typeof(CustomTaskHandlerBase).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(Grain).IsAssignableFrom(t))
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IAsyncObserver<>))
                .Select(i => (Grain: t, Event: i.GetGenericArguments()[0])))
            .ToList();

        // Act
        var misplaced = subscribers
            .Where(s => s.Event.Assembly != typeof(IDomainEvent).Assembly)
            .Select(s => $"{s.Grain.Name} observes {s.Event.FullName} from {s.Event.Assembly.GetName().Name}")
            .ToList();

        // Assert
        CollectionAssert.IsSubsetOf(
            new[] { typeof(ExecuteScriptEvent), typeof(EvaluateConditionEvent), typeof(EvaluateActivationConditionEvent),
                    typeof(EvaluateCompletionConditionEvent), typeof(ExecuteCustomTaskEvent) },
            subscribers.Select(s => s.Event).Distinct().ToList(),
            "The scan must see every stream subscriber; did a handler stop implementing IAsyncObserver<T>?");
        Assert.IsEmpty(misplaced,
            "Stream event types must live in Fleans.Domain.Abstractions:\n" + string.Join("\n", misplaced));
    }
}
