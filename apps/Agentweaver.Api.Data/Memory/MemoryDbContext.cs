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
    public DbSet<McpPendingAuthorization> McpPendingAuthorizations => Set<McpPendingAuthorization>();
    public DbSet<McpAuthorizationCode> McpAuthorizationCodes => Set<McpAuthorizationCode>();

    // Entities migrated from agentweaver.db (spec-018 P2)
    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<RunRevisionRecord> RunRevisions => Set<RunRevisionRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<BacklogTaskRecord> BacklogTasks => Set<BacklogTaskRecord>();
    public DbSet<WorkflowRunRecord> WorkflowRuns => Set<WorkflowRunRecord>();
    public DbSet<CastProposalRecord> CastProposals => Set<CastProposalRecord>();

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
        model.Entity<McpPendingAuthorization>().HasIndex(p => p.State).IsUnique();
        model.Entity<McpPendingAuthorization>().HasIndex(p => p.ExpiresAt);
        model.Entity<McpAuthorizationCode>().HasIndex(c => c.Code).IsUnique();
        model.Entity<McpAuthorizationCode>().HasIndex(c => c.ExpiresAt);

        // ── agentweaver.db entities (spec-018 P2) ──────────────────────────────────
        // These entities only exist in the Postgres schema (InitialPostgres migration).
        // For SQLite, explicitly ignore them so the SQLite migration snapshot stays unchanged
        // and EF does not report pending model changes for the memory.db migrations.
        if (!Database.IsNpgsql())
        {
            model.Ignore<RunRecord>();
            model.Ignore<RunRevisionRecord>();
            model.Ignore<ProjectRecord>();
            model.Ignore<BacklogTaskRecord>();
            model.Ignore<WorkflowRunRecord>();
            model.Ignore<CastProposalRecord>();
            return;
        }

        // Explicit snake_case column name mappings so EF uses the same column names as the
        // existing SQLite agentweaver.db schema and the Postgres InitialPostgres migration.
        model.Entity<RunRecord>(e =>
        {
            e.ToTable("runs").HasKey(r => r.RunId);
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.RepositoryPath).HasColumnName("repository_path");
            e.Property(r => r.OriginatingBranch).HasColumnName("originating_branch");
            e.Property(r => r.ModelSource).HasColumnName("model_source");
            e.Property(r => r.Task).HasColumnName("task");
            e.Property(r => r.SubmittingUser).HasColumnName("submitting_user");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.StartedAt).HasColumnName("started_at");
            e.Property(r => r.EndedAt).HasColumnName("ended_at");
            e.Property(r => r.Result).HasColumnName("result");
            e.Property(r => r.WorktreePath).HasColumnName("worktree_path");
            e.Property(r => r.WorktreeBranch).HasColumnName("worktree_branch");
            e.Property(r => r.TreeHash).HasColumnName("tree_hash");
            e.Property(r => r.StepCount).HasColumnName("step_count").HasDefaultValue(0);
            e.Property(r => r.Diff).HasColumnName("diff");
            e.Property(r => r.MergeConflicts).HasColumnName("merge_conflicts");
            e.Property(r => r.ProjectId).HasColumnName("project_id");
            e.Property(r => r.ModelId).HasColumnName("model_id");
            e.Property(r => r.AgentName).HasColumnName("agent_name");
            e.Property(r => r.AgentCharter).HasColumnName("agent_charter");
            e.Property(r => r.ReviewedBy).HasColumnName("reviewed_by");
            e.Property(r => r.WorkflowRunId).HasColumnName("workflow_run_id");
            e.Property(r => r.MergedCommitHash).HasColumnName("merged_commit_hash");
            e.Property(r => r.ParentRunId).HasColumnName("parent_run_id");
            e.Property(r => r.SubtaskId).HasColumnName("subtask_id");
            e.Property(r => r.Origin).HasColumnName("origin").HasDefaultValue("interactive");
            e.Property(r => r.RetriedFrom).HasColumnName("retried_from");
            e.Property(r => r.ReviewReadyAt).HasColumnName("review_ready_at");
            e.Property(r => r.ReviewWaitMs).HasColumnName("review_wait_ms").HasDefaultValue(0L);
            e.Property(r => r.ArchivedAt).HasColumnName("archived_at");
            e.Property(r => r.OwnerId).HasColumnName("owner_id");
            e.Property(r => r.LeaseExpiresAt).HasColumnName("lease_expires_at");
            e.Property(r => r.HeartbeatAt).HasColumnName("heartbeat_at");
            e.Property(r => r.FencingToken).HasColumnName("fencing_token").HasDefaultValue(0L);
            e.Property(r => r.Attempt).HasColumnName("attempt").HasDefaultValue(0);
            e.HasIndex(r => new { r.ProjectId, r.Status }).HasDatabaseName("IX_runs_project_status");
            e.HasIndex(r => new { r.Origin, r.Status }).HasDatabaseName("IX_runs_origin_status");
            e.HasIndex(r => new { r.ParentRunId, r.SubtaskId }).HasDatabaseName("IX_runs_parent_subtask");
            e.HasIndex(r => r.WorkflowRunId).HasDatabaseName("IX_runs_workflow_run_id");
        });

        model.Entity<RunRevisionRecord>(e =>
        {
            e.ToTable("run_revisions").HasKey(r => new { r.RunId, r.RevisionNumber });
            e.Property(r => r.RunId).HasColumnName("run_id");
            e.Property(r => r.RevisionNumber).HasColumnName("revision_number");
            e.Property(r => r.ReviewerUser).HasColumnName("reviewer_user");
            e.Property(r => r.CreatedAt).HasColumnName("created_at");
            e.Property(r => r.RawComment).HasColumnName("raw_comment");
            e.Property(r => r.SanitizedComment).HasColumnName("sanitized_comment");
            e.Property(r => r.PreviousTreeHash).HasColumnName("previous_tree_hash");
        });

        model.Entity<ProjectRecord>(e =>
        {
            e.ToTable("projects").HasKey(p => p.ProjectId);
            e.Property(p => p.ProjectId).HasColumnName("project_id");
            e.Property(p => p.Name).HasColumnName("name");
            e.Property(p => p.OriginKind).HasColumnName("origin_kind");
            e.Property(p => p.SourceRepository).HasColumnName("source_repository");
            e.Property(p => p.WorkingDirectory).HasColumnName("working_directory");
            e.Property(p => p.DefaultBranch).HasColumnName("default_branch").HasDefaultValue("main");
            e.Property(p => p.Owner).HasColumnName("owner");
            e.Property(p => p.DefaultProvider).HasColumnName("default_provider");
            e.Property(p => p.DefaultModelCopilot).HasColumnName("default_model_copilot");
            e.Property(p => p.DefaultModelFoundry).HasColumnName("default_model_foundry");
            e.Property(p => p.State).HasColumnName("state").HasDefaultValue("active");
            e.Property(p => p.CreatedAt).HasColumnName("created_at");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at");
            e.Property(p => p.MaxReadyPerHeartbeat).HasColumnName("max_ready_per_heartbeat").HasDefaultValue(3);
            e.Property(p => p.PickupAutopilot).HasColumnName("pickup_autopilot").HasDefaultValue(true);
            e.Property(p => p.PickupAutoApproveTools).HasColumnName("pickup_auto_approve_tools").HasDefaultValue(false);
            e.Property(p => p.DefaultWorkflowId).HasColumnName("default_workflow_id");
            e.Property(p => p.ActiveReviewPolicyName).HasColumnName("active_review_policy_name");
            e.Property(p => p.SandboxProfile).HasColumnName("sandbox_profile");
            e.Property(p => p.SourceBlueprintId).HasColumnName("source_blueprint_id");
            e.Property(p => p.SourceBlueprintType).HasColumnName("source_blueprint_type");
            e.Property(p => p.AllowedWorkflowIds).HasColumnName("allowed_workflow_ids");
            e.HasIndex(p => p.State).HasDatabaseName("IX_projects_state");
        });

        model.Entity<BacklogTaskRecord>(e =>
        {
            e.ToTable("backlog_tasks").HasKey(t => t.TaskId);
            e.Property(t => t.TaskId).HasColumnName("task_id");
            e.Property(t => t.ProjectId).HasColumnName("project_id");
            e.Property(t => t.Title).HasColumnName("title");
            e.Property(t => t.Description).HasColumnName("description");
            e.Property(t => t.State).HasColumnName("state");
            e.Property(t => t.OrderKey).HasColumnName("order_key");
            e.Property(t => t.CapturedBy).HasColumnName("captured_by");
            e.Property(t => t.CreatedAt).HasColumnName("created_at");
            e.Property(t => t.CommittedAt).HasColumnName("committed_at");
            e.Property(t => t.ClaimedAt).HasColumnName("claimed_at");
            e.Property(t => t.RunId).HasColumnName("run_id");
            e.Property(t => t.WorkflowOverrideId).HasColumnName("workflow_override_id");
            e.Property(t => t.ArchivedAt).HasColumnName("archived_at");
            e.Property(t => t.SourceFilePath).HasColumnName("source_file_path");
            e.HasIndex(t => new { t.ProjectId, t.State, t.OrderKey })
                .HasDatabaseName("IX_backlog_tasks_project_state_orderkey");
            e.HasIndex(t => new { t.ProjectId, t.State, t.OrderKey })
                .HasDatabaseName("IX_backlog_tasks_orderkey_unique")
                .IsUnique()
                .HasFilter("state IN ('backlog','ready') AND archived_at IS NULL");
            e.HasIndex(t => t.RunId)
                .HasDatabaseName("IX_backlog_tasks_run")
                .IsUnique()
                .HasFilter("run_id IS NOT NULL");
        });

        model.Entity<WorkflowRunRecord>(e =>
        {
            e.ToTable("workflow_runs").HasKey(w => w.WorkflowRunId);
            e.Property(w => w.WorkflowRunId).HasColumnName("workflow_run_id");
            e.Property(w => w.ProjectId).HasColumnName("project_id");
            e.Property(w => w.Task).HasColumnName("task");
            e.Property(w => w.SubmittingUser).HasColumnName("submitting_user");
            e.Property(w => w.StartedAt).HasColumnName("started_at");
            e.Property(w => w.OrchestrationWorktreePath).HasColumnName("orchestration_worktree_path");
            e.HasIndex(w => w.ProjectId).HasDatabaseName("IX_workflow_runs_project_id");
        });

        model.Entity<CastProposalRecord>(e =>
        {
            e.ToTable("cast_proposals").HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id");
            e.Property(c => c.ProjectId).HasColumnName("project_id");
            e.Property(c => c.Owner).HasColumnName("owner");
            e.Property(c => c.CreatedAt).HasColumnName("created_at");
            e.Property(c => c.ExpiresAt).HasColumnName("expires_at");
            e.Property(c => c.ProposalJson).HasColumnName("proposal_json");
            e.HasIndex(c => c.ProjectId).HasDatabaseName("IX_cast_proposals_project_id");
        });
    }
}
