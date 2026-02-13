using System.Dynamic;

namespace Fleans.Persistence.Entities;

public class WorkflowVariablesEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowInstanceId { get; set; }
    public ExpandoObject Variables { get; set; } = new();
}
