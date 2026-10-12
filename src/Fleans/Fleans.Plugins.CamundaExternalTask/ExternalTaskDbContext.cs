using Microsoft.EntityFrameworkCore;

namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// The plugin's own table — independent of the engine's persistence. The host chooses the
/// provider (SQLite, PostgreSQL, …) in <c>AddCamundaExternalTasks(options =&gt; …)</c>.
/// </summary>
public sealed class ExternalTaskDbContext(DbContextOptions<ExternalTaskDbContext> options) : DbContext(options)
{
    public const string TableName = "camunda_external_tasks";

    public DbSet<ExternalTaskEntity> ExternalTasks => Set<ExternalTaskEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<ExternalTaskEntity>();
        task.ToTable(TableName);
        task.HasKey(t => t.Id);
        task.Property(t => t.Id).HasMaxLength(64);
        task.Property(t => t.TopicName).HasMaxLength(256);
        task.Property(t => t.ProcessDefinitionKey).HasMaxLength(256);
        task.Property(t => t.ProcessDefinitionId).HasMaxLength(256);
        task.Property(t => t.ActivityId).HasMaxLength(256);
        task.Property(t => t.WorkerId).HasMaxLength(256);
        task.HasIndex(t => new { t.TopicName, t.LockExpirationTime });
        task.HasIndex(t => t.WorkflowInstanceId);
    }
}
