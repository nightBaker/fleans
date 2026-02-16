using System.Dynamic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Fleans.Persistence;

internal static class FleanModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
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

            entity.Property(e => e.ErrorMessage)
                .HasMaxLength(2000);

            entity.Ignore(e => e.ErrorState);
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

            entity.Property(e => e.ProcessDefinitionId).HasMaxLength(512);

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

        modelBuilder.Entity<ProcessDefinition>(entity =>
        {
            entity.ToTable("ProcessDefinitions");
            entity.HasKey(e => e.ProcessDefinitionId);

            entity.Property(e => e.ProcessDefinitionId).HasMaxLength(512);
            entity.Property(e => e.ProcessDefinitionKey).HasMaxLength(256);
            entity.Property(e => e.ETag).HasMaxLength(64);
            entity.Property(e => e.BpmnXml);

            entity.HasIndex(e => e.ProcessDefinitionKey);

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
    }
}

/// <summary>
/// Restricts TypeNameHandling deserialization to types from the Fleans.Domain assembly only.
/// </summary>
internal sealed class DomainAssemblySerializationBinder : DefaultSerializationBinder
{
    private static readonly Assembly DomainAssembly = typeof(WorkflowDefinition).Assembly;

    public override Type BindToType(string? assemblyName, string typeName)
    {
        var type = base.BindToType(assemblyName, typeName);
        if (type.Assembly != DomainAssembly)
            throw new JsonSerializationException(
                $"Deserialization of type '{type.FullName}' from assembly '{type.Assembly.FullName}' is not allowed. " +
                $"Only types from '{DomainAssembly.GetName().Name}' are permitted.");
        return type;
    }
}
