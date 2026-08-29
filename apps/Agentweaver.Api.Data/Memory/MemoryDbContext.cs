using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Diagnostics;

namespace Agentweaver.Api.Memory;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options)
{
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<DecisionInboxEntry> DecisionInbox => Set<DecisionInboxEntry>();
    public DbSet<AgentMemory> AgentMemory => Set<AgentMemory>();
    public DbSet<RunAuthorshipCapability> RunAuthorshipCapabilities => Set<RunAuthorshipCapability>();
    public DbSet<SessionContext> SessionContexts => Set<SessionContext>();
    public DbSet<RunEventRecord> RunEvents => Set<RunEventRecord>();
    public DbSet<OutcomeSpec> OutcomeSpecs => Set<OutcomeSpec>();
    public DbSet<WorkPlan> WorkPlans => Set<WorkPlan>();
    public DbSet<Subtask> Subtasks => Set<Subtask>();
    public DbSet<SubtaskDependency> SubtaskDependencies => Set<SubtaskDependency>();
    public DbSet<SteeringDirective> SteeringDirectives => Set<SteeringDirective>();
    public DbSet<SteeringRevisionExecution> SteeringRevisionExecutions => Set<SteeringRevisionExecution>();
    public DbSet<EntraOAuthState> EntraOAuthStates => Set<EntraOAuthState>();
    public DbSet<WebSessionExchangeCode> WebSessionExchangeCodes => Set<WebSessionExchangeCode>();
    public DbSet<BrowserEntraSession> BrowserEntraSessions => Set<BrowserEntraSession>();
    public DbSet<IntegrationBuildLockRecord> IntegrationBuildLocks => Set<IntegrationBuildLockRecord>();
    public DbSet<DismissedNotification> DismissedNotifications => Set<DismissedNotification>();
    public DbSet<GitHubAuthorizationRecord> GitHubAuthorizations => Set<GitHubAuthorizationRecord>();
    public DbSet<GitHubAppAuthorizationRecord> GitHubAppAuthorizations => Set<GitHubAppAuthorizationRecord>();
    public DbSet<GitHubInstallationRecord> GitHubInstallations => Set<GitHubInstallationRecord>();
    public DbSet<GitHubRepositoryGrantRecord> GitHubRepositoryGrants => Set<GitHubRepositoryGrantRecord>();
    public DbSet<GitHubRepositorySelectionCodeRecord> GitHubRepositorySelectionCodes => Set<GitHubRepositorySelectionCodeRecord>();
    public DbSet<ProjectCopilotBindingRecord> ProjectCopilotBindings => Set<ProjectCopilotBindingRecord>();
    public DbSet<AutomationActivationRecord> AutomationActivations => Set<AutomationActivationRecord>();
    public DbSet<AutomationInvocationRecord> AutomationInvocations => Set<AutomationInvocationRecord>();
    public DbSet<GitHubLifecycleDeliveryRecord> GitHubLifecycleDeliveries => Set<GitHubLifecycleDeliveryRecord>();
    public DbSet<RunGitHubIdentitySnapshotRecord> RunGitHubIdentitySnapshots => Set<RunGitHubIdentitySnapshotRecord>();
    public DbSet<RunGitHubCapabilitySnapshotRecord> RunGitHubCapabilitySnapshots => Set<RunGitHubCapabilitySnapshotRecord>();
    public DbSet<GitHubAuditRecord> GitHubAuditRecords => Set<GitHubAuditRecord>();

    // Replica-safe per-pod / per-run singleton state moved out of process memory.
    public DbSet<PendingRequestRecord> PendingRequests => Set<PendingRequestRecord>();
    public DbSet<HeartbeatStatusRecord> HeartbeatStatuses => Set<HeartbeatStatusRecord>();
    public DbSet<CoordinatorDeferredDecisionRecord> DeferredDecisions => Set<CoordinatorDeferredDecisionRecord>();
    public DbSet<CoordinatorAssemblyReviewRecord> AssemblyReviews => Set<CoordinatorAssemblyReviewRecord>();

    // Shared, concurrency-safe MAF workflow checkpoints (replaces the per-pod file store on Postgres).
    // Postgres-only: local/dev (sqlite) still uses the file-based checkpoint store.
    public DbSet<WorkflowCheckpointRecord> WorkflowCheckpoints => Set<WorkflowCheckpointRecord>();

    // Entities migrated from agentweaver.db (spec-018 P2)
    public DbSet<RunRecord> Runs => Set<RunRecord>();
    public DbSet<RunRevisionRecord> RunRevisions => Set<RunRevisionRecord>();
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<ProjectRoleAssignmentRecord> ProjectRoleAssignments => Set<ProjectRoleAssignmentRecord>();
    public DbSet<BacklogTaskRecord> BacklogTasks => Set<BacklogTaskRecord>();
    public DbSet<BacklogTaskDependencyRecord> BacklogTaskDependencies => Set<BacklogTaskDependencyRecord>();
    public DbSet<WorkflowRunRecord> WorkflowRuns => Set<WorkflowRunRecord>();
    public DbSet<CastProposalRecord> CastProposals => Set<CastProposalRecord>();
    public DbSet<SkillRecord> Skills => Set<SkillRecord>();
    public DbSet<SkillAssignmentRecord> SkillAssignments => Set<SkillAssignmentRecord>();
    public DbSet<SkillMarketplaceSourceRecord> SkillMarketplaceSources => Set<SkillMarketplaceSourceRecord>();
    public DbSet<BlueprintPackageLibraryRecord> BlueprintPackageLibrary => Set<BlueprintPackageLibraryRecord>();
    public DbSet<BlueprintPackageVersionRecord> BlueprintPackageVersions => Set<BlueprintPackageVersionRecord>();
    public DbSet<BlueprintPackagePayloadRecord> BlueprintPackagePayloads => Set<BlueprintPackagePayloadRecord>();
    public DbSet<BlueprintPackageAcquisitionRecord> BlueprintPackageAcquisitions => Set<BlueprintPackageAcquisitionRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Decision>().HasIndex(d => new { d.ProjectId, d.Status });
        model.Entity<Decision>().HasIndex(d => new { d.ProjectId, d.AgentName });
        model.Entity<Decision>().Property(d => d.SourceKind).HasDefaultValue(MemorySourceKinds.Legacy);
        model.Entity<Decision>().Property(d => d.TrustState).HasDefaultValue(MemoryTrustStates.Legacy);
        model.Entity<Decision>()
            .HasOne<Decision>()
            .WithMany()
            .HasForeignKey(d => d.SupersededById)
            .IsRequired(false);
        model.Entity<DecisionInboxEntry>().HasIndex(e => new { e.ProjectId, e.Status });
        model.Entity<DecisionInboxEntry>().HasIndex(e => new { e.ProjectId, e.Slug }).IsUnique();
        model.Entity<DecisionInboxEntry>().Property(e => e.SourceKind).HasDefaultValue(MemorySourceKinds.Legacy);
        model.Entity<DecisionInboxEntry>()
            .HasOne<Decision>()
            .WithMany()
            .HasForeignKey(e => e.DecisionId)
            .IsRequired(false);
        model.Entity<AgentMemory>().HasIndex(m => new { m.ProjectId, m.AgentName });
        model.Entity<AgentMemory>().HasIndex(m => new { m.ProjectId, m.Type });
        model.Entity<AgentMemory>().Property(m => m.SourceKind).HasDefaultValue(MemorySourceKinds.Legacy);
        model.Entity<AgentMemory>().Property(m => m.TrustState).HasDefaultValue(MemoryTrustStates.Legacy);
        model.Entity<RunAuthorshipCapability>().ToTable("run_authorship_capabilities");
        model.Entity<RunAuthorshipCapability>().HasKey(capability => capability.RunId);
        model.Entity<RunAuthorshipCapability>().Property(capability => capability.RunId)
            .HasColumnName("run_id")
            .HasMaxLength(128);
        model.Entity<RunAuthorshipCapability>().Property(capability => capability.TokenHash)
            .HasColumnName("token_hash")
            .IsRequired();
        model.Entity<RunAuthorshipCapability>().Property(capability => capability.ExpiresAt)
            .HasColumnName("expires_at")
            .IsRequired();
        model.Entity<RunAuthorshipCapability>().HasIndex(capability => capability.ExpiresAt);
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

        model.Entity<EntraOAuthState>().HasKey(s => s.State);
        model.Entity<EntraOAuthState>().HasIndex(s => s.ExpiresAt);

        model.Entity<WebSessionExchangeCode>().HasKey(c => c.Code);
        model.Entity<WebSessionExchangeCode>().HasIndex(c => c.ExpiresAt);
        model.Entity<BrowserEntraSession>().HasKey(s => s.Id);
        model.Entity<BrowserEntraSession>().HasIndex(s => s.ExpiresAt);

        model.Entity<IntegrationBuildLockRecord>().HasKey(l => l.ProjectId);
        ConfigureTwoAppPersistence(model);
        model.Entity<DismissedNotification>(e =>
        {
            e.ToTable("dismissed_notifications");
            e.HasKey(d => new { d.User, d.NotificationId });
            e.Property(d => d.User).HasColumnName("user");
            e.Property(d => d.NotificationId).HasColumnName("notification_id");
            e.Property(d => d.DismissedAt).HasColumnName("dismissed_at");
        });

        model.Entity<PendingRequestRecord>().HasIndex(p => p.RunId).IsUnique();
        model.Entity<PendingRequestRecord>().HasIndex(p => p.ExpiresAt);
        model.Entity<HeartbeatStatusRecord>().HasKey(h => h.PodName);
        model.Entity<CoordinatorDeferredDecisionRecord>().HasIndex(d => d.RunId).IsUnique();
        model.Entity<CoordinatorAssemblyReviewRecord>().HasIndex(r => r.CoordinatorRunId).IsUnique();

        // UNIFIED AUTONOMOUS STEERING (rev8, §3d; RD-B per-child): the attempt-specific two-phase
        // revision-effect marker is PER TARGET CHILD. Direction A can target MULTIPLE subtasks, each
        // resumed as its own child run; a single (SteeringDirectiveId, ActionAttempt) marker would let
        // recovery mark the WHOLE directive applied after only ONE child confirmed, silently skipping
        // the rest. The UNIQUE (SteeringDirectiveId, ActionAttempt, RunId) key gives each targeted child
        // its own crash-safe idempotency guard — a racing/replayed launch of the SAME child conflicts,
        // so at most one actor owns a (directive, attempt, child) launch, while distinct children each
        // get their own marker. Mapped on BOTH providers (not in the SQLite ignore list) so
        // EnsureCreated/tests build the table.
        model.Entity<SteeringRevisionExecution>()
            .HasIndex(e => new { e.SteeringDirectiveId, e.ActionAttempt, e.RunId }).IsUnique();

        // ── agentweaver.db entities (spec-018 P2) ──────────────────────────────────
        // These entities only exist in the Postgres schema (InitialPostgres migration).
        // For SQLite, explicitly ignore them so the SQLite migration snapshot stays unchanged
        // and EF does not report pending model changes for the memory.db migrations.
        if (!Database.IsNpgsql())
        {
            // The SQLite companion database keeps a project projection solely as the principal
            // for project-scoped durable two-App records, matching PostgreSQL FK semantics.
            model.Entity<ProjectRecord>(e =>
            {
                e.ToTable("projects");
                e.HasKey(x => x.ProjectId);
                e.Property(x => x.ProjectId).HasColumnName("project_id");
            });
            model.Ignore<RunRecord>();
            model.Ignore<RunRevisionRecord>();
            model.Ignore<ProjectRoleAssignmentRecord>();
            model.Ignore<BacklogTaskRecord>();
            model.Ignore<BacklogTaskDependencyRecord>();
            model.Ignore<WorkflowRunRecord>();
            model.Ignore<CastProposalRecord>();
            model.Ignore<WorkflowCheckpointRecord>();
            model.Ignore<SkillRecord>();
            model.Ignore<SkillAssignmentRecord>();
            model.Ignore<SkillMarketplaceSourceRecord>();
            model.Ignore<BlueprintPackageLibraryRecord>();
            model.Ignore<BlueprintPackageVersionRecord>();
            model.Ignore<BlueprintPackagePayloadRecord>();
            model.Ignore<BlueprintPackageAcquisitionRecord>();
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
            e.Property(r => r.ApprovalGeneration).HasColumnName("approval_generation").HasDefaultValue(1);
            e.Property(r => r.StartedAt).HasColumnName("started_at");
            e.Property(r => r.EndedAt).HasColumnName("ended_at");
            e.Property(r => r.Result).HasColumnName("result");
            e.Property(r => r.WorktreePath).HasColumnName("worktree_path");
            e.Property(r => r.WorktreeBranch).HasColumnName("worktree_branch");
            e.Property(r => r.TreeHash).HasColumnName("tree_hash");
            e.Property(r => r.Diff).HasColumnName("diff");
            e.Property(r => r.MergeConflicts).HasColumnName("merge_conflicts");
            e.Property(r => r.ProjectId).HasColumnName("project_id");
            e.Property(r => r.ModelId).HasColumnName("model_id");
            e.Property(r => r.AgentName).HasColumnName("agent_name");
            e.Property(r => r.AgentCharter).HasColumnName("agent_charter");
            e.Property(r => r.ReviewedBy).HasColumnName("reviewed_by");
            e.Property(r => r.WorkflowRunId).HasColumnName("workflow_run_id");
            e.Property(r => r.WorkflowSelectionReason).HasColumnName("workflow_selection_reason");
            e.Property(r => r.MergedCommitHash).HasColumnName("merged_commit_hash");
            e.Property(r => r.ParentRunId).HasColumnName("parent_run_id");
            e.Property(r => r.SubtaskId).HasColumnName("subtask_id");
            e.Property(r => r.Origin).HasColumnName("origin").HasDefaultValue("interactive");
            e.Property(r => r.RetriedFrom).HasColumnName("retried_from");
            e.Property(r => r.ReviewReadyAt).HasColumnName("review_ready_at");
            e.Property(r => r.ArchivedAt).HasColumnName("archived_at");
            e.Property(r => r.OwnerId).HasColumnName("owner_id");
            e.Property(r => r.LeaseExpiresAt).HasColumnName("lease_expires_at");
            e.Property(r => r.HeartbeatAt).HasColumnName("heartbeat_at");
            e.Property(r => r.FencingToken).HasColumnName("fencing_token").HasDefaultValue(0L);
            e.Property(r => r.Attempt).HasColumnName("attempt").HasDefaultValue(0);
            e.Property(r => r.SandboxBackend).HasColumnName("sandbox_backend");
            e.Property(r => r.SandboxClaimName).HasColumnName("sandbox_claim_name");
            e.Property(r => r.SandboxPodName).HasColumnName("sandbox_pod_name");
            e.Property(r => r.SandboxNamespace).HasColumnName("sandbox_namespace");
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
            e.Property(p => p.TeamRevision).HasColumnName("team_revision").HasDefaultValue(0L);
            e.Property(p => p.MaxReadyPerHeartbeat).HasColumnName("max_ready_per_heartbeat").HasDefaultValue(3);
            e.Property(p => p.PickupAutopilot).HasColumnName("pickup_autopilot").HasDefaultValue(true);
            e.Property(p => p.PickupAutoApproveTools).HasColumnName("pickup_auto_approve_tools").HasDefaultValue(true);
            e.Property(p => p.PreviewApprovalTimeoutMinutes).HasColumnName("preview_approval_timeout_minutes").HasDefaultValue(30);
            e.Property(p => p.DefaultWorkflowId).HasColumnName("default_workflow_id");
            e.Property(p => p.ActiveReviewPolicyName).HasColumnName("active_review_policy_name");
            e.Property(p => p.SandboxProfile).HasColumnName("sandbox_profile");
            e.Property(p => p.SourceBlueprintId).HasColumnName("source_blueprint_id");
            e.Property(p => p.SourceBlueprintType).HasColumnName("source_blueprint_type");
            e.Property(p => p.BlueprintGenerationModel).HasColumnName("blueprint_generation_model");
            e.Property(p => p.WorkflowGenerationModel).HasColumnName("workflow_generation_model");
            e.Property(p => p.OutcomeSpecGenerationModel).HasColumnName("outcome_spec_generation_model");
            e.Property(p => p.AllowedWorkflowIds).HasColumnName("allowed_workflow_ids");
            e.Property(p => p.WebhookSecret).HasColumnName("webhook_secret");
            e.HasIndex(p => p.State).HasDatabaseName("IX_projects_state");
        });

        model.Entity<ProjectRoleAssignmentRecord>(e =>
        {
            e.ToTable("project_role_assignments").HasKey(p => new { p.ProjectId, p.PrincipalId });
            e.Property(p => p.ProjectId).HasColumnName("project_id");
            e.Property(p => p.PrincipalId).HasColumnName("principal_id");
            e.Property(p => p.Role).HasColumnName("role");
            e.Property(p => p.GrantedBy).HasColumnName("granted_by");
            e.Property(p => p.GrantedAt).HasColumnName("granted_at");
            e.HasIndex(p => p.PrincipalId).HasDatabaseName("IX_project_role_assignments_principal_id");
            e.HasIndex(p => new { p.ProjectId, p.Role }).HasDatabaseName("IX_project_role_assignments_project_role");
        });

        model.Entity<BlueprintPackageLibraryRecord>(e =>
        {
            e.ToTable("blueprint_package_library").HasKey(x => new { x.OwnerId, x.PackageId });
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.PackageId).HasColumnName("package_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        model.Entity<BlueprintPackageVersionRecord>(e =>
        {
            e.ToTable("blueprint_package_versions").HasKey(x => new { x.OwnerId, x.PackageId, x.CanonicalVersionKey });
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.PackageId).HasColumnName("package_id");
            e.Property(x => x.CanonicalVersionKey).HasColumnName("canonical_version_key").HasMaxLength(64);
            e.Property(x => x.CanonicalVersion).HasColumnName("canonical_version");
            e.Property(x => x.ContentDigest).HasColumnName("content_digest");
            e.Property(x => x.PayloadSetDigest).HasColumnName("payload_set_digest");
            e.Property(x => x.RawManifestSha256).HasColumnName("raw_manifest_sha256");
            e.Property(x => x.ContainerSha256).HasColumnName("container_sha256");
            e.Property(x => x.RawManifest).HasColumnName("raw_manifest");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
        model.Entity<BlueprintPackagePayloadRecord>(e =>
        {
            e.ToTable("blueprint_package_payloads").HasKey(x => new { x.OwnerId, x.PackageId, x.CanonicalVersionKey, x.Path });
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.PackageId).HasColumnName("package_id");
            e.Property(x => x.CanonicalVersionKey).HasColumnName("canonical_version_key").HasMaxLength(64);
            e.Property(x => x.CanonicalVersion).HasColumnName("canonical_version");
            e.Property(x => x.Path).HasColumnName("path");
            e.Property(x => x.Bytes).HasColumnName("bytes");
        });
        model.Entity<BlueprintPackageAcquisitionRecord>(e =>
        {
            e.ToTable("blueprint_package_acquisitions").HasKey(x => new { x.OwnerId, x.PackageId, x.CanonicalVersionKey, x.Ordinal });
            e.Property(x => x.OwnerId).HasColumnName("owner_id");
            e.Property(x => x.PackageId).HasColumnName("package_id");
            e.Property(x => x.CanonicalVersionKey).HasColumnName("canonical_version_key").HasMaxLength(64);
            e.Property(x => x.CanonicalVersion).HasColumnName("canonical_version");
            e.Property(x => x.Ordinal).HasColumnName("ordinal");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.Producer).HasColumnName("producer");
            e.Property(x => x.Repository).HasColumnName("repository");
            e.Property(x => x.Revision).HasColumnName("revision");
            e.Property(x => x.AcquiredAt).HasColumnName("acquired_at");
            e.Property(x => x.RequestedRef).HasColumnName("requested_ref");
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
            e.Property(t => t.CapturedByUserId).HasColumnName("captured_by_user_id");
            e.Property(t => t.CreatedAt).HasColumnName("created_at");
            e.Property(t => t.CommittedAt).HasColumnName("committed_at");
            e.Property(t => t.ClaimedAt).HasColumnName("claimed_at");
            e.Property(t => t.RunId).HasColumnName("run_id");
            e.Property(t => t.WorkflowOverrideId).HasColumnName("workflow_override_id");
            e.Property(t => t.ArchivedAt).HasColumnName("archived_at");
            e.Property(t => t.SourceFilePath).HasColumnName("source_file_path");
            e.Property(t => t.ParentPrdRunId).HasColumnName("parent_prd_run_id");
            e.Property(t => t.PromotionKey).HasColumnName("promotion_key");
            e.Property(t => t.PromotionReason).HasColumnName("promotion_reason");
            e.Property(t => t.IsAutomationInvocationPending)
                .HasColumnName("automation_invocation_pending")
                .HasDefaultValue(false);
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
            e.HasIndex(t => new { t.ParentPrdRunId, t.PromotionKey })
                .HasDatabaseName("IX_backlog_tasks_parent_promotion_key")
                .IsUnique()
                .HasFilter("parent_prd_run_id IS NOT NULL AND promotion_key IS NOT NULL");
        });

        model.Entity<BacklogTaskDependencyRecord>(e =>
        {
            e.ToTable("backlog_task_dependencies").HasKey(d => new { d.TaskId, d.DependsOnTaskId });
            e.Property(d => d.ProjectId).HasColumnName("project_id");
            e.Property(d => d.TaskId).HasColumnName("task_id");
            e.Property(d => d.DependsOnTaskId).HasColumnName("depends_on_task_id");
            e.Property(d => d.CreatedAt).HasColumnName("created_at");
            e.HasIndex(d => new { d.ProjectId, d.TaskId }).HasDatabaseName("IX_backlog_task_dependencies_project_task");
            e.HasIndex(d => d.DependsOnTaskId).HasDatabaseName("IX_backlog_task_dependencies_prerequisite");
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

        model.Entity<SkillRecord>(e =>
        {
            e.ToTable("skills").HasKey(s => s.SkillId);
            e.HasAlternateKey(s => new { s.ProjectId, s.SkillId })
                .HasName("AK_skills_project_id_skill_id");
            e.Property(s => s.SkillId).HasColumnName("skill_id");
            e.Property(s => s.ProjectId).HasColumnName("project_id");
            e.Property(s => s.Name).HasColumnName("name");
            e.Property(s => s.Description).HasColumnName("description");
            e.Property(s => s.Instructions).HasColumnName("instructions");
            e.Property(s => s.Resources).HasColumnName("resources");
            e.Property(s => s.Provenance).HasColumnName("provenance");
            e.Property(s => s.SourceRepository).HasColumnName("source_repository");
            e.Property(s => s.SourceLocation).HasColumnName("source_location");
            e.Property(s => s.MarketplaceName).HasColumnName("marketplace_name");
            e.Property(s => s.ContentHash).HasColumnName("content_hash");
            e.Property(s => s.Status).HasColumnName("status").HasDefaultValue("active");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            // Uniqueness is enforced by a functional unique index on (project_id, lower(name)) created
            // in the AddSkillCatalog migration (case-insensitive parity with SQLite's COLLATE NOCASE).
            // EF cannot model a lower() index, so it is intentionally not declared here; case-insensitive
            // lookups (EfSkillStore.GetByNameAsync) translate to WHERE lower(name) = … and use it.
            e.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(s => s.ProjectId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_skills_projects_project_id");
        });

        model.Entity<SkillAssignmentRecord>(e =>
        {
            e.ToTable("skill_assignments")
                .HasKey(a => new { a.ProjectId, a.SkillId, a.AgentName });
            e.Property(a => a.ProjectId).HasColumnName("project_id");
            e.Property(a => a.SkillId).HasColumnName("skill_id");
            e.Property(a => a.AgentName).HasColumnName("agent_name");
            e.Property(a => a.CreatedAt).HasColumnName("created_at");
            e.HasIndex(a => new { a.ProjectId, a.AgentName })
                .HasDatabaseName("IX_skill_assignments_agent");
            e.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(a => a.ProjectId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_skill_assignments_projects_project_id");
            e.HasOne<SkillRecord>()
                .WithMany()
                .HasForeignKey(a => new { a.ProjectId, a.SkillId })
                .HasPrincipalKey(s => new { s.ProjectId, s.SkillId })
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_skill_assignments_skills_project_id_skill_id");
        });

        model.Entity<SkillMarketplaceSourceRecord>(e =>
        {
            e.ToTable("skill_marketplace_sources").HasKey(s => s.SourceId);
            e.Property(s => s.SourceId).HasColumnName("source_id");
            e.Property(s => s.ProjectId).HasColumnName("project_id");
            e.Property(s => s.Name).HasColumnName("name");
            e.Property(s => s.Repository).HasColumnName("repository");
            e.Property(s => s.Branch).HasColumnName("branch");
            e.Property(s => s.Subpath).HasColumnName("subpath");
            e.Property(s => s.ParseStrategy).HasColumnName("parse_strategy");
            e.Property(s => s.Enabled).HasColumnName("enabled");
            e.Property(s => s.CreatedAt).HasColumnName("created_at");
            e.Property(s => s.UpdatedAt).HasColumnName("updated_at");
            // Case-insensitive uniqueness parity with SQLite (name COLLATE NOCASE): a functional unique
            // index on (project_id, lower(name)) is created in the AddSkillMarketplaceSources migration.
            e.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(s => s.ProjectId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_skill_marketplace_sources_projects_project_id");
        });

        // Shared MAF workflow checkpoints. Each row is an independent, unique-PK checkpoint so the two
        // API replicas write without contention (no exclusive file lock) and read each other's
        // checkpoints via MVCC — genuine cross-pod resume. store_name partitions the runs/coordinator
        // stores that previously lived in separate directories.
        model.Entity<WorkflowCheckpointRecord>(e =>
        {
            e.ToTable("workflow_checkpoints")
                .HasKey(c => new { c.StoreName, c.SessionId, c.CheckpointId });
            e.Property(c => c.StoreName).HasColumnName("store_name");
            e.Property(c => c.SessionId).HasColumnName("session_id");
            e.Property(c => c.CheckpointId).HasColumnName("checkpoint_id");
            e.Property(c => c.ParentCheckpointId).HasColumnName("parent_checkpoint_id");
            e.Property(c => c.HasParentMetadata).HasColumnName("has_parent_metadata").HasDefaultValue(true);
            e.Property(c => c.Payload).HasColumnName("payload").HasColumnType("jsonb");
            e.Property(c => c.CreatedAt).HasColumnName("created_at");
            e.Property(c => c.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(c => new { c.StoreName, c.SessionId })
                .HasDatabaseName("IX_workflow_checkpoints_store_session");
        });

    }

    private void ConfigureTwoAppPersistence(ModelBuilder model)
    {
        model.Entity<GitHubAuthorizationRecord>(e =>
        {
            e.ToTable("github_authorizations").HasKey(x => x.State);
            e.Property(x => x.State).HasColumnName("state");
            e.Property(x => x.ExternalTransactionId).HasColumnName("external_transaction_id");
            e.Property(x => x.AppKind).HasColumnName("app_kind");
            e.Property(x => x.Purpose).HasColumnName("purpose");
            e.Property(x => x.EntraObjectId).HasColumnName("entra_object_id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.ExpiresAtUnixMilliseconds).HasColumnName("expires_at_unix_ms");
            e.Property(x => x.ReturnRouteKey).HasColumnName("return_route_key");
            e.Property(x => x.PkceVerifierProtected).HasColumnName("pkce_verifier_protected");
            e.Property(x => x.CallbackCookieHash).HasColumnName("callback_cookie_hash");
            e.Property(x => x.BrowserSessionId).HasColumnName("browser_session_id");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(x => new { x.EntraObjectId, x.State }).IsUnique();
            e.HasIndex(x => x.ExternalTransactionId).IsUnique();
            e.HasIndex(x => x.ExpiresAtUnixMilliseconds);
            ConfigureProjectForeignKey(e, "FK_github_authorizations_projects_project_id");
        });

        model.Entity<GitHubAppAuthorizationRecord>(e =>
        {
            e.ToTable("github_app_authorizations").HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntraObjectId).HasColumnName("entra_object_id");
            e.Property(x => x.AppKind).HasColumnName("app_kind");
            e.Property(x => x.Purpose).HasColumnName("purpose");
            e.Property(x => x.CredentialReference).HasColumnName("credential_reference");
            e.Property(x => x.CredentialVersion).HasColumnName("credential_version");
            e.Property(x => x.GrantDigest).HasColumnName("grant_digest");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.HasIndex(x => new { x.EntraObjectId, x.AppKind, x.Purpose });
        });

        model.Entity<GitHubInstallationRecord>(e =>
        {
            e.ToTable("github_installations").HasKey(x => x.InstallationId);
            e.Property(x => x.InstallationId).HasColumnName("installation_id").ValueGeneratedNever();
            e.Property(x => x.AppKind).HasColumnName("app_kind");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            ConfigureProjectForeignKey(e, "FK_github_installations_projects_project_id");
        });

        model.Entity<GitHubRepositoryGrantRecord>(e =>
        {
            e.ToTable("github_repository_grants").HasKey(x => new { x.InstallationId, x.RepositoryId });
            e.Property(x => x.InstallationId).HasColumnName("installation_id");
            e.Property(x => x.RepositoryId).HasColumnName("repository_id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.FullNameDisplay).HasColumnName("full_name_display");
            e.Property(x => x.PermissionDigest).HasColumnName("permission_digest");
            e.Property(x => x.GrantedAt).HasColumnName("granted_at");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.HasIndex(x => new { x.InstallationId, x.RepositoryId }).IsUnique();
            e.HasOne<GitHubInstallationRecord>().WithMany().HasForeignKey(x => x.InstallationId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_github_repository_grants_installations_installation_id");
            ConfigureProjectForeignKey(e, "FK_github_repository_grants_projects_project_id");
        });

        model.Entity<GitHubRepositorySelectionCodeRecord>(e =>
        {
            e.ToTable("github_repository_selection_codes").HasKey(x => x.CodeHash);
            e.Property(x => x.CodeHash).HasColumnName("code_hash");
            e.Property(x => x.EntraObjectId).HasColumnName("entra_object_id");
            e.Property(x => x.RepoAppAuthorizationId).HasColumnName("repo_app_authorization_id");
            e.Property(x => x.RepositoryId).HasColumnName("repository_id");
            e.Property(x => x.ExpiresAtUnixMilliseconds).HasColumnName("expires_at_unix_ms");
            e.Property(x => x.ConsumedAtUnixMilliseconds).HasColumnName("consumed_at_unix_ms");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => new { x.EntraObjectId, x.ExpiresAtUnixMilliseconds });
            e.HasIndex(x => x.ExpiresAtUnixMilliseconds);
        });

        model.Entity<ProjectCopilotBindingRecord>(e =>
        {
            e.ToTable("project_copilot_bindings").HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.EntraObjectId).HasColumnName("entra_object_id");
            e.Property(x => x.CredentialReference).HasColumnName("credential_reference");
            e.Property(x => x.CredentialVersion).HasColumnName("credential_version");
            e.Property(x => x.GrantDigest).HasColumnName("grant_digest");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.BoundAt).HasColumnName("bound_at");
            e.Property(x => x.DeactivatedAt).HasColumnName("deactivated_at");
            e.HasIndex(x => x.ProjectId).IsUnique().HasFilter("status = 0")
                .HasDatabaseName("UX_project_copilot_bindings_active_project");
            ConfigureProjectForeignKey(e, "FK_project_copilot_bindings_projects_project_id");
        });

        model.Entity<AutomationActivationRecord>(e =>
        {
            e.ToTable("automation_activations").HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.InstallationId).HasColumnName("installation_id");
            e.Property(x => x.RepositoryId).HasColumnName("repository_id");
            e.Property(x => x.RepositoryGrantDigest).HasColumnName("repository_grant_digest");
            e.Property(x => x.CopilotBindingId).HasColumnName("copilot_binding_id");
            e.Property(x => x.CopilotBindingGrantDigest).HasColumnName("copilot_binding_grant_digest");
            e.Property(x => x.AutomationKey).HasColumnName("automation_key");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.ActivatedAt).HasColumnName("activated_at");
            e.Property(x => x.InvalidatedAt).HasColumnName("invalidated_at");
            e.HasIndex(x => x.ProjectId).IsUnique().HasFilter("status = 0")
                .HasDatabaseName("UX_automation_activations_active_project");
            e.HasOne<GitHubRepositoryGrantRecord>().WithMany()
                .HasForeignKey(x => new { x.InstallationId, x.RepositoryId })
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_automation_activations_repository_grants_installation_id_repository_id");
            ConfigureProjectForeignKey(e, "FK_automation_activations_projects_project_id");
        });

        model.Entity<AutomationInvocationRecord>(e =>
        {
            e.ToTable("automation_invocations").HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.ActivationId).HasColumnName("activation_id");
            e.Property(x => x.BacklogTaskId).HasColumnName("backlog_task_id");
            e.Property(x => x.PendingBacklogTaskId).HasColumnName("pending_backlog_task_id");
            e.Property(x => x.OccurrenceKey).HasColumnName("occurrence_key");
            e.Property(x => x.DeliveryId).HasColumnName("delivery_id");
            e.Property(x => x.EventName).HasColumnName("event_name");
            e.Property(x => x.InstallationId).HasColumnName("installation_id");
            e.Property(x => x.RepositoryId).HasColumnName("repository_id");
            e.Property(x => x.Outcome).HasColumnName("outcome");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at");
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.HasIndex(x => new { x.ActivationId, x.OccurrenceKey }).IsUnique();
            e.HasIndex(x => x.BacklogTaskId).IsUnique().HasFilter("backlog_task_id IS NOT NULL")
                .HasDatabaseName("UX_automation_invocations_backlog_task_id");
            e.HasIndex(x => x.PendingBacklogTaskId).IsUnique().HasFilter("pending_backlog_task_id IS NOT NULL")
                .HasDatabaseName("UX_automation_invocations_pending_backlog_task_id");
            e.HasIndex(x => x.DeliveryId).IsUnique().HasFilter("delivery_id IS NOT NULL")
                .HasDatabaseName("UX_automation_invocations_delivery_id");
            e.HasOne<AutomationActivationRecord>().WithMany().HasForeignKey(x => x.ActivationId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_automation_invocations_activations_activation_id");
            ConfigureProjectForeignKey(e, "FK_automation_invocations_projects_project_id");
        });

        model.Entity<GitHubLifecycleDeliveryRecord>(e =>
        {
            e.ToTable("github_lifecycle_deliveries").HasKey(x => x.DeliveryId);
            e.Property(x => x.DeliveryId).HasColumnName("delivery_id");
            e.Property(x => x.EventName).HasColumnName("event_name");
            e.Property(x => x.InstallationId).HasColumnName("installation_id");
            e.Property(x => x.RepositoryId).HasColumnName("repository_id");
            e.Property(x => x.ReceivedAt).HasColumnName("received_at");
        });

        model.Entity<RunGitHubIdentitySnapshotRecord>(e =>
        {
            e.ToTable("run_github_identity_snapshots").HasKey(x => x.RunId);
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.AppKind).HasColumnName("app_kind");
            e.Property(x => x.Purpose).HasColumnName("purpose");
            e.Property(x => x.CredentialReference).HasColumnName("credential_reference");
            e.Property(x => x.CredentialVersion).HasColumnName("credential_version");
            e.Property(x => x.GrantDigest).HasColumnName("grant_digest");
            e.Property(x => x.InstallationId).HasColumnName("installation_id");
            e.Property(x => x.RepositoryId).HasColumnName("repository_id");
            e.Property(x => x.EntraObjectId).HasColumnName("entra_object_id");
            e.Property(x => x.CapturedAt).HasColumnName("captured_at");
            ConfigureProjectForeignKey(e, "FK_run_github_identity_snapshots_projects_project_id");
        });

        model.Entity<RunGitHubCapabilitySnapshotRecord>(e =>
        {
            e.ToTable("run_github_capability_snapshots", table => table
                .HasCheckConstraint(
                    "CK_run_github_capability_snapshots_purpose_mapping",
                    """
                    (purpose = 0 AND app_kind = 0 AND source_kind = 0 AND entra_object_id IS NOT NULL AND source_authorization_id IS NOT NULL AND source_binding_id IS NULL AND installation_id IS NULL AND repository_id IS NOT NULL AND credential_reference IS NOT NULL AND credential_version IS NOT NULL)
                    OR (purpose = 1 AND app_kind = 0 AND source_kind = 0 AND entra_object_id IS NOT NULL AND source_authorization_id IS NOT NULL AND source_binding_id IS NULL AND installation_id IS NULL AND repository_id IS NULL AND credential_reference IS NOT NULL AND credential_version IS NOT NULL)
                    OR (purpose = 2 AND app_kind = 0 AND source_kind = 1 AND entra_object_id IS NULL AND source_authorization_id IS NULL AND source_binding_id IS NULL AND installation_id IS NOT NULL AND repository_id IS NOT NULL AND credential_reference IS NULL AND credential_version IS NULL)
                    OR (purpose = 3 AND app_kind = 1 AND source_kind = 2 AND entra_object_id IS NULL AND source_authorization_id IS NULL AND source_binding_id IS NOT NULL AND installation_id IS NULL AND repository_id IS NULL AND credential_reference IS NOT NULL AND credential_version IS NOT NULL)
                    """));
            e.HasKey(x => x.SnapshotRef);
            e.Property(x => x.SnapshotRef).HasColumnName("snapshot_ref");
            e.Property(x => x.RunId).HasColumnName("run_id");
            e.Property(x => x.Purpose).HasColumnName("purpose");
            e.Property(x => x.AppKind).HasColumnName("app_kind");
            e.Property(x => x.SourceKind).HasColumnName("source_kind");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.EntraObjectId).HasColumnName("entra_object_id");
            e.Property(x => x.SourceAuthorizationId).HasColumnName("source_authorization_id");
            e.Property(x => x.SourceBindingId).HasColumnName("source_binding_id");
            e.Property(x => x.InstallationId).HasColumnName("installation_id");
            e.Property(x => x.RepositoryId).HasColumnName("repository_id");
            e.Property(x => x.CredentialReference).HasColumnName("credential_reference");
            e.Property(x => x.CredentialVersion).HasColumnName("credential_version");
            e.Property(x => x.GrantDigest).HasColumnName("grant_digest");
            e.Property(x => x.CapturedAt).HasColumnName("captured_at");
            e.Property(x => x.SnapshotExpiresAt).HasColumnName("snapshot_expires_at");
            e.HasIndex(x => new { x.RunId, x.Purpose }).IsUnique()
                .HasDatabaseName("UX_run_github_capability_snapshots_run_purpose");
            ConfigureProjectForeignKey(e, "FK_run_github_capability_snapshots_projects_project_id");
        });

        model.Entity<GitHubAuditRecord>(e =>
        {
            e.ToTable("github_audit_records").HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.EntraObjectId).HasColumnName("entra_object_id");
            e.Property(x => x.ActorKind).HasColumnName("actor_kind");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.ResourceId).HasColumnName("resource_id");
            e.Property(x => x.AppKind).HasColumnName("app_kind");
            e.Property(x => x.CapabilityPurpose).HasColumnName("capability_purpose");
            e.Property(x => x.Outcome).HasColumnName("outcome");
            e.Property(x => x.ReasonCode).HasColumnName("reason_code");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            e.Property(x => x.GrantDigest).HasColumnName("grant_digest");
            e.HasIndex(x => x.OccurredAt);
        });
    }

    private void ConfigureProjectForeignKey<TEntity>(
        EntityTypeBuilder<TEntity> entity,
        string constraintName)
        where TEntity : class
    {
        entity.HasOne<ProjectRecord>().WithMany().HasForeignKey("ProjectId")
            .OnDelete(DeleteBehavior.Cascade).HasConstraintName(constraintName);
    }
}
