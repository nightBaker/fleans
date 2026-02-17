using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

public class FleanCommandDbContext : DbContext
{
    public DbSet<ActivityInstanceState> ActivityInstances => Set<ActivityInstanceState>();
    public DbSet<WorkflowInstanceState> WorkflowInstances => Set<WorkflowInstanceState>();
    public DbSet<ActivityInstanceEntry> WorkflowActivityInstanceEntries => Set<ActivityInstanceEntry>();
    public DbSet<WorkflowVariablesState> WorkflowVariableStates => Set<WorkflowVariablesState>();
    public DbSet<ConditionSequenceState> WorkflowConditionSequenceStates => Set<ConditionSequenceState>();
    public DbSet<ProcessDefinition> ProcessDefinitions => Set<ProcessDefinition>();
    public DbSet<TimerStartEventSchedulerState> TimerSchedulers => Set<TimerStartEventSchedulerState>();

    public FleanCommandDbContext(DbContextOptions<FleanCommandDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        FleanModelConfiguration.Configure(modelBuilder);
    }
}
