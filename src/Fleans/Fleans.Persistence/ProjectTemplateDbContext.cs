using Fleans.Domain;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Persistence;

public class FleansDbContext : DbContext
{
    public FleansDbContext(DbContextOptions<FleansDbContext> options)
        : base(options)
    {

    }

    public DbSet<WorkflowDefinition> WorkflowDefinitions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

    }
}
