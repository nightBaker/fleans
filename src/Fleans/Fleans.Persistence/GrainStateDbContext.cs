using System.Dynamic;
using Fleans.Domain.States;
using Fleans.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Fleans.Persistence;

public class GrainStateDbContext : DbContext
{
    public DbSet<ActivityInstanceEntity> ActivityInstances => Set<ActivityInstanceEntity>();
    public DbSet<WorkflowInstanceState> WorkflowInstances => Set<WorkflowInstanceState>();
    public DbSet<ActivityInstanceEntry> WorkflowActivityInstanceEntries => Set<ActivityInstanceEntry>();
    public DbSet<WorkflowVariablesState> WorkflowVariableStates => Set<WorkflowVariablesState>();
    public DbSet<ConditionSequenceState> WorkflowConditionSequenceStates => Set<ConditionSequenceState>();

    public GrainStateDbContext(DbContextOptions<GrainStateDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActivityInstanceEntity>(entity =>
        {
            entity.ToTable("ActivityInstances");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ETag)
                .HasMaxLength(64);

            entity.Property(e => e.ActivityId)
                .HasMaxLength(256);

            entity.Property(e => e.ActivityType)
                .HasMaxLength(256);

            entity.Property(e => e.ErrorMessage)
                .HasMaxLength(2000);
        });

        modelBuilder.Entity<WorkflowInstanceState>(entity =>
        {
            entity.ToTable("WorkflowInstances");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ETag).HasMaxLength(64);

            entity.HasMany(e => e.Entries)
                .WithOne()
                .HasForeignKey(e => e.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.VariableStates)
                .WithOne()
                .HasForeignKey(e => e.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ConditionSequenceStates)
                .WithOne()
                .HasForeignKey(e => e.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ActivityInstanceEntry>(entity =>
        {
            entity.ToTable("WorkflowActivityInstanceEntries");
            entity.HasKey(e => e.ActivityInstanceId);

            entity.Property(e => e.ActivityId).HasMaxLength(256);
        });

        modelBuilder.Entity<WorkflowVariablesState>(entity =>
        {
            entity.ToTable("WorkflowVariableStates");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Variables)
                .HasColumnName("Variables")
                .HasConversion(
                    v => JsonConvert.SerializeObject(v),
                    v => JsonConvert.DeserializeObject<ExpandoObject>(v) ?? new ExpandoObject());
        });

        modelBuilder.Entity<ConditionSequenceState>(entity =>
        {
            entity.ToTable("WorkflowConditionSequenceStates");
            entity.HasKey(e => new { e.GatewayActivityInstanceId, e.ConditionalSequenceFlowId });

            entity.Property(e => e.ConditionalSequenceFlowId).HasMaxLength(256);
        });
    }
}
