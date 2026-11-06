using Fleans.Api.Controllers;
using Fleans.Application.CustomTasks;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Fleans.Api.Tests;

[TestClass]
public class CustomTasksControllerTests
{
    private ICustomTaskCatalogGrain _catalog = null!;
    private CustomTasksController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _catalog = Substitute.For<ICustomTaskCatalogGrain>();
        var grains = Substitute.For<IGrainFactory>();
        grains.GetGrain<ICustomTaskCatalogGrain>(0).Returns(_catalog);
        _controller = new CustomTasksController(grains);
    }

    private static CustomTaskCatalogEntry Private(params string[] allowed) =>
        new("pay", "Pay", null, ["silo"], "Payments", CustomTaskVisibility.Private, [.. allowed]);

    [TestMethod]
    public async Task Get_ReturnsCategoryVisibilityAndAllowList()
    {
        // Arrange
        _catalog.Get("pay").Returns(Private("billing"));

        // Act
        var dto = (await _controller.Get("pay")).Value!;

        // Assert
        Assert.AreEqual("Payments", dto.Category);
        Assert.AreEqual(CustomTaskVisibility.Private, dto.Visibility);
        CollectionAssert.AreEqual(new[] { "billing" }, dto.AllowedProcessKeys.ToList());
    }

    [TestMethod]
    public async Task Get_PublicPlugin_HasAnEmptyAllowList()
    {
        // Arrange
        _catalog.Get("open").Returns(new CustomTaskCatalogEntry("open", null, null, ["silo"]));

        // Act
        var dto = (await _controller.Get("open")).Value!;

        // Assert
        Assert.AreEqual(CustomTaskVisibility.Public, dto.Visibility);
        Assert.IsEmpty(dto.AllowedProcessKeys);
    }

    [TestMethod]
    public async Task SetAllowedProcesses_PrivatePlugin_ReturnsTheNewEntry()
    {
        // Arrange
        _catalog.Get("pay").Returns(Private("billing"));
        _catalog.SetAllowedProcesses("pay", Arg.Any<List<string>>()).Returns(Private("shop"));

        // Act
        var result = await _controller.SetAllowedProcesses("pay", new SetAllowedProcessesRequest(["shop"]));

        // Assert
        CollectionAssert.AreEqual(new[] { "shop" }, result.Value!.AllowedProcessKeys.ToList());
        await _catalog.Received(1).SetAllowedProcesses("pay", Arg.Is<List<string>>(k => k.Single() == "shop"));
    }

    [TestMethod]
    public async Task SetAllowedProcesses_UnknownTaskType_Returns404()
    {
        // Arrange
        _catalog.Get("nope").Returns((CustomTaskCatalogEntry?)null);

        // Act
        var result = await _controller.SetAllowedProcesses("nope", new SetAllowedProcessesRequest(["shop"]));

        // Assert
        Assert.IsInstanceOfType<NotFoundResult>(result.Result);
    }

    [TestMethod]
    public async Task SetAllowedProcesses_PublicPlugin_PropagatesConflict()
    {
        // Arrange — GlobalExceptionHandler answers 409 to this
        _catalog.Get("open").Returns(new CustomTaskCatalogEntry("open", null, null, ["silo"]));
        _catalog.SetAllowedProcesses("open", Arg.Any<List<string>>()).Throws(new InvalidOperationException("not private"));

        // Act + Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _controller.SetAllowedProcesses("open", new SetAllowedProcessesRequest(["shop"])));
    }
}
