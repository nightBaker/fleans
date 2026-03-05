using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EnvironmentVariablesTests : WorkflowTestBase
{
    private async Task ClearEnvVariables()
    {
        var envGrain = Cluster.GrainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
        var all = await envGrain.GetAll();
        foreach (var v in all)
            await envGrain.Remove(v.Name);
    }

    [TestMethod]
    public async Task GlobalEnvVar_IsInjectedOnWorkflowStart()
    {
        // Arrange
        await ClearEnvVariables();
        var envGrain = Cluster.GrainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
        await envGrain.Set(new EnvironmentVariableEntry
        {
            Name = "TEST_VAR",
            Value = "hello",
            ValueType = "string",
            ProcessKeys = null
        });

        var workflow = CreateSimpleWorkflow("test-workflow");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);

        // The root variable scope should contain an "Env" key
        var rootScope = snapshot.VariableStates.First();
        Assert.IsTrue(rootScope.Variables.ContainsKey("Env"),
            "Root scope should contain an 'Env' variable after env injection");

        var envValue = rootScope.Variables["Env"];
        Assert.IsTrue(envValue.Contains("TEST_VAR"), "Env should contain TEST_VAR");
        Assert.IsTrue(envValue.Contains("hello"), "Env should contain the value 'hello'");
    }

    [TestMethod]
    public async Task ProcessScopedEnvVar_ExcludedForNonMatchingProcess()
    {
        // Arrange
        await ClearEnvVariables();
        var envGrain = Cluster.GrainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
        await envGrain.Set(new EnvironmentVariableEntry
        {
            Name = "SCOPED_VAR",
            Value = "secret",
            ValueType = "string",
            ProcessKeys = new List<string> { "other-process" }
        });

        var workflow = CreateSimpleWorkflow("my-process");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);

        var rootScope = snapshot.VariableStates.First();
        // Env should either not exist or not contain SCOPED_VAR
        if (rootScope.Variables.TryGetValue("Env", out var envValue))
        {
            Assert.IsFalse(envValue.Contains("SCOPED_VAR"),
                "Env should not contain SCOPED_VAR for a non-matching process");
        }
        // If Env doesn't exist at all, that's also correct
    }

    [TestMethod]
    public async Task Set_WithInvalidEntry_ThrowsArgumentException()
    {
        // Arrange
        var envGrain = Cluster.GrainFactory.GetGrain<IEnvironmentVariablesGrain>(0);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await envGrain.Set(new EnvironmentVariableEntry
            {
                Name = "bad",
                Value = "abc",
                ValueType = "int"
            });
        });
    }

    [TestMethod]
    public async Task SecretEnvVar_TrackedInEnvSecretKeys()
    {
        // Arrange
        await ClearEnvVariables();
        var envGrain = Cluster.GrainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
        await envGrain.Set(new EnvironmentVariableEntry
        {
            Name = "DB_PASSWORD",
            Value = "s3cret",
            ValueType = "string",
            IsSecret = true,
            ProcessKeys = null
        });

        var workflow = CreateSimpleWorkflow("secret-test-workflow");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);

        var rootScope = snapshot.VariableStates.First();
        Assert.IsTrue(rootScope.Variables.ContainsKey("_EnvSecretKeys"),
            "Root scope should contain '_EnvSecretKeys' when a secret env var is injected");

        var secretKeysValue = rootScope.Variables["_EnvSecretKeys"];
        Assert.IsTrue(secretKeysValue.Contains("DB_PASSWORD"),
            "_EnvSecretKeys should contain the secret variable name 'DB_PASSWORD'");
    }

    [TestMethod]
    public async Task ProcessScopedEnvVar_IncludedForMatchingProcess()
    {
        // Arrange
        await ClearEnvVariables();
        var envGrain = Cluster.GrainFactory.GetGrain<IEnvironmentVariablesGrain>(0);
        await envGrain.Set(new EnvironmentVariableEntry
        {
            Name = "PROCESS_VAR",
            Value = "42",
            ValueType = "int",
            ProcessKeys = new List<string> { "matching-process" }
        });

        var workflow = CreateSimpleWorkflow("matching-process");
        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.SetWorkflow(workflow);

        // Act
        await workflowInstance.StartWorkflow();
        await workflowInstance.CompleteActivity("task", new ExpandoObject());

        // Assert
        var instanceId = workflowInstance.GetPrimaryKey();
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsNotNull(snapshot);

        var rootScope = snapshot.VariableStates.First();
        Assert.IsTrue(rootScope.Variables.ContainsKey("Env"),
            "Root scope should contain 'Env' for matching process");

        var envValue = rootScope.Variables["Env"];
        Assert.IsTrue(envValue.Contains("PROCESS_VAR"),
            "Env should contain PROCESS_VAR for matching process");
        Assert.IsTrue(envValue.Contains("42"),
            "Env should contain the typed value 42");
    }

    private static IWorkflowDefinition CreateSimpleWorkflow(string workflowId)
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");

        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = new List<Activity> { start, task, end },
            SequenceFlows = new List<SequenceFlow>
            {
                new SequenceFlow("seq1", start, task),
                new SequenceFlow("seq2", task, end)
            }
        };
    }
}
