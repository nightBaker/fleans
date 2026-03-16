using System.Dynamic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Fleans.Domain;
using Fleans.Domain.States;
using Fleans.Persistence.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Fleans.Persistence;

internal static class FleanModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
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

            entity.Property(e => e.ProcessDefinitionId).HasMaxLength(512);

            // UserTasks is an in-memory dictionary rehydrated from the UserTasks table on activation.
            entity.Ignore(e => e.UserTasks);

            entity.HasMany(e => e.TimerCycleTracking)
                .WithOne()
                .HasForeignKey(e => e.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<ProcessDefinition>()
                .WithMany()
                .HasForeignKey(e => e.ProcessDefinitionId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ActivityInstanceEntry>(entity =>
        {
            entity.ToTable("WorkflowActivityInstanceEntries");
            entity.HasKey(e => e.ActivityInstanceId);

            entity.Property(e => e.ActivityId).HasMaxLength(256);
            entity.Property(e => e.ActivityType).HasMaxLength(256);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.CancellationReason).HasMaxLength(2000);
            entity.Ignore(e => e.ErrorState);
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

        modelBuilder.Entity<GatewayForkState>(entity =>
        {
            entity.ToTable("GatewayForks");
            entity.HasKey(e => e.ForkInstanceId);

            entity.Property(e => e.CreatedTokenIds)
                .HasConversion(
                    v => JsonConvert.SerializeObject(v),
                    v => JsonConvert.DeserializeObject<List<Guid>>(v) ?? new List<Guid>());
        });

        modelBuilder.Entity<WorkflowInstanceState>(entity =>
        {
            entity.HasMany(e => e.GatewayForks)
                .WithOne()
                .HasForeignKey(e => e.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TimerCycleTrackingState>(entity =>
        {
            entity.ToTable("TimerCycleTracking");
            entity.HasKey(e => new { e.HostActivityInstanceId, e.TimerActivityId });

            entity.Property(e => e.TimerActivityId).HasMaxLength(256);
            entity.Property(e => e.TimerExpression).HasMaxLength(512);
        });

        modelBuilder.Entity<TimerStartEventSchedulerState>(entity =>
        {
            entity.ToTable("TimerSchedulers");
            entity.HasKey(e => e.Key);

            entity.Property(e => e.Key).HasMaxLength(256);
            entity.Property(e => e.ETag).HasMaxLength(64);
            entity.Property(e => e.ProcessDefinitionId).HasMaxLength(512);
        });

        modelBuilder.Entity<MessageCorrelationState>(entity =>
        {
            entity.ToTable("MessageCorrelations");
            entity.HasKey(e => e.Key);

            entity.Property(e => e.Key).HasMaxLength(1024);
            entity.Property(e => e.ETag).HasMaxLength(64);

            entity.HasOne(e => e.Subscription)
                .WithOne()
                .HasForeignKey<MessageSubscription>(s => s.MessageName)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageSubscription>(sub =>
        {
            sub.ToTable("MessageSubscriptions");
            sub.HasKey(s => s.MessageName);
            sub.Property(s => s.MessageName).HasMaxLength(1024);
            sub.Property(s => s.CorrelationKey).HasMaxLength(512);
            sub.Property(s => s.ActivityId).HasMaxLength(256);
        });

        modelBuilder.Entity<SignalCorrelationState>(entity =>
        {
            entity.ToTable("SignalCorrelations");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(512);
            entity.Property(e => e.ETag).HasMaxLength(64);

            entity.HasMany(e => e.Subscriptions)
                .WithOne()
                .HasForeignKey(s => s.SignalName)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SignalSubscription>(sub =>
        {
            sub.ToTable("SignalSubscriptions");
            sub.HasKey(s => new { s.SignalName, s.WorkflowInstanceId, s.ActivityId });
            sub.Property(s => s.SignalName).HasMaxLength(512);
            sub.Property(s => s.ActivityId).HasMaxLength(256);
        });

        // BREAKING CHANGE: MessageStartEventListenerState persistence migrated from JSON
        // ProcessDefinitionKeys column to a separate MessageStartEventRegistrations join table.
        // Existing databases will lose message start event registrations on upgrade.
        // After upgrading, re-deploy all workflows with message start events to re-register.
        modelBuilder.Entity<MessageStartEventListenerState>(entity =>
        {
            entity.ToTable("MessageStartEventListeners");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(512);
            entity.Property(e => e.ETag).HasMaxLength(64);
            entity.Ignore(e => e.ProcessDefinitionKeys);
        });

        modelBuilder.Entity<SignalStartEventListenerState>(entity =>
        {
            entity.ToTable("SignalStartEventListeners");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(512);
            entity.Property(e => e.ETag).HasMaxLength(64);
            entity.Ignore(e => e.ProcessDefinitionKeys);
        });

        modelBuilder.Entity<MessageStartEventRegistration>(entity =>
        {
            entity.ToTable("MessageStartEventRegistrations");
            entity.HasKey(e => new { e.MessageName, e.ProcessDefinitionKey });
            entity.Property(e => e.MessageName).HasMaxLength(512);
            entity.Property(e => e.ProcessDefinitionKey).HasMaxLength(256);
        });

        modelBuilder.Entity<SignalStartEventRegistration>(entity =>
        {
            entity.ToTable("SignalStartEventRegistrations");
            entity.HasKey(e => new { e.SignalName, e.ProcessDefinitionKey });
            entity.Property(e => e.SignalName).HasMaxLength(512);
            entity.Property(e => e.ProcessDefinitionKey).HasMaxLength(256);
        });

        modelBuilder.Entity<UserTaskState>(entity =>
        {
            entity.ToTable("UserTasks");
            entity.HasKey(e => e.ActivityInstanceId);

            entity.Property(e => e.ActivityId).HasMaxLength(256);
            entity.Property(e => e.Assignee).HasMaxLength(256);
            entity.Property(e => e.ClaimedBy).HasMaxLength(256);
            entity.Property(e => e.ETag).HasMaxLength(64);

            entity.Property(e => e.CandidateGroups)
                .HasConversion(
                    v => JsonConvert.SerializeObject(v),
                    v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>());

            entity.Property(e => e.CandidateUsers)
                .HasConversion(
                    v => JsonConvert.SerializeObject(v),
                    v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>());

            entity.Property(e => e.ExpectedOutputVariables)
                .HasConversion(
                    v => v == null ? null : JsonConvert.SerializeObject(v),
                    v => v == null ? null : JsonConvert.DeserializeObject<List<string>>(v));

            entity.HasOne<WorkflowInstanceState>()
                .WithMany()
                .HasForeignKey(e => e.WorkflowInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.WorkflowInstanceId);
        });

        modelBuilder.Entity<ProcessDefinition>(entity =>
        {
            entity.ToTable("ProcessDefinitions");
            entity.HasKey(e => e.ProcessDefinitionId);

            entity.Property(e => e.ProcessDefinitionId).HasMaxLength(512);
            entity.Property(e => e.ProcessDefinitionKey).HasMaxLength(256);
            entity.Property(e => e.ETag).HasMaxLength(64);
            entity.Property(e => e.BpmnXml);

            entity.HasIndex(e => e.ProcessDefinitionKey);

            entity.Property(e => e.IsActive).HasDefaultValue(true);

            var jsonSettings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                PreserveReferencesHandling = PreserveReferencesHandling.Objects,
                SerializationBinder = new DomainAssemblySerializationBinder()
            };

            entity.Property(e => e.Workflow)
                .HasColumnName("Workflow")
                .HasConversion(
                    v => JsonConvert.SerializeObject(v, jsonSettings),
                    v => JsonConvert.DeserializeObject<WorkflowDefinition>(v, jsonSettings)!)
                .Metadata.SetValueComparer(
                    new ValueComparer<WorkflowDefinition>(
                        (a, b) => JsonConvert.SerializeObject(a, jsonSettings) ==
                                  JsonConvert.SerializeObject(b, jsonSettings),
                        v => JsonConvert.SerializeObject(v, jsonSettings).GetHashCode(),
                        v => JsonConvert.DeserializeObject<WorkflowDefinition>(
                            JsonConvert.SerializeObject(v, jsonSettings), jsonSettings)!));
        });

        modelBuilder.Entity<WorkflowEventEntity>(entity =>
        {
            entity.ToTable("WorkflowEvents");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.GrainId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.EventType).HasMaxLength(128).IsRequired();

            entity.HasIndex(e => new { e.GrainId, e.Version }).IsUnique();
        });

        modelBuilder.Entity<WorkflowSnapshotEntity>(entity =>
        {
            entity.ToTable("WorkflowSnapshots");
            entity.HasKey(e => e.GrainId);

            entity.Property(e => e.GrainId).HasMaxLength(256);
        });
    }
}

/// <summary>
/// Restricts TypeNameHandling deserialization to types from the Fleans.Domain assembly
/// and BCL/system assemblies (e.g. System.Collections.Generic.List&lt;string&gt;).
/// </summary>
internal sealed class DomainAssemblySerializationBinder : DefaultSerializationBinder
{
    private static readonly Assembly DomainAssembly = typeof(WorkflowDefinition).Assembly;

    public override Type BindToType(string? assemblyName, string typeName)
    {
        var type = base.BindToType(assemblyName, typeName);
        if (type.Assembly != DomainAssembly && !IsSystemAssembly(type.Assembly))
            throw new JsonSerializationException(
                $"Deserialization of type '{type.FullName}' from assembly '{type.Assembly.FullName}' is not allowed. " +
                $"Only types from '{DomainAssembly.GetName().Name}' and system assemblies are permitted.");
        return type;
    }

    private static bool IsSystemAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return name != null && (name.StartsWith("System.", StringComparison.Ordinal)
            || name is "System" or "mscorlib" or "netstandard"
            || name == typeof(object).Assembly.GetName().Name);
    }
}
