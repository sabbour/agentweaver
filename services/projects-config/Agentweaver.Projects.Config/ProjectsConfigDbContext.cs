using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentweaver.Projects.Config;

/// <summary>
/// EF Core mapping for the service-owned PostgreSQL schema, including revision tokens and authority/audit relationships.
/// </summary>
public sealed class ProjectsConfigDbContext(DbContextOptions<ProjectsConfigDbContext> options) : DbContext(options)
{
    public const string Schema = "projects_config";

    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<ProjectTenantMembershipRecord> TenantMemberships => Set<ProjectTenantMembershipRecord>();
    public DbSet<ProjectRoleAssignmentRecord> RoleAssignments => Set<ProjectRoleAssignmentRecord>();
    public DbSet<ProjectAuthorityAuditRecord> AuthorityAudit => Set<ProjectAuthorityAuditRecord>();
    public DbSet<ProjectConfigurationRevisionRecord> ProjectConfigurationRevisions => Set<ProjectConfigurationRevisionRecord>();
    public DbSet<PlatformRuntimeHeadRecord> PlatformRuntimeHeads => Set<PlatformRuntimeHeadRecord>();
    public DbSet<PlatformRuntimeRevisionRecord> PlatformRuntimeRevisions => Set<PlatformRuntimeRevisionRecord>();
    public DbSet<ProjectRunSelectionRecord> RunSelections => Set<ProjectRunSelectionRecord>();
    public DbSet<ProjectSetupOperationRecord> ProjectSetupOperations => Set<ProjectSetupOperationRecord>();
    public DbSet<ProjectMarketplaceSourceRecord> MarketplaceSources => Set<ProjectMarketplaceSourceRecord>();
    public DbSet<ProjectCastingProposalRecord> CastingProposals => Set<ProjectCastingProposalRecord>();
    public DbSet<ProjectCastingGenerationRequestRecord> CastingGenerationRequests =>
        Set<ProjectCastingGenerationRequestRecord>();
    public DbSet<ProjectSkillContentRevisionRecord> SkillContentRevisions => Set<ProjectSkillContentRevisionRecord>();
    public DbSet<ProjectSkillImportIdempotencyRecord> SkillImportIdempotency =>
        Set<ProjectSkillImportIdempotencyRecord>();
    public DbSet<ProjectSkillContentRevocationRecord> SkillContentRevocations =>
        Set<ProjectSkillContentRevocationRecord>();
    public DbSet<HostedModelReasoningCatalogRecord> HostedModelReasoningCatalog =>
        Set<HostedModelReasoningCatalogRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The database mapping mirrors the append-only revision and authority constraints used by the service transactions.
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<ProjectRecord>(entity =>
        {
            entity.ToTable("projects", table =>
            {
                table.HasCheckConstraint("ck_projects_revision", "revision > 0");
                table.HasCheckConstraint("ck_projects_configuration_revision", "configuration_revision > 0");
                table.HasCheckConstraint("ck_projects_state", "state IN ('Active', 'Archived')");
            });
            entity.HasKey(project => project.ProjectId);
            entity.Property(project => project.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(project => project.TenantId).HasColumnName("tenant_id").HasMaxLength(256).IsRequired();
            entity.Property(project => project.CreatedByActorId).HasColumnName("created_by_actor_id").HasMaxLength(256).IsRequired();
            entity.Property(project => project.Name).HasColumnName("name").HasMaxLength(160).IsRequired();
            entity.Property(project => project.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            entity.Property(project => project.Revision).HasColumnName("revision").IsConcurrencyToken();
            entity.Property(project => project.ConfigurationRevision)
                .HasColumnName("configuration_revision").IsConcurrencyToken();
            entity.Property(project => project.CreatedAt).HasColumnName("created_at");
            entity.Property(project => project.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(project => new { project.TenantId, project.State });
        });

        modelBuilder.Entity<ProjectTenantMembershipRecord>(entity =>
        {
            entity.ToTable("tenant_memberships", table =>
            {
                table.HasCheckConstraint("ck_tenant_memberships_state", "state IN ('Active', 'Revoked')");
                table.HasCheckConstraint("ck_tenant_memberships_revision", "revision > 0");
            });
            entity.HasKey(membership => membership.MembershipId);
            entity.Property(membership => membership.MembershipId).HasColumnName("membership_id");
            entity.Property(membership => membership.Issuer).HasColumnName("issuer").HasMaxLength(512).IsRequired();
            entity.Property(membership => membership.Subject).HasColumnName("subject").HasMaxLength(256).IsRequired();
            entity.Property(membership => membership.TenantId).HasColumnName("tenant_id").HasMaxLength(256).IsRequired();
            entity.Property(membership => membership.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            entity.Property(membership => membership.Revision).HasColumnName("revision").IsConcurrencyToken();
            entity.Property(membership => membership.GrantedBy).HasColumnName("granted_by").HasMaxLength(256).IsRequired();
            entity.Property(membership => membership.GrantedAt).HasColumnName("granted_at");
            entity.Property(membership => membership.RevokedBy).HasColumnName("revoked_by").HasMaxLength(256);
            entity.Property(membership => membership.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(membership => new { membership.Issuer, membership.Subject, membership.TenantId })
                .IsUnique()
                .HasFilter("\"state\" = 'Active'");
            entity.HasIndex(membership => new { membership.Issuer, membership.Subject, membership.State });
        });

        modelBuilder.Entity<ProjectRoleAssignmentRecord>(entity =>
        {
            entity.ToTable("project_role_assignments", table =>
            {
                table.HasCheckConstraint("ck_project_role_assignments_state", "state IN ('Active', 'Revoked')");
                table.HasCheckConstraint(
                    "ck_project_role_assignments_resource_type",
                    "resource_type IN ('Platform', 'Tenant', 'Project')");
                table.HasCheckConstraint(
                    "ck_project_role_assignments_role",
                    "role IN ('PlatformAdmin', 'TenantAdmin', 'Owner', 'Contributor', 'Viewer', 'Orchestrator')");
                table.HasCheckConstraint("ck_project_role_assignments_revision", "revision > 0");
            });
            entity.HasKey(assignment => assignment.AssignmentId);
            entity.Property(assignment => assignment.AssignmentId).HasColumnName("assignment_id");
            entity.Property(assignment => assignment.MembershipId).HasColumnName("membership_id");
            entity.Property(assignment => assignment.ResourceType).HasColumnName("resource_type")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(assignment => assignment.ResourceId).HasColumnName("resource_id").HasMaxLength(256).IsRequired();
            entity.Property(assignment => assignment.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(32);
            entity.Property(assignment => assignment.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            entity.Property(assignment => assignment.Revision).HasColumnName("revision").IsConcurrencyToken();
            entity.Property(assignment => assignment.GrantedBy).HasColumnName("granted_by").HasMaxLength(256).IsRequired();
            entity.Property(assignment => assignment.GrantedAt).HasColumnName("granted_at");
            entity.Property(assignment => assignment.RevokedBy).HasColumnName("revoked_by").HasMaxLength(256);
            entity.Property(assignment => assignment.RevokedAt).HasColumnName("revoked_at");
            entity.HasOne<ProjectTenantMembershipRecord>()
                .WithMany()
                .HasForeignKey(assignment => assignment.MembershipId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(assignment => new
                {
                    assignment.MembershipId,
                    assignment.ResourceType,
                    assignment.ResourceId,
                    assignment.Role,
                })
                .IsUnique()
                .HasFilter("\"state\" = 'Active'");
            entity.HasIndex(assignment => new
                {
                    assignment.ResourceType,
                    assignment.ResourceId,
                    assignment.Role,
                    assignment.State,
                });
        });

        modelBuilder.Entity<ProjectAuthorityAuditRecord>(entity =>
        {
            entity.ToTable("authority_audit", table =>
            {
                table.HasCheckConstraint(
                    "ck_authority_audit_event_type",
                    "event_type IN ('membership_granted', 'membership_revoked', 'role_assigned', 'role_revoked')");
                table.HasCheckConstraint("ck_authority_audit_revision", "revision > 0");
            });
            entity.HasKey(audit => audit.EventId);
            entity.Property(audit => audit.EventId).HasColumnName("event_id");
            entity.Property(audit => audit.InvocationId).HasColumnName("invocation_id");
            entity.Property(audit => audit.EventType).HasColumnName("event_type").HasMaxLength(32).IsRequired();
            entity.Property(audit => audit.MembershipId).HasColumnName("membership_id");
            entity.Property(audit => audit.AssignmentId).HasColumnName("assignment_id");
            entity.Property(audit => audit.Issuer).HasColumnName("issuer").HasMaxLength(512).IsRequired();
            entity.Property(audit => audit.Subject).HasColumnName("subject").HasMaxLength(256).IsRequired();
            entity.Property(audit => audit.TenantId).HasColumnName("tenant_id").HasMaxLength(256).IsRequired();
            entity.Property(audit => audit.ResourceType).HasColumnName("resource_type").HasConversion<string>().HasMaxLength(16);
            entity.Property(audit => audit.ResourceId).HasColumnName("resource_id").HasMaxLength(256);
            entity.Property(audit => audit.Role).HasColumnName("role").HasConversion<string>().HasMaxLength(32);
            entity.Property(audit => audit.Revision).HasColumnName("revision");
            entity.Property(audit => audit.Actor).HasColumnName("actor").HasMaxLength(256).IsRequired();
            entity.Property(audit => audit.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(audit => new { audit.MembershipId, audit.CreatedAt });
            entity.HasIndex(audit => new { audit.TenantId, audit.InvocationId, audit.CreatedAt })
                .HasDatabaseName("IX_authority_audit_tenant_invocation_created_at")
                .HasFilter("\"invocation_id\" IS NOT NULL");
        });

        modelBuilder.Entity<ProjectConfigurationRevisionRecord>(entity =>
        {
            entity.ToTable("project_configuration_revisions");
            entity.HasKey(configuration => new { configuration.ProjectId, configuration.Revision });
            entity.Property(configuration => configuration.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(configuration => configuration.Revision).HasColumnName("revision");
            entity.Property(configuration => configuration.ConfigurationJson)
                .HasColumnName("configuration").HasColumnType("jsonb").IsRequired();
            entity.Property(configuration => configuration.UpdatedByActorId)
                .HasColumnName("updated_by_actor_id").HasMaxLength(256).IsRequired();
            entity.Property(configuration => configuration.CreatedAt).HasColumnName("created_at");
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(configuration => configuration.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlatformRuntimeHeadRecord>(entity =>
        {
            entity.ToTable("platform_runtime_heads", table =>
                table.HasCheckConstraint("ck_platform_runtime_singleton", "id = 'default'"));
            entity.HasKey(head => head.Id);
            entity.Property(head => head.Id).HasColumnName("id").HasMaxLength(16);
            entity.Property(head => head.CurrentRevision).HasColumnName("current_revision")
                .IsConcurrencyToken();
            entity.HasData(new PlatformRuntimeHeadRecord
            {
                Id = PlatformRuntimeHeadRecord.SingletonId,
                CurrentRevision = 0,
            });
        });

        modelBuilder.Entity<PlatformRuntimeRevisionRecord>(entity =>
        {
            entity.ToTable("platform_runtime_revisions");
            entity.HasKey(configuration => configuration.Revision);
            entity.Property(configuration => configuration.HeadId).HasColumnName("head_id").HasMaxLength(16);
            entity.Property(configuration => configuration.Revision).HasColumnName("revision");
            entity.Property(configuration => configuration.ConfigurationJson)
                .HasColumnName("configuration").HasColumnType("jsonb").IsRequired();
            entity.Property(configuration => configuration.UpdatedByActorId)
                .HasColumnName("updated_by_actor_id").HasMaxLength(256).IsRequired();
            entity.Property(configuration => configuration.CreatedAt).HasColumnName("created_at");
            entity.HasOne<PlatformRuntimeHeadRecord>()
                .WithMany()
                .HasForeignKey(configuration => configuration.HeadId)
                .HasPrincipalKey(head => head.Id)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectRunSelectionRecord>(entity =>
        {
            entity.ToTable("project_run_selections");
            entity.HasKey(selection => selection.RunId);
            entity.Property(selection => selection.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(selection => selection.ProjectId).HasColumnName("project_id").HasMaxLength(32).IsRequired();
            entity.Property(selection => selection.ProjectRevision).HasColumnName("project_revision");
            entity.Property(selection => selection.ProjectConfigurationRevision)
                .HasColumnName("project_configuration_revision");
            entity.Property(selection => selection.PlatformRuntimeRevision).HasColumnName("platform_runtime_revision");
            entity.Property(selection => selection.ContextRevision).HasColumnName("context_revision").HasMaxLength(256).IsRequired();
            entity.Property(selection => selection.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64).IsRequired();
            entity.Property(selection => selection.SnapshotJson).HasColumnName("snapshot").HasColumnType("jsonb").IsRequired();
            entity.Property(selection => selection.CreatedAt).HasColumnName("created_at");
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(selection => selection.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(selection => new { selection.ProjectId, selection.RunId }).IsUnique();
        });

        modelBuilder.Entity<ProjectSetupOperationRecord>(entity =>
        {
            entity.ToTable("project_setup_operations", table =>
            {
                table.HasCheckConstraint(
                    "ck_project_setup_operations_kind",
                    "operation_kind IN ('Setup', 'Cleanup')");
                table.HasCheckConstraint(
                    "ck_project_setup_operations_state",
                    "result_state IN ('Created', 'Cleaned', 'RetainedPending')");
                table.HasCheckConstraint(
                    "ck_project_setup_operations_revision",
                    "membership_revision > 0 AND project_revision > 0 AND " +
                    "project_configuration_revision > 0 AND assignment_revision > 0 AND " +
                    "(owner_assignment_revision IS NULL OR owner_assignment_revision > 0)");
                table.HasCheckConstraint(
                    "ck_project_setup_operations_owner_binding",
                    "(owner_assignment_id IS NULL AND owner_assignment_revision IS NULL AND " +
                    "owner_authority_audit_event_id IS NULL) OR " +
                    "(owner_assignment_id IS NOT NULL AND owner_assignment_id <> assignment_id AND " +
                    "owner_assignment_revision IS NOT NULL AND owner_authority_audit_event_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_project_setup_operations_owner_revocation_audit",
                    "owner_revocation_audit_event_id IS NULL OR operation_kind = 'Cleanup'");
                table.HasCheckConstraint(
                    "ck_project_setup_operations_cleanup_revision",
                    "(operation_kind = 'Setup' AND result_state = 'Created' AND " +
                    "expected_project_revision IS NULL AND expected_assignment_revision IS NULL) OR " +
                    "(operation_kind = 'Cleanup' AND result_state IN ('Cleaned', 'RetainedPending') AND " +
                    "expected_project_revision IS NOT NULL AND expected_project_revision > 0 AND " +
                    "expected_assignment_revision IS NOT NULL AND expected_assignment_revision > 0)");
                table.HasCheckConstraint(
                    "ck_project_setup_operations_hash",
                    "request_hash ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(operation => new { operation.InvocationId, operation.OperationKind });
            entity.Property(operation => operation.InvocationId).HasColumnName("invocation_id");
            entity.Property(operation => operation.OperationKind).HasColumnName("operation_kind")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(operation => operation.RequestHash).HasColumnName("request_hash")
                .HasColumnType("character(64)").HasMaxLength(64).IsRequired();
            entity.Property(operation => operation.Issuer).HasColumnName("issuer").HasMaxLength(512).IsRequired();
            entity.Property(operation => operation.Subject).HasColumnName("subject").HasMaxLength(256).IsRequired();
            entity.Property(operation => operation.TenantId).HasColumnName("tenant_id").HasMaxLength(256).IsRequired();
            entity.Property(operation => operation.MembershipId).HasColumnName("membership_id");
            entity.Property(operation => operation.MembershipRevision).HasColumnName("membership_revision");
            entity.Property(operation => operation.ProjectId).HasColumnName("project_id").HasMaxLength(32).IsRequired();
            entity.Property(operation => operation.ProjectRevision).HasColumnName("project_revision");
            entity.Property(operation => operation.ProjectConfigurationRevision)
                .HasColumnName("project_configuration_revision");
            entity.Property(operation => operation.AssignmentId).HasColumnName("assignment_id");
            entity.Property(operation => operation.AssignmentRevision).HasColumnName("assignment_revision");
            entity.Property(operation => operation.OwnerAssignmentId).HasColumnName("owner_assignment_id");
            entity.Property(operation => operation.OwnerAssignmentRevision).HasColumnName("owner_assignment_revision");
            entity.Property(operation => operation.OwnerAuthorityAuditEventId)
                .HasColumnName("owner_authority_audit_event_id");
            entity.Property(operation => operation.OwnerRevocationAuditEventId)
                .HasColumnName("owner_revocation_audit_event_id");
            entity.Property(operation => operation.ExpectedProjectRevision).HasColumnName("expected_project_revision");
            entity.Property(operation => operation.ExpectedAssignmentRevision).HasColumnName("expected_assignment_revision");
            entity.Property(operation => operation.AuthorityAuditEventId).HasColumnName("authority_audit_event_id");
            entity.Property(operation => operation.ResultState).HasColumnName("result_state")
                .HasConversion<string>().HasMaxLength(24);
            entity.Property(operation => operation.ResultJson).HasColumnName("result")
                .HasColumnType("jsonb").IsRequired();
            entity.Property(operation => operation.CreatedAt).HasColumnName("created_at");
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(operation => operation.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectTenantMembershipRecord>()
                .WithMany()
                .HasForeignKey(operation => operation.MembershipId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectRoleAssignmentRecord>()
                .WithMany()
                .HasForeignKey(operation => operation.AssignmentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectAuthorityAuditRecord>()
                .WithMany()
                .HasForeignKey(operation => operation.AuthorityAuditEventId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectRoleAssignmentRecord>()
                .WithMany()
                .HasForeignKey(operation => operation.OwnerAssignmentId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectAuthorityAuditRecord>()
                .WithMany()
                .HasForeignKey(operation => operation.OwnerAuthorityAuditEventId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectAuthorityAuditRecord>()
                .WithMany()
                .HasForeignKey(operation => operation.OwnerRevocationAuditEventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectMarketplaceSourceRecord>(entity =>
        {
            entity.ToTable("marketplace_sources", table =>
            {
                table.HasCheckConstraint("ck_marketplace_sources_revision", "revision > 0");
                table.HasCheckConstraint(
                    "ck_marketplace_sources_state",
                    "state IN ('Active', 'Removed')");
            });
            entity.HasKey(source => source.SourceId);
            entity.Property(source => source.SourceId).HasColumnName("source_id");
            entity.Property(source => source.ProjectId).HasColumnName("project_id").HasMaxLength(32).IsRequired();
            entity.Property(source => source.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            entity.Property(source => source.NormalizedName)
                .HasColumnName("normalized_name").HasMaxLength(64).IsRequired();
            entity.Property(source => source.Repository).HasColumnName("repository").HasMaxLength(201).IsRequired();
            entity.Property(source => source.RequestedRef).HasColumnName("requested_ref").HasMaxLength(255).IsRequired();
            entity.Property(source => source.Subpath).HasColumnName("subpath").HasMaxLength(1024);
            entity.Property(source => source.Revision).HasColumnName("revision").IsConcurrencyToken();
            entity.Property(source => source.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            entity.Property(source => source.CreatedByActorId)
                .HasColumnName("created_by_actor_id").HasMaxLength(256).IsRequired();
            entity.Property(source => source.UpdatedByActorId)
                .HasColumnName("updated_by_actor_id").HasMaxLength(256).IsRequired();
            entity.Property(source => source.CreatedAt).HasColumnName("created_at");
            entity.Property(source => source.UpdatedAt).HasColumnName("updated_at");
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(source => source.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(source => new { source.ProjectId, source.NormalizedName })
                .IsUnique()
                .HasDatabaseName("ux_marketplace_sources_project_name_active")
                .HasFilter("\"state\" = 'Active'");
            entity.HasIndex(source => new { source.ProjectId, source.State });
        });

        modelBuilder.Entity<ProjectCastingProposalRecord>(entity =>
        {
            entity.ToTable("project_casting_proposals", table =>
            {
                table.HasCheckConstraint(
                    "ck_project_casting_proposals_base_revision",
                    "base_configuration_revision > 0");
                table.HasCheckConstraint("ck_project_casting_proposals_draft_revision", "draft_revision > 0");
                table.HasCheckConstraint(
                    "ck_project_casting_proposals_state",
                    "state IN ('Pending', 'Confirmed', 'Rejected')");
                table.HasCheckConstraint(
                    "ck_project_casting_proposals_confirmed_result",
                    "(state = 'Confirmed' AND confirmed_configuration_revision IS NOT NULL AND result IS NOT NULL) OR " +
                    "(state <> 'Confirmed' AND confirmed_configuration_revision IS NULL AND result IS NULL)");
                table.HasCheckConstraint(
                    "ck_project_casting_proposals_transfer_provenance",
                    "(transfer_format_version IS NULL AND transfer_source_project_id IS NULL AND " +
                    "transfer_source_configuration_revision IS NULL AND transfer_content_digest IS NULL) OR " +
                    "(transfer_format_version = 1 AND transfer_source_project_id IS NOT NULL AND " +
                    "transfer_source_configuration_revision > 0 AND transfer_content_digest ~ '^[0-9a-f]{64}$')");
            });
            entity.HasKey(proposal => proposal.ProposalId);
            entity.Property(proposal => proposal.ProposalId).HasColumnName("proposal_id");
            entity.Property(proposal => proposal.ProjectId).HasColumnName("project_id").HasMaxLength(32).IsRequired();
            entity.Property(proposal => proposal.BaseConfigurationRevision)
                .HasColumnName("base_configuration_revision").IsRequired();
            entity.Property(proposal => proposal.DraftRevision).HasColumnName("draft_revision").IsConcurrencyToken();
            entity.Property(proposal => proposal.State).HasColumnName("state")
                .HasConversion<string>().HasMaxLength(16).IsConcurrencyToken();
            entity.Property(proposal => proposal.DraftJson).HasColumnName("draft").HasColumnType("jsonb").IsRequired();
            entity.Property(proposal => proposal.ConfirmedConfigurationRevision)
                .HasColumnName("confirmed_configuration_revision");
            entity.Property(proposal => proposal.ResultJson).HasColumnName("result").HasColumnType("jsonb");
            entity.Property(proposal => proposal.TransferFormatVersion).HasColumnName("transfer_format_version");
            entity.Property(proposal => proposal.TransferSourceProjectId)
                .HasColumnName("transfer_source_project_id").HasMaxLength(32);
            entity.Property(proposal => proposal.TransferSourceConfigurationRevision)
                .HasColumnName("transfer_source_configuration_revision");
            entity.Property(proposal => proposal.TransferContentDigest)
                .HasColumnName("transfer_content_digest").HasMaxLength(64);
            entity.Property(proposal => proposal.CreatedByActorId)
                .HasColumnName("created_by_actor_id").HasMaxLength(256).IsRequired();
            entity.Property(proposal => proposal.CreatedAt).HasColumnName("created_at");
            entity.Property(proposal => proposal.UpdatedByActorId)
                .HasColumnName("updated_by_actor_id").HasMaxLength(256).IsRequired();
            entity.Property(proposal => proposal.UpdatedAt).HasColumnName("updated_at");
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(proposal => proposal.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(proposal => new { proposal.ProjectId, proposal.CreatedAt });
        });

        modelBuilder.Entity<ProjectCastingGenerationRequestRecord>(entity =>
        {
            entity.ToTable("casting_generation_requests", table =>
            {
                table.HasCheckConstraint(
                    "ck_casting_generation_requests_base_revision",
                    "base_configuration_revision > 0");
                table.HasCheckConstraint(
                    "ck_casting_generation_requests_mode",
                    "mode IN ('Formulate', 'Analyze')");
                table.HasCheckConstraint(
                    "ck_casting_generation_requests_state",
                    "state IN ('Pending', 'Completed', 'Failed', 'Stale', 'Cancelled')");
                table.HasCheckConstraint(
                    "ck_casting_generation_requests_result",
                    "(state = 'Completed' AND output_digest IS NOT NULL AND " +
                    "output_digest ~ '^[0-9a-f]{64}$' AND failure_code IS NULL) OR " +
                    "(state = 'Failed' AND output_digest IS NULL AND failure_code IS NOT NULL AND " +
                    "failure_code ~ '^[a-z][a-z0-9_]{0,63}$') OR " +
                    "(state IN ('Pending', 'Stale', 'Cancelled') AND output_digest IS NULL AND failure_code IS NULL)");
                table.HasCheckConstraint(
                    "ck_casting_generation_requests_request_digest",
                    "request_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_casting_generation_requests_idempotency_key",
                    "idempotency_key ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$'");
            });
            entity.HasKey(request => request.RequestId);
            entity.Property(request => request.RequestId).HasColumnName("request_id");
            entity.Property(request => request.ProjectId).HasColumnName("project_id").HasMaxLength(32).IsRequired();
            entity.Property(request => request.ProposalId).HasColumnName("proposal_id");
            entity.Property(request => request.BaseConfigurationRevision)
                .HasColumnName("base_configuration_revision").IsRequired();
            entity.Property(request => request.RunId).HasColumnName("run_id").HasMaxLength(256).IsRequired();
            entity.Property(request => request.Mode).HasColumnName("mode")
                .HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(request => request.State).HasColumnName("state")
                .HasConversion<string>().HasMaxLength(16).IsConcurrencyToken();
            entity.Property(request => request.ActorIssuer).HasColumnName("actor_issuer")
                .HasMaxLength(512).IsRequired();
            entity.Property(request => request.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.Property(request => request.IdempotencyKey).HasColumnName("idempotency_key")
                .HasMaxLength(128).IsRequired();
            entity.Property(request => request.RequestDigest).HasColumnName("request_digest")
                .HasMaxLength(64).IsRequired();
            entity.Property(request => request.OutputDigest).HasColumnName("output_digest").HasMaxLength(64);
            entity.Property(request => request.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
            entity.Property(request => request.CreatedAt).HasColumnName("created_at");
            entity.Property(request => request.UpdatedAt).HasColumnName("updated_at");
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(request => request.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectCastingProposalRecord>()
                .WithMany()
                .HasForeignKey(request => request.ProposalId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(request => request.ProposalId).IsUnique();
            entity.HasIndex(request => new
            {
                request.ProjectId,
                request.ActorIssuer,
                request.ActorId,
                request.IdempotencyKey,
            }).IsUnique().HasDatabaseName("ux_casting_generation_requests_idempotency");
            entity.HasIndex(request => new { request.ProjectId, request.RunId })
                .IsUnique().HasDatabaseName("ux_casting_generation_requests_project_run");
        });

        modelBuilder.Entity<ProjectSkillContentRevisionRecord>(entity =>
        {
            entity.ToTable("project_skill_content_revisions", table =>
            {
                table.HasCheckConstraint("ck_project_skill_revision", "revision > 0");
                table.HasCheckConstraint("ck_project_skill_resource_count", "resource_count BETWEEN 0 AND 64");
                table.HasCheckConstraint("ck_project_skill_total_bytes", "total_bytes >= 0");
                table.HasCheckConstraint(
                    "ck_project_skill_digest",
                    "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(revision => new { revision.ProjectId, revision.SkillId, revision.Revision });
            entity.Property(revision => revision.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(revision => revision.SkillId).HasColumnName("skill_id").HasMaxLength(256);
            entity.Property(revision => revision.Revision).HasColumnName("revision");
            entity.Property(revision => revision.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
            entity.Property(revision => revision.Description).HasColumnName("description").HasMaxLength(1024).IsRequired();
            entity.Property(revision => revision.ContentDigest).HasColumnName("content_digest").HasMaxLength(64).IsRequired();
            entity.Property(revision => revision.ObjectKey).HasColumnName("object_key").HasMaxLength(512).IsRequired();
            entity.Property(revision => revision.ResourceCount).HasColumnName("resource_count");
            entity.Property(revision => revision.TotalBytes).HasColumnName("total_bytes");
            entity.Property(revision => revision.CreatedByActorId).HasColumnName("created_by_actor_id")
                .HasMaxLength(256).IsRequired();
            entity.Property(revision => revision.CreatedAt).HasColumnName("created_at");
            entity.Property(revision => revision.SourceId).HasColumnName("source_id").HasMaxLength(256);
            entity.Property(revision => revision.SourceRevision).HasColumnName("source_revision").HasMaxLength(256);
            entity.Property(revision => revision.RequestedRef).HasColumnName("requested_ref").HasMaxLength(256);
            entity.Property(revision => revision.ResolvedCommitSha).HasColumnName("resolved_commit_sha").HasMaxLength(64);
            entity.Property(revision => revision.SelectedPath).HasColumnName("selected_path").HasMaxLength(1024);
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(revision => revision.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectSkillImportIdempotencyRecord>(entity =>
        {
            entity.ToTable("project_skill_import_idempotency");
            entity.HasKey(operation => new { operation.ProjectId, operation.ScopeDigest });
            entity.Property(operation => operation.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(operation => operation.ScopeDigest).HasColumnName("scope_digest").HasMaxLength(64);
            entity.Property(operation => operation.ActorIssuer).HasColumnName("actor_issuer").HasMaxLength(512);
            entity.Property(operation => operation.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(operation => operation.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(operation => operation.RequestDigest).HasColumnName("request_digest").HasMaxLength(64).IsRequired();
            entity.Property(operation => operation.ReceiptJson).HasColumnName("receipt").HasColumnType("jsonb").IsRequired();
            entity.Property(operation => operation.CreatedAt).HasColumnName("created_at");
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(operation => operation.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectSkillContentRevocationRecord>(entity =>
        {
            entity.ToTable("project_skill_content_revocations", table =>
                table.HasCheckConstraint("ck_project_skill_revocation_revision", "revision > 0"));
            entity.HasKey(revocation => new
            {
                revocation.ProjectId,
                revocation.SkillId,
                revocation.Revision,
            });
            entity.Property(revocation => revocation.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(revocation => revocation.SkillId).HasColumnName("skill_id").HasMaxLength(256);
            entity.Property(revocation => revocation.Revision).HasColumnName("revision");
            entity.Property(revocation => revocation.RevokedByActorId).HasColumnName("revoked_by_actor_id")
                .HasMaxLength(256).IsRequired();
            entity.Property(revocation => revocation.Reason).HasColumnName("reason").HasMaxLength(2000).IsRequired();
            entity.Property(revocation => revocation.RevokedAt).HasColumnName("revoked_at");
            entity.HasOne<ProjectSkillContentRevisionRecord>()
                .WithMany()
                .HasForeignKey(revocation => new
                {
                    revocation.ProjectId,
                    revocation.SkillId,
                    revocation.Revision,
                })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HostedModelReasoningCatalogRecord>(entity =>
        {
            entity.ToTable("hosted_model_reasoning_catalog", table =>
                table.HasCheckConstraint(
                    "ck_hosted_model_reasoning_supported_efforts",
                    "supported_reasoning_efforts <@ ARRAY['low', 'medium', 'high', 'xhigh']::text[]"));
            entity.HasKey(catalog => new
            {
                catalog.TenantId,
                catalog.ProjectId,
                catalog.ActorId,
                catalog.ConnectionId,
                catalog.BindingsRevision,
                catalog.Reference,
            });
            entity.Property(catalog => catalog.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(catalog => catalog.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(catalog => catalog.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(catalog => catalog.ConnectionId).HasColumnName("connection_id");
            entity.Property(catalog => catalog.BindingsRevision).HasColumnName("bindings_revision").HasMaxLength(256);
            entity.Property(catalog => catalog.Reference).HasColumnName("reference").HasMaxLength(256);
            entity.Property(catalog => catalog.SupportedReasoningEfforts)
                .HasColumnName("supported_reasoning_efforts").HasColumnType("text[]").IsRequired();
            entity.Property(catalog => catalog.VerifiedAt).HasColumnName("verified_at");
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(catalog => catalog.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

/// <summary>Design-time context factory used by EF tooling; it does not connect until the returned context is used.</summary>
public sealed class ProjectsConfigDbContextFactory : IDesignTimeDbContextFactory<ProjectsConfigDbContext>
{
    /// <summary>Creates a context with a placeholder local connection and the service's migration-history schema.</summary>
    public ProjectsConfigDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=projects_config_design;Username=design_time",
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        return new ProjectsConfigDbContext(options);
    }
}
