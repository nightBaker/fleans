using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orleans;

namespace Fleans.Application.Tests;

[TestClass]
public class WorkflowDefinitionCacheTests
{
    private const string Key = "order";
    private const string DefinitionId = "order:1:20261023T101010.0000000Z";

    private IGrainFactory _grainFactory = null!;
    private IProcessDefinitionGrain _processGrain = null!;
    private WorkflowDefinitionCache _cache = null!;

    [TestInitialize]
    public void Setup()
    {
        _grainFactory = Substitute.For<IGrainFactory>();
        _processGrain = Substitute.For<IProcessDefinitionGrain>();
        _grainFactory.GetGrain<IProcessDefinitionGrain>(Key).Returns(_processGrain);
        _cache = new WorkflowDefinitionCache(_grainFactory, NullLogger<WorkflowDefinitionCache>.Instance);
    }

    [TestMethod]
    public async Task GetAsync_SecondCall_ReturnsSameInstance_WithoutAnotherGrainCall()
    {
        // Arrange
        var definition = CreateDefinition();
        _processGrain.GetDefinitionById(DefinitionId).Returns(definition);

        // Act
        var first = await _cache.GetAsync(DefinitionId);
        var second = await _cache.GetAsync(DefinitionId);

        // Assert
        Assert.AreSame(definition, first);
        Assert.AreSame(first, second);
        await _processGrain.Received(1).GetDefinitionById(DefinitionId);
    }

    [TestMethod]
    public async Task GetAsync_ConcurrentMisses_ShareOneGrainCall()
    {
        // Arrange
        var load = new TaskCompletionSource<WorkflowDefinition>(TaskCreationOptions.RunContinuationsAsynchronously);
        _processGrain.GetDefinitionById(DefinitionId).Returns(load.Task);

        // Act
        var callers = Enumerable.Range(0, 10).Select(_ => _cache.GetAsync(DefinitionId).AsTask()).ToList();
        var definition = CreateDefinition();
        load.SetResult(definition);
        var results = await Task.WhenAll(callers);

        // Assert
        Assert.IsTrue(results.All(r => ReferenceEquals(r, definition)));
        await _processGrain.Received(1).GetDefinitionById(DefinitionId);
    }

    [TestMethod]
    public async Task GetAsync_FailedLoad_IsNotCached()
    {
        // Arrange
        var definition = CreateDefinition();
        _processGrain.GetDefinitionById(DefinitionId).Returns(
            _ => Task.FromException<WorkflowDefinition>(new KeyNotFoundException("not deployed")),
            _ => Task.FromResult(definition));

        // Act
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => _cache.GetAsync(DefinitionId).AsTask());
        var retried = await _cache.GetAsync(DefinitionId);

        // Assert
        Assert.AreSame(definition, retried);
        await _processGrain.Received(2).GetDefinitionById(DefinitionId);
    }

    [TestMethod]
    public async Task GetAsync_DifferentVersions_AreSeparateEntries()
    {
        // Arrange
        const string v2 = "order:2:20261023T111111.0000000Z";
        var first = CreateDefinition();
        var second = CreateDefinition() with { ProcessDefinitionId = v2 };
        _processGrain.GetDefinitionById(DefinitionId).Returns(first);
        _processGrain.GetDefinitionById(v2).Returns(second);

        // Act + Assert
        Assert.AreSame(first, await _cache.GetAsync(DefinitionId));
        Assert.AreSame(second, await _cache.GetAsync(v2));
    }

    private static WorkflowDefinition CreateDefinition()
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = Key,
            ProcessDefinitionId = DefinitionId,
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("f1", start, end)]
        };
    }
}
