using Microsoft.EntityFrameworkCore;
using Scaffolder.Api.Persistence.Entities;

namespace Scaffolder.Api.Persistence;

public class ScaffolderDbContext : DbContext
{
    public ScaffolderDbContext(DbContextOptions<ScaffolderDbContext> options)
        : base(options)
    {
    }

    public DbSet<RunEntity> Runs => Set<RunEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();
    public DbSet<ToolOperationEntity> ToolOperations => Set<ToolOperationEntity>();
    public DbSet<ReviewDecisionEntity> ReviewDecisions => Set<ReviewDecisionEntity>();
    public DbSet<OperationalRecordEntity> OperationalRecords => Set<OperationalRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ScaffolderDbContext).Assembly);
    }
}
