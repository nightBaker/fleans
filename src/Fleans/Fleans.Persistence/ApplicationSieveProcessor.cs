using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Options;
using Sieve.Models;
using Sieve.Services;

namespace Fleans.Persistence;

public class ApplicationSieveProcessor : SieveProcessor
{
    public ApplicationSieveProcessor(IOptions<SieveOptions> options) : base(options) { }

    protected override SievePropertyMapper MapProperties(SievePropertyMapper mapper)
    {
        mapper.Property<WorkflowInstanceState>(w => w.CreatedAt)
            .CanFilter().CanSort();
        mapper.Property<WorkflowInstanceState>(w => w.IsStarted)
            .CanFilter();
        mapper.Property<WorkflowInstanceState>(w => w.IsCompleted)
            .CanFilter();
        mapper.Property<WorkflowInstanceState>(w => w.IsCancelled)
            .CanFilter();
        mapper.Property<WorkflowInstanceState>(w => w.CompletedAt)
            .CanSort();
        mapper.Property<WorkflowInstanceState>(w => w.ExecutionStartedAt)
            .CanSort();

        mapper.Property<ProcessDefinition>(p => p.ProcessDefinitionKey)
            .CanFilter().CanSort();
        mapper.Property<ProcessDefinition>(p => p.Version)
            .CanFilter().CanSort();
        mapper.Property<ProcessDefinition>(p => p.DeployedAt)
            .CanSort();
        mapper.Property<ProcessDefinition>(p => p.IsActive)
            .CanFilter();

        mapper.Property<UserTaskState>(t => t.CreatedAt)
            .CanFilter().CanSort();
        mapper.Property<UserTaskState>(t => t.TaskState)
            .CanFilter();

        return mapper;
    }
}
