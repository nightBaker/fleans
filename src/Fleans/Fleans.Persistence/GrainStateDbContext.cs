using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

public class GrainStateDbContext : DbContext
{
    public DbSet<ActivityInstanceState> ActivityInstances => Set<ActivityInstanceState>();

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
    }
}
