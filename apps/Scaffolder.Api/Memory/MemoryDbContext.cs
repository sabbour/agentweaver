using Microsoft.EntityFrameworkCore;
using Scaffolder.Api.Runs;

namespace Scaffolder.Api.Memory;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options)
{
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<DecisionInboxEntry> DecisionInbox => Set<DecisionInboxEntry>();
    public DbSet<AgentMemory> AgentMemory => Set<AgentMemory>();
    public DbSet<SessionContext> SessionContexts => Set<SessionContext>();
    public DbSet<RunEventRecord> RunEvents => Set<RunEventRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Decision>().HasIndex(d => new { d.ProjectId, d.Status });
        model.Entity<Decision>().HasIndex(d => new { d.ProjectId, d.AgentName });
        model.Entity<DecisionInboxEntry>().HasIndex(e => new { e.ProjectId, e.Status });
        model.Entity<DecisionInboxEntry>().HasIndex(e => new { e.ProjectId, e.AgentName, e.Slug }).IsUnique();
        model.Entity<AgentMemory>().HasIndex(m => new { m.ProjectId, m.AgentName });
        model.Entity<AgentMemory>().HasIndex(m => new { m.ProjectId, m.Type });
        model.Entity<SessionContext>().HasIndex(s => new { s.ProjectId, s.EndedAt });
        model.Entity<SessionContext>().HasIndex(s => new { s.ProjectId, s.SessionId }).IsUnique();
        model.Entity<RunEventRecord>().HasIndex(e => e.RunId);
        model.Entity<RunEventRecord>().HasIndex(e => new { e.RunId, e.Sequence }).IsUnique();
    }
}
