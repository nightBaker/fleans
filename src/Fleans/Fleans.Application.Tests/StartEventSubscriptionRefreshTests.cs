using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Application.StartEvents;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Orleans.TestingHost.InProcess;

namespace Fleans.Application.Tests;

/// <summary>
/// Every silo caches which processes a start event starts (#858). A deploy, disable or enable
/// refreshes the cache of every silo before it returns, so the next start on any silo sees it.
/// Each test first warms the cache of every silo, so a missed refresh would show as a stale start.
/// </summary>
[TestClass]
public class StartEventSubscriptionRefreshTests : WorkflowTestBase
{
    [TestMethod]
    public async Task Deploy_NewProcessForACachedMessageName_StartsItOnEverySilo()
    {
        // Arrange
        var messageName = $"refresh-msg-{Guid.NewGuid():N}";
        await Deploy(MessageStartWorkflow("refresh-msg-a", messageName));
        var silos = LaunchersOfEverySilo();
        foreach (var launcher in silos)
            Assert.HasCount(1, await launcher.StartByMessage(messageName, new ExpandoObject()));

        // Act
        await Deploy(MessageStartWorkflow("refresh-msg-b", messageName));

        // Assert
        foreach (var launcher in silos)
            Assert.HasCount(2, await launcher.StartByMessage(messageName, new ExpandoObject()));
    }

    [TestMethod]
    public async Task Disable_ThenEnable_StopsAndResumesSignalStartsOnEverySilo()
    {
        // Arrange
        var signalName = $"refresh-sig-{Guid.NewGuid():N}";
        await Deploy(SignalStartWorkflow("refresh-sig", signalName));
        var silos = LaunchersOfEverySilo();
        foreach (var launcher in silos)
            Assert.HasCount(1, await launcher.StartBySignal(signalName));
        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("refresh-sig");

        // Act + Assert — disabled
        await processGrain.Disable();
        foreach (var launcher in silos)
            Assert.IsEmpty(await launcher.StartBySignal(signalName));

        // Act + Assert — enabled again
        await processGrain.Enable();
        foreach (var launcher in silos)
            Assert.HasCount(1, await launcher.StartBySignal(signalName));
    }

    [TestMethod]
    public async Task Redeploy_WithoutTheMessageStart_StartsNothingOnEverySilo()
    {
        // Arrange
        var messageName = $"refresh-drop-{Guid.NewGuid():N}";
        await Deploy(MessageStartWorkflow("refresh-drop", messageName));
        var silos = LaunchersOfEverySilo();
        foreach (var launcher in silos)
            Assert.HasCount(1, await launcher.StartByMessage(messageName, new ExpandoObject()));

        // Act
        await Deploy(MessageStartWorkflow("refresh-drop", $"other-{messageName}"));

        // Assert
        foreach (var launcher in silos)
            Assert.IsEmpty(await launcher.StartByMessage(messageName, new ExpandoObject()));
    }

    [TestMethod]
    public async Task Deploy_NewConditionalProcess_IsEvaluatedOnEverySilo()
    {
        // Arrange
        await Deploy(ConditionalStartWorkflow("refresh-cond-a", "true"));
        var silos = LaunchersOfEverySilo();
        foreach (var launcher in silos)
            Assert.HasCount(1, (await launcher.StartByConditions(null, new ExpandoObject())).StartedInstanceIds);

        // Act
        await Deploy(ConditionalStartWorkflow("refresh-cond-b", "true"));

        // Assert
        foreach (var launcher in silos)
            Assert.HasCount(2, (await launcher.StartByConditions(null, new ExpandoObject())).StartedInstanceIds);
    }

    [TestMethod]
    public async Task Redeploy_WithAnotherCondition_EvaluatesTheNewConditionOnEverySilo()
    {
        // Arrange
        await Deploy(ConditionalStartWorkflow("refresh-cond-change", "true"));
        var silos = LaunchersOfEverySilo();
        foreach (var launcher in silos)
            Assert.HasCount(1, (await launcher.StartByConditions(null, new ExpandoObject())).StartedInstanceIds);

        // Act
        await Deploy(ConditionalStartWorkflow("refresh-cond-change", "false"));

        // Assert
        foreach (var launcher in silos)
        {
            var result = await launcher.StartByConditions(null, new ExpandoObject());
            Assert.IsEmpty(result.StartedInstanceIds);
            Assert.IsNull(result.Errors);
        }
    }

    private List<StartEventLauncher> LaunchersOfEverySilo()
    {
        var launchers = Cluster.Silos
            .Select(s => ((InProcessSiloHandle)s).SiloHost.Services.GetRequiredService<StartEventLauncher>())
            .ToList();
        Assert.IsGreaterThan(1, launchers.Count, "The refresh needs more than one silo to be tested.");
        return launchers;
    }

    private async Task Deploy(WorkflowDefinition workflow) =>
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId)
            .DeployVersion(workflow, "<placeholder/>");

    private static WorkflowDefinition MessageStartWorkflow(string processId, string messageName)
    {
        var start = new MessageStartEvent("msgStart", "msg1");
        var task = new TaskActivity("task1");
        return new WorkflowDefinition
        {
            WorkflowId = processId,
            Activities = [start, task],
            SequenceFlows = [new SequenceFlow("f1", start, task)],
            Messages = [new MessageDefinition("msg1", messageName, null)]
        };
    }

    private static WorkflowDefinition SignalStartWorkflow(string processId, string signalName)
    {
        var start = new SignalStartEvent("sigStart", "sig1");
        var task = new TaskActivity("task1");
        return new WorkflowDefinition
        {
            WorkflowId = processId,
            Activities = [start, task],
            SequenceFlows = [new SequenceFlow("f1", start, task)],
            Signals = [new SignalDefinition("sig1", signalName)]
        };
    }

    private static WorkflowDefinition ConditionalStartWorkflow(string processId, string condition)
    {
        var start = new ConditionalStartEvent("condStart", condition);
        var task = new TaskActivity("task1");
        return new WorkflowDefinition
        {
            WorkflowId = processId,
            Activities = [start, task],
            SequenceFlows = [new SequenceFlow("f1", start, task)]
        };
    }
}
