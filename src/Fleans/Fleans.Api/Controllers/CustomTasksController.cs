using Fleans.Application.CustomTasks;
using Fleans.ServiceDefaults.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Fleans.Api.Controllers;

/// <summary>
/// Surfaces the <see cref="ICustomTaskCatalogGrain"/> contents to the management UI.
/// </summary>
[ApiController]
[Route("custom-tasks")]
public sealed class CustomTasksController : ControllerBase
{
    private readonly IGrainFactory _grainFactory;

    public CustomTasksController(IGrainFactory grainFactory)
    {
        _grainFactory = grainFactory;
    }

    /// <summary>Returns one entry per registered task type with the silos hosting it.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<CustomTaskCatalogEntryDto>> GetAll()
    {
        var catalog = _grainFactory.GetGrain<ICustomTaskCatalogGrain>(0);
        var entries = await catalog.GetAll();
        return entries.Select(CustomTaskCatalogMappers.ToDto).ToList();
    }

    /// <summary>Returns the entry for one task type, 404 if no plugin claims it.</summary>
    [HttpGet("{taskType}")]
    public async Task<ActionResult<CustomTaskCatalogEntryDto>> Get(string taskType)
    {
        var catalog = _grainFactory.GetGrain<ICustomTaskCatalogGrain>(0);
        var entry = await catalog.Get(taskType);
        if (entry is null)
            return NotFound();
        return entry.ToDto();
    }
}

internal static class CustomTaskCatalogMappers
{
    public static CustomTaskCatalogEntryDto ToDto(this CustomTaskCatalogEntry entry) =>
        new(entry.TaskType, entry.DisplayName, entry.ParameterSchema?.ToDto(), entry.SiloNames);

    public static CustomTaskParameterSchemaDto ToDto(this CustomTaskParameterSchema schema) =>
        new(schema.Parameters.Select(p => p.ToDto()).ToList());

    public static CustomTaskParameterSpecDto ToDto(this CustomTaskParameterSpec spec) =>
        new(spec.Name, spec.DisplayName, spec.Type, spec.Required,
            spec.Description, spec.DefaultValue, spec.ItemType);
}
