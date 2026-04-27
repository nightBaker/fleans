using System.Dynamic;

namespace Fleans.Application.CustomTasks;

/// <summary>
/// Contract for a plugin that backs a <c>&lt;serviceTask type="..."&gt;</c>. Each plugin defines
/// its own grain interface inheriting from <see cref="ICustomTaskCallProvider"/>; the framework
/// resolves the concrete grain via Orleans' <c>GetGrain(Type, Guid)</c> using the activity
/// instance id as the grain key. The <paramref name="resolved"/> dictionary carries inputs
/// (set by the framework before the call) and outputs (the provider mutates it during the call,
/// typically by writing <c>resolved["__response"]</c>).
/// </summary>
public interface ICustomTaskCallProvider : IGrainWithGuidKey
{
    Task ExecuteAsync(IDictionary<string, object?> resolved, ExpandoObject variables);
}
