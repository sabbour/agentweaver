using Microsoft.EntityFrameworkCore;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Auth.OAuth;

namespace Agentweaver.Api.Memory;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options)
{
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<DecisionInboxEntry> DecisionInbox => Set<DecisionInboxEntry>();
    public DbSet<AgentMemory> AgentMemory => Set<AgentMemory>();
    public DbSet<SessionContext> SessionContexts => Set<SessionContext>();
    public DbSet<RunEventRecord> RunEvents => Set<RunEventRecord>();
    public DbSet<OutcomeSpec> OutcomeSpecs => Set<OutcomeSpec>();
    public DbSet<WorkPlan> WorkPlans => Set<WorkPlan>();
    public DbSet<Subtask> Subtasks => Set<Subtask>();
    public DbSet<SubtaskDependency> SubtaskDependencies => Set<SubtaskDependency>();
    public DbSet<SteeringDirective> SteeringDirectives => Set<SteeringDirective>();
    public DbSet<McpRefreshToken> McpRefreshTokens => Set<McpRefreshToken>();
    public DbSet<McpRevokedJti> McpRevokedJtis => Set<McpRevokedJti>();
    public DbSet<McpClientRegistration> McpClientRegistrations => Set<McpClientRegistration>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Decision>().HasIndex(d => new { d.ProjectId, d.Status });
        model.Entity<Decision>().HasIndex(d => new { d.ProjectId, d.AgentName });
        model.Entity<Decision>()
            .HasOne<Decision>()
            .WithMany()
            .HasForeignKey(d => d.SupersededById)
            .IsRequired(false);
        model.Entity<DecisionInboxEntry>().HasIndex(e => new { e.ProjectId, e.Status });
        model.Entity<DecisionInboxEntry>().HasIndex(e => new { e.ProjectId, e.Slug }).IsUnique();
        model.Entity<DecisionInboxEntry>()
            .HasOne<Decision>()
            .WithMany()
            .HasForeignKey(e => e.DecisionId)
            .IsRequired(false);
        model.Entity<AgentMemory>().HasIndex(m => new { m.ProjectId, m.AgentName });
        model.Entity<AgentMemory>().HasIndex(m => new { m.ProjectId, m.Type });
        model.Entity<SessionContext>().HasIndex(s => new { s.ProjectId, s.EndedAt });
        model.Entity<SessionContext>().HasIndex(s => new { s.ProjectId, s.SessionId }).IsUnique();
        model.Entity<RunEventRecord>().HasIndex(e => e.RunId);
        model.Entity<RunEventRecord>().HasIndex(e => new { e.RunId, e.Sequence }).IsUnique();
        model.Entity<OutcomeSpec>().HasIndex(o => new { o.ProjectId, o.CoordinatorRunId });

        model.Entity<WorkPlan>().HasIndex(w => w.CoordinatorRunId);
        model.Entity<WorkPlan>()
            .HasOne<OutcomeSpec>()
            .WithMany()
            .HasForeignKey(w => w.OutcomeSpecId)
            .OnDelete(DeleteBehavior.Cascade);

        model.Entity<Subtask>().HasIndex(s => s.WorkPlanId);
        model.Entity<Subtask>()
            .HasOne<WorkPlan>()
            .WithMany()
            .HasForeignKey(s => s.WorkPlanId)
            .OnDelete(DeleteBehavior.Cascade);

        model.Entity<SubtaskDependency>().HasIndex(d => d.SubtaskId);
        model.Entity<SubtaskDependency>()
            .HasOne<Subtask>()
            .WithMany()
            .HasForeignKey(d => d.SubtaskId)
            .OnDelete(DeleteBehavior.Cascade);
        model.Entity<SubtaskDependency>()
            .HasOne<Subtask>()
            .WithMany()
            .HasForeignKey(d => d.DependsOnSubtaskId)
            .OnDelete(DeleteBehavior.Restrict);

        model.Entity<SteeringDirective>().HasIndex(s => new { s.CoordinatorRunId, s.Status });

        model.Entity<McpRefreshToken>().HasIndex(t => t.TokenHash).IsUnique();
        model.Entity<McpRefreshToken>().HasIndex(t => t.ChainId);
        model.Entity<McpRefreshToken>().HasIndex(t => new { t.Subject, t.ClientId });
        model.Entity<McpRevokedJti>().HasIndex(j => j.Jti).IsUnique();
        model.Entity<McpRevokedJti>().HasIndex(j => j.ExpiresAt);
        model.Entity<McpClientRegistration>().HasIndex(c => c.ClientId).IsUnique();
    }
}
