using System.Dynamic;

namespace Fleans.Application.Scripts;

public interface IScriptExecutorGrain : IGrainWithIntegerKey
{
    Task<ExpandoObject> Execute(string script, ExpandoObject variables);
}
