using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Fleans.Persistence.Sqlite;

/// <summary>
/// SQLite-specific model tweaks applied on top of the shared Fleans model.
///
/// SQLite does not support <see cref="DateTimeOffset"/> in <c>ORDER BY</c>. To keep Sieve
/// sorting working against SQLite, we store every <see cref="DateTimeOffset"/>
/// (and <see cref="Nullable{DateTimeOffset}"/>) property as an ISO 8601 string.
/// PostgreSQL and other providers store them natively, so this conversion lives in the
/// SQLite provider package rather than the shared model.
/// </summary>
internal sealed class SqliteModelCustomizer : RelationalModelCustomizer
{
    private static readonly ValueConverter<DateTimeOffset?, string?> NullableDtoToStringConverter =
        new(
            v => v.HasValue ? v.Value.ToString("O") : null,
            v => v != null ? DateTimeOffset.Parse(v) : null);

    private static readonly ValueConverter<DateTimeOffset, string> DtoToStringConverter =
        new(
            v => v.ToString("O"),
            v => DateTimeOffset.Parse(v));

    public SqliteModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        // The journal model has none of the entities below; configuring them would add them.
        if (context is FleanJournalDbContext)
            return;

        // WorkflowInstanceState — three nullable DateTimeOffset columns
        modelBuilder.Entity<WorkflowInstanceState>()
            .Property(e => e.CreatedAt)
            .HasConversion(NullableDtoToStringConverter);

        modelBuilder.Entity<WorkflowInstanceState>()
            .Property(e => e.ExecutionStartedAt)
            .HasConversion(NullableDtoToStringConverter);

        modelBuilder.Entity<WorkflowInstanceState>()
            .Property(e => e.CompletedAt)
            .HasConversion(NullableDtoToStringConverter);

        // UserTaskState.CreatedAt — non-nullable DateTimeOffset
        modelBuilder.Entity<UserTaskState>()
            .Property(e => e.CreatedAt)
            .HasConversion(DtoToStringConverter);

        // DeploymentRequestState — one non-nullable and two nullable DateTimeOffset columns
        modelBuilder.Entity<DeploymentRequestState>()
            .Property(e => e.RequestedAt)
            .HasConversion(DtoToStringConverter);

        modelBuilder.Entity<DeploymentRequestState>()
            .Property(e => e.ClosedAt)
            .HasConversion(NullableDtoToStringConverter);

        modelBuilder.Entity<DeploymentRequestState>()
            .Property(e => e.DeployedAt)
            .HasConversion(NullableDtoToStringConverter);

        // ProcessDefinition.DeployedAt — non-nullable DateTimeOffset
        modelBuilder.Entity<ProcessDefinition>()
            .Property(e => e.DeployedAt)
            .HasConversion(DtoToStringConverter);
    }
}
