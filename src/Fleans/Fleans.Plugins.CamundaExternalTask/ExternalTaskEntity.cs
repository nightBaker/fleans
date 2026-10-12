namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// One row per waiting Camunda-style external task. Written by
/// <see cref="CamundaExternalTaskHandler"/> when the engine publishes the custom-task event,
/// locked by <c>fetchAndLock</c>, and deleted once the task is completed or failed back into
/// the workflow. <see cref="Id"/> is the activity instance id, so a redelivered stream event
/// maps onto the same row.
/// </summary>
public sealed class ExternalTaskEntity
{
    public required string Id { get; set; }
    public required string TopicName { get; set; }
    public required Guid WorkflowInstanceId { get; set; }
    public required string ProcessDefinitionKey { get; set; }
    public string? ProcessDefinitionId { get; set; }
    public required string ActivityId { get; set; }
    public required Guid ActivityInstanceId { get; set; }
    public required Guid VariablesId { get; set; }

    /// <summary>JSON array of <c>{source,target}</c> input mappings from the BPMN.</summary>
    public required string InputMappingsJson { get; set; }

    /// <summary>JSON array of <c>{source,target}</c> output mappings from the BPMN.</summary>
    public required string OutputMappingsJson { get; set; }

    public string? WorkerId { get; set; }

    /// <summary>UTC. Fetchable when null or in the past.</summary>
    public DateTime? LockExpirationTime { get; set; }

    /// <summary>Null until a worker reports a failure (Camunda semantics).</summary>
    public int? Retries { get; set; }

    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }
    public long Priority { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreateTime { get; set; }
}
