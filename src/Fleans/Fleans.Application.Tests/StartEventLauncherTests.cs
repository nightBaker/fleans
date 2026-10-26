using System.Dynamic;
using Fleans.Application.Conditions;
using Fleans.Application.Grains;
using Fleans.Application.StartEvents;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleans.Application.Tests;

/// <summary>
/// Conditional starts read each process's startable definition once per evaluation, however many
/// conditional start events the process has (#858).
/// </summary>
[TestClass]
public class StartEventLauncherTests
{
    [TestMethod]
    public async Task StartByConditions_ReadsEachProcessDefinitionOnce()
    {
        // Arrange — two conditional starts in "orders", one in "billing", every condition false
        var grainFactory = Substitute.For<IGrainFactory>();
        var registry = Substitute.For<IConditionalStartEventRegistryGrain>();
        var notifier = Substitute.For<IStartEventSubscriptionNotifierGrain>();
        var evaluator = Substitute.For<IConditionExpressionEvaluatorGrain>();
        var orders = Substitute.For<IProcessDefinitionGrain>();
        var billing = Substitute.For<IProcessDefinitionGrain>();
        grainFactory.GetGrain<IConditionalStartEventRegistryGrain>(0, Arg.Any<string?>()).Returns(registry);
        grainFactory.GetGrain<IStartEventSubscriptionNotifierGrain>(0, Arg.Any<string?>()).Returns(notifier);
        grainFactory.GetGrain<IConditionExpressionEvaluatorGrain>(Arg.Any<long>(), Arg.Any<string?>()).Returns(evaluator);
        grainFactory.GetGrain<IProcessDefinitionGrain>("orders", Arg.Any<string?>()).Returns(orders);
        grainFactory.GetGrain<IProcessDefinitionGrain>("billing", Arg.Any<string?>()).Returns(billing);
        notifier.Subscribe(Arg.Any<IStartEventSubscriptionObserver>()).Returns(Task.FromResult(true));
        registry.GetAll().Returns(ValueTask.FromResult(new List<ConditionalStartEntry>
        {
            new("orders", "a", "x > 1"),
            new("orders", "b", "x > 2"),
            new("billing", "c", "x > 3"),
        }));
        orders.GetStartableDefinition().Returns(Task.FromResult<IWorkflowDefinition?>(Definition("orders", "a", "b")));
        billing.GetStartableDefinition().Returns(Task.FromResult<IWorkflowDefinition?>(Definition("billing", "c")));
        evaluator.Evaluate(Arg.Any<string>(), Arg.Any<ExpandoObject>()).Returns(Task.FromResult(false));

        var launcher = new StartEventLauncher(grainFactory,
            new StartEventSubscriptionCache(grainFactory, TimeProvider.System, NullLogger<StartEventSubscriptionCache>.Instance),
            Options.Create(new WorkerEvaluatorOptions()),
            NullLogger<StartEventLauncher>.Instance);

        // Act
        var result = await launcher.StartByConditions(workflowId: null, new ExpandoObject());

        // Assert
        Assert.AreEqual(0, result.StartedInstanceIds.Count);
        await orders.Received(1).GetStartableDefinition();
        await billing.Received(1).GetStartableDefinition();
        await evaluator.Received(3).Evaluate(Arg.Any<string>(), Arg.Any<ExpandoObject>());
    }

    private static WorkflowDefinition Definition(string workflowId, params string[] conditionalStartIds) => new()
    {
        WorkflowId = workflowId,
        Activities = conditionalStartIds.Select(id => (Activity)new ConditionalStartEvent(id, "x > 0")).ToList(),
        SequenceFlows = [],
    };
}
