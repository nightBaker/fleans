using Fleans.Application.Grains;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Fleans.Application.Tests;

[TestClass]
public class WorkflowInstanceOptionsTests
{
    private static ServiceProvider Build(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddApplication();
        return services.BuildServiceProvider();
    }

    private static TimeSpan CollectionAgeOfWorkflowInstance(ServiceProvider sp) =>
        sp.GetRequiredService<IOptions<GrainCollectionOptions>>().Value
            .ClassSpecificCollectionAge[typeof(WorkflowInstance).FullName!];

    [TestMethod]
    public void By_default_an_idle_workflow_instance_is_collected_after_two_minutes()
    {
        // Arrange
        using var sp = Build(new());

        // Act
        var age = CollectionAgeOfWorkflowInstance(sp);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(2), age);
    }

    [TestMethod]
    public void IdleCollectionAge_comes_from_configuration()
    {
        // Arrange
        using var sp = Build(new() { ["Fleans:Instances:IdleCollectionAge"] = "00:05:00" });

        // Act
        var age = CollectionAgeOfWorkflowInstance(sp);

        // Assert
        Assert.AreEqual(TimeSpan.FromMinutes(5), age);
    }

    [TestMethod]
    public void An_age_within_the_collection_quantum_is_rejected()
    {
        // Arrange
        using var sp = Build(new() { ["Fleans:Instances:IdleCollectionAge"] = "00:00:30" });

        // Act & Assert
        Assert.ThrowsExactly<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<WorkflowInstanceOptions>>().Value);
    }
}
