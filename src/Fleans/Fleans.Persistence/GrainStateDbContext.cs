using System.Dynamic;
using Fleans.Domain.States;
using Fleans.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Fleans.Persistence;

public class GrainStateDbContext : DbContext
{
    public DbSet<ActivityInstanceState> ActivityInstances => Set<ActivityInstanceState>();
    public DbSet<WorkflowInstanceEntity> WorkflowInstances => Set<WorkflowInstanceEntity>();
    public DbSet<ActivityInstanceEntryEntity> WorkflowActivityInstanceEntries => Set<ActivityInstanceEntryEntity>();
    public DbSet<WorkflowVariablesEntity> WorkflowVariableStates => Set<WorkflowVariablesEntity>();
    public DbSet<ConditionSequenceEntity> WorkflowConditionSequenceStates => Set<ConditionSequenceEntity>();

    public GrainStateDbContext(DbContextOptions<GrainStateDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActivityInstanceState>(entity =>
        {
            entity.ToTable("ActivityInstances");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ETag)
                .HasMaxLength(64);

            entity.Property(e => e.ActivityId)
                .HasMaxLength(256);

            entity.Property(e => e.ActivityType)
                .HasMaxLength(256);

            entity.OwnsOne(e => e.ErrorState, error =>
            {
                error.Property(e => e.Code).HasColumnName("ErrorCode");
                error.Property(e => e.Message).HasColumnName("ErrorMessage").HasMaxLength(2000);
            });
        });

        modelBuilder.Entity<WorkflowInstanceEntity>(entity =>
        {
            entity.ToTable("WorkflowInstances");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.ETag)
                .HasMaxLength(64);

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

        modelBuilder.Entity<ActivityInstanceEntryEntity>(entity =>
        {
            entity.ToTable("WorkflowActivityInstanceEntries");
            entity.HasKey(e => e.ActivityInstanceId);

            entity.Property(e => e.ActivityId)
                .HasMaxLength(256);
        });

        modelBuilder.Entity<WorkflowVariablesEntity>(entity =>
        {
            entity.ToTable("WorkflowVariableStates");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Variables)
                .HasColumnName("Variables")
                .HasConversion(
                    v => JsonConvert.SerializeObject(v),
                    v => JsonConvert.DeserializeObject<ExpandoObject>(v) ?? new ExpandoObject());
        });

        modelBuilder.Entity<ConditionSequenceEntity>(entity =>
        {
            entity.ToTable("WorkflowConditionSequenceStates");
            entity.HasKey(e => new { e.GatewayActivityInstanceId, e.ConditionalSequenceFlowId });

            entity.Property(e => e.ConditionalSequenceFlowId)
                .HasMaxLength(256);
        });
    }
}
