using Agentweaver.Abstractions;
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
    public DbSet<ModelProviderDraftHeadRecord> ModelProviderDrafts => Set<ModelProviderDraftHeadRecord>();
    public DbSet<ModelProviderDraftRevisionRecord> ModelProviderDraftRevisions =>
        Set<ModelProviderDraftRevisionRecord>();
    public DbSet<ModelProviderReferenceReservationRecord> ModelProviderReferenceReservations =>
        Set<ModelProviderReferenceReservationRecord>();
    public DbSet<ModelProviderCredentialOperationRecord> ModelProviderCredentialOperations =>
        Set<ModelProviderCredentialOperationRecord>();
    public DbSet<ModelProviderCredentialPreflightRecord> ModelProviderCredentialPreflights =>
        Set<ModelProviderCredentialPreflightRecord>();
    public DbSet<ModelProviderHostRequestRecord> ModelProviderHostRequests =>
        Set<ModelProviderHostRequestRecord>();
    public DbSet<ProjectMarketplaceSourceRecord> MarketplaceSources => Set<ProjectMarketplaceSourceRecord>();
    public DbSet<ProjectCastingProposalRecord> CastingProposals => Set<ProjectCastingProposalRecord>();
    public DbSet<ProjectCastingGenerationRequestRecord> CastingGenerationRequests =>
        Set<ProjectCastingGenerationRequestRecord>();
    public DbSet<ProjectSkillContentRevisionRecord> SkillContentRevisions => Set<ProjectSkillContentRevisionRecord>();
    public DbSet<ProjectSkillImportIdempotencyRecord> SkillImportIdempotency =>
        Set<ProjectSkillImportIdempotencyRecord>();
    public DbSet<ProjectSkillContentRevocationRecord> SkillContentRevocations =>
        Set<ProjectSkillContentRevocationRecord>();
    public DbSet<ProjectSkillTrustEvidenceRecord> SkillTrustEvidence => Set<ProjectSkillTrustEvidenceRecord>();
    public DbSet<ProjectSkillAdmissionRecord> SkillAdmissions => Set<ProjectSkillAdmissionRecord>();
    public DbSet<ProjectSkillRefreshReceiptRecord> SkillRefreshReceipts =>
        Set<ProjectSkillRefreshReceiptRecord>();
    public DbSet<ProjectSkillRefreshAuditIntentRecord> SkillRefreshAuditIntents =>
        Set<ProjectSkillRefreshAuditIntentRecord>();
    public DbSet<ProjectSkillAdmissionFenceRecord> SkillAdmissionFences =>
        Set<ProjectSkillAdmissionFenceRecord>();
    public DbSet<ProjectSkillBuiltInReleaseRecord> SkillBuiltInReleases =>
        Set<ProjectSkillBuiltInReleaseRecord>();
    public DbSet<HostedModelReasoningCatalogRecord> HostedModelReasoningCatalog =>
        Set<HostedModelReasoningCatalogRecord>();
    public DbSet<NativeAcsPolicyHeadRecord> NativeAcsPolicyHeads => Set<NativeAcsPolicyHeadRecord>();
    public DbSet<NativeAcsPolicyPreparationRecord> NativeAcsPolicyPreparations =>
        Set<NativeAcsPolicyPreparationRecord>();
    public DbSet<NativeAcsPolicyRevisionRecord> NativeAcsPolicyRevisions =>
        Set<NativeAcsPolicyRevisionRecord>();
    public DbSet<NativeAcsPolicyArtifactFileRecord> NativeAcsPolicyArtifactFiles =>
        Set<NativeAcsPolicyArtifactFileRecord>();
    public DbSet<NativeAcsPolicyIdempotencyRecord> NativeAcsPolicyIdempotency =>
        Set<NativeAcsPolicyIdempotencyRecord>();
    public DbSet<NativeAcsPolicyChangeAuditRecord> NativeAcsPolicyChangeAudit =>
        Set<NativeAcsPolicyChangeAuditRecord>();
    public DbSet<NativeAcsPolicyOutboxRecord> NativeAcsPolicyOutbox =>
        Set<NativeAcsPolicyOutboxRecord>();
    public DbSet<ProjectGovernanceDecisionStreamHeadRecord> GovernanceDecisionStreamHeads =>
        Set<ProjectGovernanceDecisionStreamHeadRecord>();
    public DbSet<ProjectGovernanceDecisionRecord> GovernanceDecisions =>
        Set<ProjectGovernanceDecisionRecord>();
    public DbSet<ProjectGovernanceDecisionOutboxRecord> GovernanceDecisionOutbox =>
        Set<ProjectGovernanceDecisionOutboxRecord>();

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
            // Keep revoked grants as history while allowing one active assignment for the same member, resource, and role.
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

        modelBuilder.Entity<ModelProviderReferenceReservationRecord>(entity =>
        {
            entity.ToTable("model_provider_reference_reservations", table =>
            {
                table.HasCheckConstraint("ck_provider_reference_syntax",
                    "reference COLLATE \"C\" ~ '^[A-Za-z0-9.-]([A-Za-z0-9._-]{0,254}[A-Za-z0-9.-])?$' AND position('__' in reference) = 0");
                table.HasCheckConstraint("ck_provider_reference_folded",
                    "reference_folded = translate(reference COLLATE \"C\", 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')");
                table.HasCheckConstraint("ck_provider_reference_creation_revision", "creation_revision = 1");
            });
            entity.HasKey(item => item.ReferenceFolded).HasName("pk_model_provider_reference_reservations");
            entity.Property(item => item.Reference).HasColumnName("reference").HasMaxLength(256).IsRequired();
            entity.Property(item => item.ReferenceFolded).HasColumnName("reference_folded")
                .HasMaxLength(256).UseCollation("C");
            entity.Property(item => item.ProviderId).HasColumnName("provider_id").HasMaxLength(128).IsRequired();
            entity.Property(item => item.CreationRevision).HasColumnName("creation_revision");
            entity.Property(item => item.OperationId).HasColumnName("operation_id");
            entity.Property(item => item.InstallationId).HasColumnName("installation_id");
            entity.Property(item => item.Issuer).HasColumnName("issuer").IsRequired();
            entity.Property(item => item.TenantId).HasColumnName("tenant_id").IsRequired();
            entity.Property(item => item.Subject).HasColumnName("subject").IsRequired();
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.HasAlternateKey(item => item.ProviderId)
                .HasName("ux_model_provider_reference_reservations_provider_id");
            entity.HasAlternateKey(item => item.OperationId)
                .HasName("ux_model_provider_reference_reservations_operation_id");
            entity.HasOne<ModelProviderDraftRevisionRecord>().WithMany()
                .HasForeignKey(item => new { item.ProviderId, item.CreationRevision })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ModelProviderDraftHeadRecord>(entity =>
        {
            entity.ToTable("model_providers", table =>
            {
                table.HasCheckConstraint(
                    "ck_model_providers_scope_project",
                    "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                table.HasCheckConstraint("ck_model_providers_state", "state IN ('Draft', 'Available')");
                table.HasCheckConstraint("ck_model_providers_revision", "current_revision > 0");
                table.HasCheckConstraint(
                    "ck_model_providers_reasoning_efforts",
                    "cardinality(reasoning_efforts) <= 6 AND " +
                    "reasoning_efforts <@ ARRAY['none', 'minimal', 'low', 'medium', 'high', 'xhigh']::text[]");
                table.HasCheckConstraint(
                    "ck_model_providers_prompt_capacity",
                    "prompt_capacity_tokens IS NULL OR prompt_capacity_tokens > 0");
            });
            entity.HasKey(head => head.ProviderId).HasName("pk_model_providers");
            entity.Property(head => head.ProviderId).HasColumnName("provider_id")
                .HasMaxLength(ModelProviderDraftLimits.ProviderId);
            entity.Property(head => head.Scope).HasColumnName("scope")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(head => head.ProjectId).HasColumnName("project_id")
                .HasMaxLength(ModelProviderDraftLimits.ProjectId);
            entity.Property(head => head.DisplayName).HasColumnName("display_name")
                .HasMaxLength(ModelProviderDraftLimits.DisplayName).IsRequired();
            entity.Property(head => head.ProviderType).HasColumnName("type")
                .HasMaxLength(16).IsRequired();
            entity.Property(head => head.BaseUrl).HasColumnName("base_url")
                .HasMaxLength(ModelProviderDraftLimits.BaseUrl).IsRequired();
            entity.Property(head => head.Deployment).HasColumnName("deployment")
                .HasMaxLength(ModelProviderDraftLimits.Deployment).IsRequired();
            entity.Property(head => head.WireApi).HasColumnName("wire_api")
                .HasMaxLength(ModelProviderDraftLimits.WireApi);
            entity.Property(head => head.AzureApiVersion).HasColumnName("azure_api_version")
                .HasMaxLength(ModelProviderDraftLimits.AzureApiVersion);
            entity.Property(head => head.ReasoningEfforts).HasColumnName("reasoning_efforts")
                .HasColumnType("text[]").IsRequired();
            entity.Property(head => head.PromptCapacityTokens).HasColumnName("prompt_capacity_tokens");
            entity.Property(head => head.State).HasColumnName("state")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(head => head.CurrentRevision).HasColumnName("current_revision").IsConcurrencyToken();
            entity.Property(head => head.CreatedByActorId).HasColumnName("created_by_actor_id")
                .HasMaxLength(ModelProviderDraftLimits.ActorId).IsRequired();
            entity.Property(head => head.CreatedAt).HasColumnName("created_at");
            entity.Property(head => head.UpdatedByActorId).HasColumnName("updated_by_actor_id")
                .HasMaxLength(ModelProviderDraftLimits.ActorId).IsRequired();
            entity.Property(head => head.UpdatedAt).HasColumnName("updated_at");
            entity.Property<string>("WriteTransactionId").HasColumnName("write_transaction_id")
                .HasColumnType("text").HasDefaultValue("legacy").IsRequired().ValueGeneratedOnAddOrUpdate();
            entity.HasOne<ProjectRecord>()
                .WithMany()
                .HasForeignKey(head => head.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(head => new { head.Scope, head.ProjectId, head.DisplayName })
                .HasDatabaseName("ix_model_providers_scope_project_name");
        });

        modelBuilder.Entity<ModelProviderDraftRevisionRecord>(entity =>
        {
            entity.ToTable("model_provider_revisions", table =>
            {
                table.HasCheckConstraint("ck_model_provider_revisions_revision", "provider_revision > 0");
                table.HasCheckConstraint(
                    "ck_model_provider_revisions_scope_project",
                    "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_model_provider_revisions_request_fingerprint",
                    "request_fingerprint ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_revisions_configuration_hash",
                    "configuration_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_revisions_reasoning_efforts",
                    "cardinality(reasoning_efforts) <= 6 AND " +
                    "reasoning_efforts <@ ARRAY['none', 'minimal', 'low', 'medium', 'high', 'xhigh']::text[]");
                table.HasCheckConstraint(
                    "ck_model_provider_revisions_prompt_capacity",
                    "prompt_capacity_tokens IS NULL OR prompt_capacity_tokens > 0");
            });
            entity.HasKey(revision => new { revision.ProviderId, revision.ProviderRevision })
                .HasName("pk_model_provider_revisions");
            entity.Property(revision => revision.ProviderId).HasColumnName("provider_id")
                .HasMaxLength(ModelProviderDraftLimits.ProviderId);
            entity.Property(revision => revision.Scope).HasColumnName("scope")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(revision => revision.ProjectId).HasColumnName("project_id")
                .HasMaxLength(ModelProviderDraftLimits.ProjectId);
            entity.Property(revision => revision.ProviderRevision).HasColumnName("provider_revision");
            entity.Property(revision => revision.OperationId).HasColumnName("operation_id");
            entity.Property(revision => revision.RequestFingerprint).HasColumnName("request_fingerprint")
                .HasMaxLength(ModelProviderDraftLimits.Hash).IsRequired();
            entity.Property(revision => revision.DisplayName).HasColumnName("display_name")
                .HasMaxLength(ModelProviderDraftLimits.DisplayName).IsRequired();
            entity.Property(revision => revision.ProviderType).HasColumnName("type")
                .HasMaxLength(16).IsRequired();
            entity.Property(revision => revision.BaseUrl).HasColumnName("base_url")
                .HasMaxLength(ModelProviderDraftLimits.BaseUrl).IsRequired();
            entity.Property(revision => revision.Deployment).HasColumnName("deployment")
                .HasMaxLength(ModelProviderDraftLimits.Deployment).IsRequired();
            entity.Property(revision => revision.WireApi).HasColumnName("wire_api")
                .HasMaxLength(ModelProviderDraftLimits.WireApi);
            entity.Property(revision => revision.AzureApiVersion).HasColumnName("azure_api_version")
                .HasMaxLength(ModelProviderDraftLimits.AzureApiVersion);
            entity.Property(revision => revision.ReasoningEfforts).HasColumnName("reasoning_efforts")
                .HasColumnType("text[]").IsRequired();
            entity.Property(revision => revision.PromptCapacityTokens).HasColumnName("prompt_capacity_tokens");
            entity.Property(revision => revision.ConfigurationHash).HasColumnName("configuration_hash")
                .HasMaxLength(ModelProviderDraftLimits.Hash).IsRequired();
            entity.Property(revision => revision.CredentialVersion).HasColumnName("credential_version")
                .HasMaxLength(256);
            entity.Property(revision => revision.CredentialSlotId).HasColumnName("credential_slot_id");
            entity.Property(revision => revision.CredentialSecretId).HasColumnName("credential_secret_id")
                .HasMaxLength(256);
            entity.Property(revision => revision.RateCardId).HasColumnName("rate_card_id")
                .HasMaxLength(128);
            entity.Property(revision => revision.RateCardVersion).HasColumnName("rate_card_version")
                .HasMaxLength(64);
            entity.Property(revision => revision.RateCardPinJson).HasColumnName("rate_card_pin_json")
                .HasColumnType("jsonb");
            entity.Property(revision => revision.CreatedByActorId).HasColumnName("created_by_actor_id")
                .HasMaxLength(ModelProviderDraftLimits.ActorId).IsRequired();
            entity.Property(revision => revision.CreatedAt).HasColumnName("created_at");
            entity.Property<string>("WriteTransactionId").HasColumnName("write_transaction_id")
                .HasColumnType("text").HasDefaultValue("legacy").IsRequired().ValueGeneratedOnAddOrUpdate();
            entity.HasOne<ModelProviderDraftHeadRecord>()
                .WithMany()
                .HasForeignKey(revision => revision.ProviderId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(revision => revision.OperationId)
                .IsUnique()
                .HasDatabaseName("ux_model_provider_revisions_operation_id");
        });

        modelBuilder.Entity<ModelProviderCredentialOperationRecord>(entity =>
        {
            entity.ToTable("model_provider_credential_operations", table =>
            {
                table.HasCheckConstraint(
                    "ck_model_provider_credential_operations_kind",
                    "operation_kind IN ('Publish', 'Cancel', 'Disable')");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_operations_scope_project",
                    "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_operations_fingerprint",
                    "request_fingerprint ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_operations_shape",
                    "(operation_kind = 'Cancel' AND provider_revision IS NULL AND expected_provider_revision IS NULL AND original_provider_revision IS NULL AND original_credential_revision IS NULL AND credential_slot_id IS NULL AND credential_secret_id IS NULL AND credential_version IS NULL AND configuration_hash IS NULL AND provider_state IS NULL) OR " +
                    "(operation_kind = 'Publish' AND provider_revision > 0 AND expected_provider_revision > 0 AND provider_revision = expected_provider_revision + 1 AND original_provider_revision IS NULL AND original_credential_revision IS NULL AND credential_slot_id IS NOT NULL AND credential_secret_id IS NOT NULL AND credential_version ~ '^[0-9a-fA-F]{32}$' AND configuration_hash ~ '^[0-9a-f]{64}$' AND (provider_state IS NULL OR provider_state IN ('Draft', 'Available'))) OR " +
                    "(operation_kind = 'Disable' AND provider_revision > 0 AND original_provider_revision > 0 AND provider_revision = original_provider_revision + 1 AND original_credential_revision > 0 AND (expected_provider_revision IS NULL OR expected_provider_revision = original_provider_revision) AND credential_secret_id IS NULL AND credential_version IS NULL AND configuration_hash ~ '^[0-9a-f]{64}$' AND (provider_state IS NULL OR provider_state = 'Draft'))");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_operations_provider_state",
                    "provider_state IS NULL OR provider_state IN ('Draft', 'Available')");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_operations_rate_card",
                    "(provider_state = 'Available' AND operation_kind = 'Publish' AND rate_card_id IS NOT NULL AND length(rate_card_id) > 0 AND rate_card_version IS NOT NULL AND length(rate_card_version) > 0 AND rate_card_pin_hash ~ '^[0-9a-f]{64}$') OR " +
                    "(provider_state IS DISTINCT FROM 'Available' AND rate_card_id IS NULL AND rate_card_version IS NULL AND rate_card_pin_hash IS NULL)");
            });
            entity.HasKey(op => op.OperationId).HasName("pk_model_provider_credential_operations");
            entity.Property(op => op.OperationId).HasColumnName("operation_id");
            entity.Property(op => op.OperationKind).HasColumnName("operation_kind")
                .HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(op => op.ProviderId).HasColumnName("provider_id")
                .HasMaxLength(ModelProviderDraftLimits.ProviderId).IsRequired();
            entity.Property(op => op.Scope).HasColumnName("scope")
                .HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(op => op.ProjectId).HasColumnName("project_id")
                .HasMaxLength(ModelProviderDraftLimits.ProjectId);
            entity.Property(op => op.ProviderRevision).HasColumnName("provider_revision");
            entity.Property(op => op.ExpectedProviderRevision).HasColumnName("expected_provider_revision");
            entity.Property(op => op.OriginalProviderRevision).HasColumnName("original_provider_revision");
            entity.Property(op => op.OriginalCredentialRevision).HasColumnName("original_credential_revision");
            entity.Property(op => op.CredentialSlotId).HasColumnName("credential_slot_id");
            entity.Property(op => op.CredentialSecretId).HasColumnName("credential_secret_id")
                .HasMaxLength(256);
            entity.Property(op => op.CredentialVersion).HasColumnName("credential_version")
                .HasMaxLength(32);
            entity.Property(op => op.ConfigurationHash).HasColumnName("configuration_hash")
                .HasMaxLength(ModelProviderDraftLimits.Hash);
            entity.Property(op => op.ProviderState).HasColumnName("provider_state")
                .HasMaxLength(16);
            entity.Property(op => op.RateCardId).HasColumnName("rate_card_id")
                .HasMaxLength(128);
            entity.Property(op => op.RateCardVersion).HasColumnName("rate_card_version")
                .HasMaxLength(64);
            entity.Property(op => op.RateCardPinHash).HasColumnName("rate_card_pin_hash")
                .HasMaxLength(ModelProviderDraftLimits.Hash);
            entity.Property(op => op.OriginalTenant).HasColumnName("original_tenant")
                .HasMaxLength(256).IsRequired();
            entity.Property(op => op.OriginalMembershipId).HasColumnName("original_membership_id");
            entity.Property(op => op.OriginalMembershipRevision).HasColumnName("original_membership_revision");
            entity.Property(op => op.CurrentMembershipId).HasColumnName("current_membership_id");
            entity.Property(op => op.CurrentMembershipRevision).HasColumnName("current_membership_revision");
            entity.Property(op => op.CurrentAssignmentId).HasColumnName("current_assignment_id");
            entity.Property(op => op.CurrentAssignmentRevision).HasColumnName("current_assignment_revision");
            entity.Property(op => op.Issuer).HasColumnName("issuer").HasMaxLength(2048).IsRequired();
            entity.Property(op => op.ActorId).HasColumnName("actor_id")
                .HasMaxLength(ModelProviderDraftLimits.ActorId).IsRequired();
            entity.Property(op => op.RequestFingerprint).HasColumnName("request_fingerprint")
                .HasMaxLength(ModelProviderDraftLimits.Hash).IsRequired();
            entity.Property(op => op.RecordedAt).HasColumnName("recorded_at");
            entity.Property<string>("WriteTransactionId").HasColumnName("write_transaction_id")
                .HasColumnType("text").HasDefaultValue("legacy").IsRequired().ValueGeneratedOnAddOrUpdate();
            entity.HasIndex(op => new { op.ProviderId, op.RecordedAt })
                .HasDatabaseName("ix_model_provider_credential_operations_provider_recorded");
        });

        modelBuilder.Entity<ModelProviderCredentialPreflightRecord>(entity =>
        {
            entity.ToTable("model_provider_credential_preflights", table =>
            {
                table.HasCheckConstraint(
                    "ck_model_provider_credential_preflights_mode",
                    "mode IN ('draft-snapshot', 'authority-only')");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_preflights_scope_project",
                    "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_preflights_consumed",
                    "(consumed_at IS NULL AND consumed_by_kind IS NULL) OR " +
                    "(consumed_at IS NOT NULL AND consumed_by_kind IN ('Publish', 'Cancel', 'Disable'))");
            });
            entity.HasKey(p => p.PreflightId).HasName("pk_model_provider_credential_preflights");
            entity.Property(p => p.PreflightId).HasColumnName("preflight_id");
            entity.Property(p => p.OperationId).HasColumnName("operation_id");
            entity.Property(p => p.Mode).HasColumnName("mode").HasMaxLength(16).IsRequired();
            entity.Property(p => p.Scope).HasColumnName("scope")
                .HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(p => p.ProjectId).HasColumnName("project_id")
                .HasMaxLength(ModelProviderDraftLimits.ProjectId);
            entity.Property(p => p.ProviderId).HasColumnName("provider_id")
                .HasMaxLength(ModelProviderDraftLimits.ProviderId).IsRequired();
            entity.Property(p => p.Issuer).HasColumnName("issuer").HasMaxLength(2048).IsRequired();
            entity.Property(p => p.ActorId).HasColumnName("actor_id")
                .HasMaxLength(ModelProviderDraftLimits.ActorId).IsRequired();
            entity.Property(p => p.TenantId).HasColumnName("tenant_id").HasMaxLength(256).IsRequired();
            entity.Property(p => p.MembershipId).HasColumnName("membership_id");
            entity.Property(p => p.MembershipRevision).HasColumnName("membership_revision");
            entity.Property(p => p.AssignmentId).HasColumnName("assignment_id");
            entity.Property(p => p.AssignmentRevision).HasColumnName("assignment_revision");
            entity.Property(p => p.IssuedAt).HasColumnName("issued_at");
            entity.Property(p => p.ConsumedAt).HasColumnName("consumed_at");
            entity.Property(p => p.ConsumedByKind).HasColumnName("consumed_by_kind")
                .HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(p => new { p.OperationId, p.IssuedAt })
                .HasDatabaseName("ix_model_provider_credential_preflights_operation_issued");
        });

        modelBuilder.Entity<ModelProviderHostRequestRecord>(entity =>
        {
            entity.ToTable("model_provider_host_requests", table =>
            {
                table.HasCheckConstraint(
                    "ck_model_provider_host_requests_scope_project",
                    "(scope = 'Platform' AND project_id IS NULL) OR (scope = 'Project' AND project_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_model_provider_host_requests_revision",
                    "provider_revision > 0");
                table.HasCheckConstraint(
                    "ck_model_provider_host_requests_configuration_hash",
                    "configuration_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_host_requests_port",
                    "port = 443");
                table.HasCheckConstraint(
                    "ck_model_provider_host_requests_delivery_state",
                    "delivery_state IN ('Pending', 'Delivered', 'Failed')");
                table.HasCheckConstraint(
                    "ck_model_provider_host_requests_attempt_count",
                    "attempt_count >= 0");
            });
            entity.HasKey(r => r.RequestId).HasName("pk_model_provider_host_requests");
            entity.Property(r => r.RequestId).HasColumnName("request_id");
            entity.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(256).IsRequired();
            entity.Property(r => r.Scope).HasColumnName("scope").HasConversion<string>().HasMaxLength(16);
            entity.Property(r => r.ProjectId).HasColumnName("project_id").HasMaxLength(ModelProviderDraftLimits.ProjectId);
            entity.Property(r => r.ProviderId).HasColumnName("provider_id").HasMaxLength(ModelProviderDraftLimits.ProviderId).IsRequired();
            entity.Property(r => r.ProviderRevision).HasColumnName("provider_revision");
            entity.Property(r => r.ConfigurationHash).HasColumnName("configuration_hash").HasMaxLength(ModelProviderDraftLimits.Hash).IsRequired();
            entity.Property(r => r.OperationId).HasColumnName("operation_id");
            entity.Property(r => r.ActorId).HasColumnName("actor_id").HasMaxLength(ModelProviderDraftLimits.ActorId).IsRequired();
            entity.Property(r => r.Host).HasColumnName("host").HasMaxLength(253).IsRequired();
            entity.Property(r => r.Port).HasColumnName("port");
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.DeliveryState).HasColumnName("delivery_state").HasConversion<string>().HasMaxLength(16);
            entity.Property(r => r.AttemptCount).HasColumnName("attempt_count");
            entity.Property(r => r.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(r => r.LeaseOwner).HasColumnName("lease_owner").HasMaxLength(128);
            entity.Property(r => r.LeaseToken).HasColumnName("lease_token");
            entity.Property(r => r.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(r => r.EnvironmentDestinationId).HasColumnName("environment_destination_id");
            entity.Property(r => r.DeliveredAt).HasColumnName("delivered_at");
            entity.Property(r => r.FailureCode).HasColumnName("failure_code").HasMaxLength(64);
            entity.Property(r => r.LastAttemptAt).HasColumnName("last_attempt_at");
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
            entity.HasOne<ModelProviderDraftRevisionRecord>()
                .WithMany()
                .HasForeignKey(r => new { r.ProviderId, r.ProviderRevision })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(r => new { r.ProviderId, r.ProviderRevision })
                .IsUnique()
                .HasDatabaseName("ux_model_provider_host_requests_provider_revision");
            entity.HasIndex(r => r.OperationId)
                .IsUnique()
                .HasDatabaseName("ux_model_provider_host_requests_operation_id");
            entity.HasIndex(r => new { r.DeliveryState, r.NextAttemptAt, r.CreatedAt })
                .HasDatabaseName("ix_model_provider_host_requests_outbox");
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
            entity.Property(revision => revision.AdmissionClassification)
                .HasColumnName("admission_classification").HasConversion<string>().HasMaxLength(32)
                .HasDefaultValue(ProjectSkillAdmissionClassification.LegacyThirdParty);
            entity.Property(revision => revision.SourceAuthorityKind)
                .HasColumnName("source_authority_kind").HasConversion<string>().HasMaxLength(32)
                .HasDefaultValue(ProjectSkillSourceAuthorityKind.CallerClaimed);
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

        modelBuilder.Entity<ProjectSkillTrustEvidenceRecord>(entity =>
        {
            entity.ToTable("project_skill_trust_evidence", table =>
            {
                table.HasCheckConstraint("ck_project_skill_trust_evidence_revision", "revision > 0");
                table.HasCheckConstraint(
                    "ck_project_skill_trust_evidence_hashes",
                    "request_fingerprint ~ '^[0-9a-f]{64}$' AND content_digest ~ '^[0-9a-f]{64}$' " +
                    "AND report_set_sha256 ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_project_skill_trust_evidence_report_size",
                    "octet_length(report_set) BETWEEN 1 AND 1048576");
            });
            entity.HasKey(record => record.EvidenceId);
            entity.Property(record => record.EvidenceId).HasColumnName("evidence_id");
            entity.Property(record => record.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(record => record.SkillId).HasColumnName("skill_id").HasMaxLength(256);
            entity.Property(record => record.Revision).HasColumnName("revision");
            entity.Property(record => record.ActorIssuer).HasColumnName("actor_issuer").HasMaxLength(512);
            entity.Property(record => record.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(record => record.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(record => record.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
            entity.Property(record => record.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
            entity.Property(record => record.SourceId).HasColumnName("source_id").HasMaxLength(256);
            entity.Property(record => record.SourceRevision).HasColumnName("source_revision").HasMaxLength(256);
            entity.Property(record => record.RequestedRef).HasColumnName("requested_ref").HasMaxLength(256);
            entity.Property(record => record.ResolvedCommitSha).HasColumnName("resolved_commit_sha").HasMaxLength(64);
            entity.Property(record => record.SelectedPath).HasColumnName("selected_path").HasMaxLength(1024);
            entity.Property(record => record.ReportSetJson).HasColumnName("report_set").HasColumnType("bytea");
            entity.Property(record => record.ReportSetSha256).HasColumnName("report_set_sha256").HasMaxLength(64);
            entity.Property(record => record.ScoreStatus).HasColumnName("score_status")
                .HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.HeuristicScore).HasColumnName("heuristic_score");
            entity.Property(record => record.TrustTier).HasColumnName("trust_tier")
                .HasConversion<string>().HasMaxLength(32);
            entity.Property(record => record.SignatureVerified).HasColumnName("signature_verified");
            entity.Property(record => record.CapabilityDeclarationValid)
                .HasColumnName("capability_declaration_valid");
            entity.Property(record => record.ShortDescription).HasColumnName("short_description");
            entity.Property(record => record.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(record => new
                {
                    record.ProjectId,
                    record.ActorIssuer,
                    record.ActorId,
                    record.IdempotencyKey,
                })
                .IsUnique()
                .HasDatabaseName("ux_project_skill_trust_evidence_idempotency");
            entity.HasOne<ProjectSkillContentRevisionRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.ProjectId, record.SkillId, record.Revision })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectSkillAdmissionFenceRecord>(entity =>
        {
            entity.ToTable("project_skill_admission_fence", table =>
            {
                table.HasCheckConstraint("ck_skill_admission_fence_id", "id = 1");
                table.HasCheckConstraint("ck_skill_admission_fence_generation", "generation > 0");
                table.HasCheckConstraint(
                    "ck_skill_admission_fence_cutover_generation", "cutover_generation > 0");
                table.HasCheckConstraint(
                    "ck_skill_admission_fence_state", "state IN ('Open', 'Draining', 'Active')");
            });
            entity.HasKey(fence => fence.Id);
            entity.Property(fence => fence.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(fence => fence.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            entity.Property(fence => fence.Generation).HasColumnName("generation");
            entity.Property(fence => fence.CutoverGeneration).HasColumnName("cutover_generation");
            entity.Property(fence => fence.ReleaseInventoryRevision).HasColumnName("release_inventory_revision")
                .HasMaxLength(128);
            entity.Property(fence => fence.PlatformSkillsPolicySha256).HasColumnName("platform_skills_policy_sha256")
                .HasMaxLength(64);
            entity.Property(fence => fence.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<ProjectSkillBuiltInReleaseRecord>(entity =>
        {
            entity.ToTable("project_skill_builtin_releases", table =>
                table.HasCheckConstraint(
                    "ck_skill_builtin_release_hashes",
                    "content_digest ~ '^[0-9a-f]{64}$' AND resolved_commit_sha ~ '^[0-9a-f]{40,64}$' " +
                    "AND (signature_sha256 IS NULL OR signature_sha256 ~ '^[0-9a-f]{64}$')"));
            entity.HasKey(release => release.ReleaseId);
            entity.Property(release => release.ReleaseId).HasColumnName("release_id");
            entity.Property(release => release.InventoryRevision).HasColumnName("inventory_revision").HasMaxLength(128);
            entity.Property(release => release.Repository).HasColumnName("repository").HasMaxLength(256);
            entity.Property(release => release.ResolvedCommitSha).HasColumnName("resolved_commit_sha").HasMaxLength(64);
            entity.Property(release => release.SelectedPath).HasColumnName("selected_path").HasMaxLength(1024);
            entity.Property(release => release.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
            entity.Property(release => release.TrustedPublisherId).HasColumnName("trusted_publisher_id").HasMaxLength(256);
            entity.Property(release => release.SignatureSha256).HasColumnName("signature_sha256").HasMaxLength(64);
            entity.Property(release => release.IsActive).HasColumnName("is_active");
            entity.Property(release => release.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(release => new
                {
                    release.InventoryRevision,
                    release.Repository,
                    release.ResolvedCommitSha,
                    release.SelectedPath,
                    release.ContentDigest,
                })
                .IsUnique()
                .HasDatabaseName("ux_project_skill_builtin_release_artifact");
        });

        modelBuilder.Entity<ProjectSkillAdmissionRecord>(entity =>
        {
            entity.ToTable("project_skill_admissions");
            entity.HasKey(admission => admission.AdmissionId);
            entity.Property(admission => admission.AdmissionId).HasColumnName("admission_id");
            entity.Property(admission => admission.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(admission => admission.SkillId).HasColumnName("skill_id").HasMaxLength(256);
            entity.Property(admission => admission.Revision).HasColumnName("revision");
            entity.Property(admission => admission.EvidenceId).HasColumnName("evidence_id");
            entity.Property(admission => admission.CutoverGeneration).HasColumnName("cutover_generation");
            entity.Property(admission => admission.Classification).HasColumnName("classification")
                .HasConversion<string>().HasMaxLength(32);
            entity.Property(admission => admission.SourceAuthorityKind).HasColumnName("source_authority_kind")
                .HasConversion<string>().HasMaxLength(32);
            entity.Property(admission => admission.VerifiedSourceId).HasColumnName("verified_source_id").HasMaxLength(256);
            entity.Property(admission => admission.VerifiedSourceRevision).HasColumnName("verified_source_revision")
                .HasMaxLength(256);
            entity.Property(admission => admission.VerifiedRequestedRef).HasColumnName("verified_requested_ref")
                .HasMaxLength(256);
            entity.Property(admission => admission.VerifiedResolvedCommitSha)
                .HasColumnName("verified_resolved_commit_sha").HasMaxLength(64);
            entity.Property(admission => admission.VerifiedSelectedPath).HasColumnName("verified_selected_path")
                .HasMaxLength(1024);
            entity.Property(admission => admission.ReleaseId).HasColumnName("release_id");
            entity.Property(admission => admission.TrustedPublisherId).HasColumnName("trusted_publisher_id")
                .HasMaxLength(256);
            entity.Property(admission => admission.SignatureSha256).HasColumnName("signature_sha256").HasMaxLength(64);
            entity.Property(admission => admission.PlatformPolicyId).HasColumnName("platform_policy_id").HasMaxLength(256);
            entity.Property(admission => admission.PlatformPolicyRevision).HasColumnName("platform_policy_revision");
            entity.Property(admission => admission.PlatformPolicySha256).HasColumnName("platform_policy_sha256")
                .HasMaxLength(64);
            entity.Property(admission => admission.ProjectPolicyId).HasColumnName("project_policy_id").HasMaxLength(256);
            entity.Property(admission => admission.ProjectPolicyRevision).HasColumnName("project_policy_revision");
            entity.Property(admission => admission.ProjectPolicySha256).HasColumnName("project_policy_sha256")
                .HasMaxLength(64);
            entity.Property(admission => admission.CreatedAt).HasColumnName("created_at");
            entity.HasOne<ProjectSkillContentRevisionRecord>()
                .WithMany()
                .HasForeignKey(admission => new { admission.ProjectId, admission.SkillId, admission.Revision })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectSkillTrustEvidenceRecord>()
                .WithMany()
                .HasForeignKey(admission => admission.EvidenceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasAlternateKey(admission => new { admission.AdmissionId, admission.EvidenceId });
        });

        modelBuilder.Entity<ProjectSkillRefreshReceiptRecord>(entity =>
        {
            entity.ToTable("project_skill_refresh_receipts", table =>
            {
                table.HasCheckConstraint(
                    "ck_project_skill_refresh_receipt_hashes",
                    "request_fingerprint ~ '^[0-9a-f]{64}$' AND content_digest ~ '^[0-9a-f]{64}$' " +
                    "AND platform_policy_sha256 ~ '^[0-9a-f]{64}$' AND " +
                    "(project_policy_sha256 IS NULL OR project_policy_sha256 ~ '^[0-9a-f]{64}$')");
                table.HasCheckConstraint(
                    "ck_project_skill_refresh_receipt_project_pin",
                    "(project_policy_id IS NULL AND project_policy_revision IS NULL AND project_policy_sha256 IS NULL) OR " +
                    "(project_policy_id IS NOT NULL AND project_policy_revision IS NOT NULL AND project_policy_sha256 IS NOT NULL)");
            });
            entity.HasKey(record => record.ReceiptId);
            entity.Property(record => record.ReceiptId).HasColumnName("receipt_id");
            entity.Property(record => record.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(record => record.RunId).HasColumnName("run_id").HasMaxLength(256);
            entity.Property(record => record.AgentId).HasColumnName("agent_id").HasMaxLength(256);
            entity.Property(record => record.SkillId).HasColumnName("skill_id").HasMaxLength(256);
            entity.Property(record => record.Revision).HasColumnName("revision");
            entity.Property(record => record.ContentDigest).HasColumnName("content_digest").HasMaxLength(64);
            entity.Property(record => record.ActorIssuer).HasColumnName("actor_issuer").HasMaxLength(512);
            entity.Property(record => record.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(record => record.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(record => record.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
            entity.Property(record => record.EvidenceId).HasColumnName("evidence_id");
            entity.Property(record => record.AdmissionId).HasColumnName("admission_id");
            entity.Property(record => record.PlatformPolicyId).HasColumnName("platform_policy_id").HasMaxLength(256);
            entity.Property(record => record.PlatformPolicyRevision).HasColumnName("platform_policy_revision");
            entity.Property(record => record.PlatformPolicySha256).HasColumnName("platform_policy_sha256").HasMaxLength(64);
            entity.Property(record => record.ProjectPolicyId).HasColumnName("project_policy_id").HasMaxLength(256);
            entity.Property(record => record.ProjectPolicyRevision).HasColumnName("project_policy_revision");
            entity.Property(record => record.ProjectPolicySha256).HasColumnName("project_policy_sha256").HasMaxLength(64);
            entity.Property(record => record.ObservedAt).HasColumnName("observed_at");
            entity.Property(record => record.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(record => new
                {
                    record.ProjectId,
                    record.ActorIssuer,
                    record.ActorId,
                    record.IdempotencyKey,
                })
                .IsUnique()
                .HasDatabaseName("ux_project_skill_refresh_receipts_idempotency");
            entity.HasIndex(record => record.EvidenceId)
                .IsUnique()
                .HasDatabaseName("ux_project_skill_refresh_receipts_evidence");
            entity.HasOne<ProjectSkillTrustEvidenceRecord>()
                .WithMany()
                .HasForeignKey(record => record.EvidenceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectSkillAdmissionRecord>()
                .WithMany()
                .HasForeignKey(record => new { record.AdmissionId, record.EvidenceId })
                .HasPrincipalKey(admission => new { admission.AdmissionId, admission.EvidenceId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasAlternateKey(record => new
            {
                record.ReceiptId,
                record.EvidenceId,
                record.AdmissionId,
            });
        });

        modelBuilder.Entity<ProjectSkillRefreshAuditIntentRecord>(entity =>
        {
            entity.ToTable("project_skill_refresh_audit_intents");
            entity.HasKey(record => record.AuditIntentId);
            entity.Property(record => record.AuditIntentId).HasColumnName("audit_intent_id");
            entity.Property(record => record.ReceiptId).HasColumnName("receipt_id");
            entity.Property(record => record.EvidenceId).HasColumnName("evidence_id");
            entity.Property(record => record.AdmissionId).HasColumnName("admission_id");
            entity.Property(record => record.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(record => record.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
            entity.Property(record => record.ActorIssuer).HasColumnName("actor_issuer").HasMaxLength(512);
            entity.Property(record => record.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(record => record.MembershipId).HasColumnName("membership_id");
            entity.Property(record => record.MembershipRevision).HasColumnName("membership_revision");
            entity.Property(record => record.EffectiveRoleRevisionsJson)
                .HasColumnName("effective_role_revisions").HasColumnType("jsonb");
            entity.Property(record => record.Reason).HasColumnName("reason").HasMaxLength(2000);
            entity.Property(record => record.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            entity.Property(record => record.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(record => record.EvidenceId)
                .IsUnique()
                .HasDatabaseName("ux_project_skill_refresh_audit_evidence");
            entity.HasOne<ProjectSkillRefreshReceiptRecord>()
                .WithMany()
                .HasForeignKey(record => new
                {
                    record.ReceiptId,
                    record.EvidenceId,
                    record.AdmissionId,
                })
                .HasPrincipalKey(receipt => new
                {
                    receipt.ReceiptId,
                    receipt.EvidenceId,
                    receipt.AdmissionId,
                })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HostedModelReasoningCatalogRecord>(entity =>
        {
            entity.ToTable("hosted_model_reasoning_catalog", table =>
                table.HasCheckConstraint(
                    "ck_hosted_model_reasoning_supported_efforts",
                    "supported_reasoning_efforts <@ ARRAY['none', 'minimal', 'low', 'medium', 'high', 'xhigh']::text[]"));
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

        modelBuilder.Entity<NativeAcsPolicyHeadRecord>(entity =>
        {
            entity.ToTable("native_acs_policy_heads", table =>
            {
                table.HasCheckConstraint(
                    "ck_native_acs_policy_heads_scope",
                    "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                table.HasCheckConstraint("ck_native_acs_policy_heads_revision", "current_revision > 0");
            });
            entity.HasKey(head => new { head.ScopeType, head.ScopeId, head.PolicyId });
            entity.HasIndex(head => new { head.ScopeType, head.ScopeId })
                .IsUnique()
                .HasDatabaseName("ux_native_acs_policy_heads_scope");
            entity.Property(head => head.ScopeType).HasColumnName("scope_type").HasConversion<string>().HasMaxLength(16);
            entity.Property(head => head.ScopeId).HasColumnName("scope_id").HasMaxLength(256);
            entity.Property(head => head.PolicyId).HasColumnName("policy_id").HasMaxLength(128);
            entity.Property(head => head.CurrentRevision).HasColumnName("current_revision").IsConcurrencyToken();
            entity.Property(head => head.CreatedAt).HasColumnName("created_at");
            entity.Property(head => head.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<NativeAcsPolicyPreparationRecord>(entity =>
        {
            entity.ToTable("native_acs_policy_preparations", table =>
            {
                table.HasCheckConstraint(
                    "ck_native_acs_policy_preparations_scope",
                    "scope_type = 'Platform' AND scope_id = 'default'");
                table.HasCheckConstraint(
                    "ck_native_acs_policy_preparations_hashes",
                    "scope_digest ~ '^[0-9a-f]{64}$' AND request_digest ~ '^[0-9a-f]{64}$' " +
                    "AND proposed_content_sha256 ~ '^[0-9a-f]{64}$' AND " +
                    "(expected_head_content_sha256 IS NULL OR expected_head_content_sha256 ~ '^[0-9a-f]{64}$')");
                table.HasCheckConstraint(
                    "ck_native_acs_policy_preparations_expected_head",
                    "(expected_head_policy_id IS NULL AND expected_head_revision IS NULL AND " +
                    "expected_head_content_sha256 IS NULL) OR " +
                    "(expected_head_policy_id IS NOT NULL AND expected_head_revision > 0 AND " +
                    "expected_head_content_sha256 IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_native_acs_policy_preparations_governance_decision_pair",
                    "(governance_decision_id IS NULL AND governance_decision_hash IS NULL) OR " +
                    "(governance_decision_id IS NOT NULL AND governance_decision_hash IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_native_acs_policy_preparations_governance_decision_hash",
                    "governance_decision_hash IS NULL OR governance_decision_hash ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(preparation => preparation.PreparationId);
            entity.HasIndex(preparation => preparation.ScopeDigest)
                .IsUnique()
                .HasDatabaseName("ux_native_acs_policy_preparations_scope_digest");
            entity.HasIndex(preparation => preparation.GovernanceDecisionId)
                .IsUnique()
                .HasFilter("governance_decision_id IS NOT NULL")
                .HasDatabaseName("ux_native_acs_policy_preparations_governance_decision_id");
            entity.Property(preparation => preparation.PreparationId).HasColumnName("preparation_id");
            entity.Property(preparation => preparation.ScopeDigest).HasColumnName("scope_digest").HasMaxLength(64);
            entity.Property(preparation => preparation.ScopeType)
                .HasColumnName("scope_type").HasConversion<string>().HasMaxLength(16);
            entity.Property(preparation => preparation.ScopeId).HasColumnName("scope_id").HasMaxLength(256);
            entity.Property(preparation => preparation.PolicyId).HasColumnName("policy_id").HasMaxLength(128);
            entity.Property(preparation => preparation.ActorIssuer).HasColumnName("actor_issuer").HasMaxLength(512);
            entity.Property(preparation => preparation.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(preparation => preparation.MembershipId).HasColumnName("membership_id");
            entity.Property(preparation => preparation.MembershipRevision).HasColumnName("membership_revision");
            entity.Property(preparation => preparation.RoleAssignmentId).HasColumnName("role_assignment_id");
            entity.Property(preparation => preparation.RoleAssignmentRevision).HasColumnName("role_assignment_revision");
            entity.Property(preparation => preparation.IdempotencyKey)
                .HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(preparation => preparation.RequestDigest).HasColumnName("request_digest").HasMaxLength(64);
            entity.Property(preparation => preparation.ExpectedHeadPolicyId)
                .HasColumnName("expected_head_policy_id").HasMaxLength(128);
            entity.Property(preparation => preparation.ExpectedHeadRevision).HasColumnName("expected_head_revision");
            entity.Property(preparation => preparation.ExpectedHeadContentSha256)
                .HasColumnName("expected_head_content_sha256").HasMaxLength(64);
            entity.Property(preparation => preparation.ProposedContentSha256)
                .HasColumnName("proposed_content_sha256").HasMaxLength(64);
            entity.Property(preparation => preparation.ArtifactJson).HasColumnName("artifact").HasColumnType("jsonb");
            entity.Property(preparation => preparation.Reason).HasColumnName("reason").HasMaxLength(1024);
            entity.Property(preparation => preparation.CorrelationId)
                .HasColumnName("correlation_id").HasMaxLength(128);
            entity.Property(preparation => preparation.GovernanceDecisionId)
                .HasColumnName("governance_decision_id");
            entity.Property(preparation => preparation.GovernanceDecisionHash)
                .HasColumnName("governance_decision_hash").HasMaxLength(64);
            entity.Property(preparation => preparation.CreatedAt).HasColumnName("created_at");
            entity.HasOne<ProjectGovernanceDecisionRecord>()
                .WithMany()
                .HasForeignKey(preparation => preparation.GovernanceDecisionId)
                .HasPrincipalKey(decision => decision.DecisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NativeAcsPolicyRevisionRecord>(entity =>
        {
            entity.ToTable("native_acs_policy_revisions", table =>
            {
                table.HasCheckConstraint(
                    "ck_native_acs_policy_revisions_scope",
                    "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                table.HasCheckConstraint("ck_native_acs_policy_revisions_revision", "revision > 0");
                table.HasCheckConstraint(
                    "ck_native_acs_policy_revisions_hash",
                    "content_sha256 ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(revision => new
            {
                revision.ScopeType,
                revision.ScopeId,
                revision.PolicyId,
                revision.Revision,
            });
            entity.Property(revision => revision.ScopeType)
                .HasColumnName("scope_type").HasConversion<string>().HasMaxLength(16);
            entity.Property(revision => revision.ScopeId).HasColumnName("scope_id").HasMaxLength(256);
            entity.Property(revision => revision.PolicyId).HasColumnName("policy_id").HasMaxLength(128);
            entity.Property(revision => revision.Revision).HasColumnName("revision");
            entity.Property(revision => revision.ContentSha256).HasColumnName("content_sha256").HasMaxLength(64);
            entity.Property(revision => revision.ManifestBytes).HasColumnName("manifest_bytes").HasColumnType("bytea");
            entity.Property(revision => revision.WriteTransactionId)
                .HasColumnName("write_transaction_id")
                .HasColumnType("text")
                .HasDefaultValueSql("pg_current_xact_id()::text")
                .ValueGeneratedOnAdd();
            entity.Property(revision => revision.CreatedByIssuer).HasColumnName("created_by_issuer").HasMaxLength(512);
            entity.Property(revision => revision.CreatedByActorId).HasColumnName("created_by_actor_id").HasMaxLength(256);
            entity.Property(revision => revision.MembershipId).HasColumnName("membership_id");
            entity.Property(revision => revision.MembershipRevision).HasColumnName("membership_revision");
            entity.Property(revision => revision.RoleAssignmentId).HasColumnName("role_assignment_id");
            entity.Property(revision => revision.RoleAssignmentRevision).HasColumnName("role_assignment_revision");
            entity.Property(revision => revision.Reason).HasColumnName("reason").HasMaxLength(1024);
            entity.Property(revision => revision.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            entity.Property(revision => revision.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<NativeAcsPolicyArtifactFileRecord>(entity =>
        {
            entity.ToTable("native_acs_policy_artifact_files", table =>
                table.HasCheckConstraint(
                    "ck_native_acs_policy_artifact_files_scope",
                    "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')"));
            entity.HasKey(file => new
            {
                file.ScopeType,
                file.ScopeId,
                file.PolicyId,
                file.Revision,
                file.RelativePath,
            });
            entity.Property(file => file.ScopeType).HasColumnName("scope_type").HasConversion<string>().HasMaxLength(16);
            entity.Property(file => file.ScopeId).HasColumnName("scope_id").HasMaxLength(256);
            entity.Property(file => file.PolicyId).HasColumnName("policy_id").HasMaxLength(128);
            entity.Property(file => file.Revision).HasColumnName("revision");
            entity.Property(file => file.RelativePath).HasColumnName("relative_path").HasMaxLength(256);
            entity.Property(file => file.Bytes).HasColumnName("bytes").HasColumnType("bytea");
            entity.Property(file => file.WriteTransactionId)
                .HasColumnName("write_transaction_id")
                .HasColumnType("text")
                .HasDefaultValueSql("pg_current_xact_id()::text")
                .ValueGeneratedOnAdd();
            entity.HasOne<NativeAcsPolicyRevisionRecord>()
                .WithMany()
                .HasForeignKey(file => new { file.ScopeType, file.ScopeId, file.PolicyId, file.Revision })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NativeAcsPolicyIdempotencyRecord>(entity =>
        {
            entity.ToTable("native_acs_policy_idempotency", table =>
                table.HasCheckConstraint(
                    "ck_native_acs_policy_idempotency_scope",
                    "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')"));
            entity.HasKey(item => item.ScopeDigest);
            entity.Property(item => item.ScopeDigest).HasColumnName("scope_digest").HasMaxLength(64);
            entity.Property(item => item.ScopeType).HasColumnName("scope_type").HasConversion<string>().HasMaxLength(16);
            entity.Property(item => item.ScopeId).HasColumnName("scope_id").HasMaxLength(256);
            entity.Property(item => item.PolicyId).HasColumnName("policy_id").HasMaxLength(128);
            entity.Property(item => item.ActorIssuer).HasColumnName("actor_issuer").HasMaxLength(512);
            entity.Property(item => item.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(item => item.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(item => item.RequestDigest).HasColumnName("request_digest").HasMaxLength(64);
            entity.Property(item => item.ReceiptJson).HasColumnName("receipt").HasColumnType("jsonb");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<NativeAcsPolicyChangeAuditRecord>(entity =>
        {
            entity.ToTable("native_acs_policy_change_audit", table =>
            {
                table.HasCheckConstraint(
                    "ck_native_acs_policy_change_audit_scope",
                    "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                table.HasCheckConstraint(
                    "ck_native_acs_policy_change_audit_type",
                    "change_type IN ('created', 'updated')");
                table.HasCheckConstraint(
                    "ck_native_acs_policy_change_audit_hash",
                    "content_sha256 ~ '^[0-9a-f]{64}$' AND (previous_content_sha256 IS NULL OR previous_content_sha256 ~ '^[0-9a-f]{64}$')");
            });
            entity.HasKey(audit => audit.AuditId);
            entity.Property(audit => audit.AuditId).HasColumnName("audit_id");
            entity.Property(audit => audit.ScopeType).HasColumnName("scope_type").HasConversion<string>().HasMaxLength(16);
            entity.Property(audit => audit.ScopeId).HasColumnName("scope_id").HasMaxLength(256);
            entity.Property(audit => audit.PolicyId).HasColumnName("policy_id").HasMaxLength(128);
            entity.Property(audit => audit.Revision).HasColumnName("revision");
            entity.Property(audit => audit.PreviousRevision).HasColumnName("previous_revision");
            entity.Property(audit => audit.ChangeType).HasColumnName("change_type").HasMaxLength(16);
            entity.Property(audit => audit.ActorIssuer).HasColumnName("actor_issuer").HasMaxLength(512);
            entity.Property(audit => audit.ActorId).HasColumnName("actor_id").HasMaxLength(256);
            entity.Property(audit => audit.MembershipId).HasColumnName("membership_id");
            entity.Property(audit => audit.MembershipRevision).HasColumnName("membership_revision");
            entity.Property(audit => audit.RoleAssignmentId).HasColumnName("role_assignment_id");
            entity.Property(audit => audit.RoleAssignmentRevision).HasColumnName("role_assignment_revision");
            entity.Property(audit => audit.PreviousContentSha256).HasColumnName("previous_content_sha256").HasMaxLength(64);
            entity.Property(audit => audit.ContentSha256).HasColumnName("content_sha256").HasMaxLength(64);
            entity.Property(audit => audit.Reason).HasColumnName("reason").HasMaxLength(1024);
            entity.Property(audit => audit.CorrelationId).HasColumnName("correlation_id").HasMaxLength(128);
            entity.Property(audit => audit.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(audit => new { audit.ScopeType, audit.ScopeId, audit.PolicyId, audit.CreatedAt });
        });

        modelBuilder.Entity<NativeAcsPolicyOutboxRecord>(entity =>
        {
            entity.ToTable("native_acs_policy_outbox");
            entity.HasKey(item => item.OutboxId);
            entity.Property(item => item.OutboxId).HasColumnName("outbox_id");
            entity.Property(item => item.EventType).HasColumnName("event_type").HasMaxLength(96);
            entity.Property(item => item.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(item => item.CreatedAt);
        });

        modelBuilder.Entity<ProjectGovernanceDecisionStreamHeadRecord>(entity =>
        {
            entity.ToTable("project_governance_decision_stream_heads", table =>
            {
                table.HasCheckConstraint(
                    "ck_project_governance_decision_stream_heads_scope",
                    "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                table.HasCheckConstraint(
                    "ck_project_governance_decision_stream_heads_position",
                    "sequence >= 0 AND fence_generation = sequence");
                table.HasCheckConstraint(
                    "ck_project_governance_decision_stream_heads_hash",
                    "head_hash ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(head => new
            {
                head.ScopeType,
                head.ScopeId,
                head.ResourceType,
                head.ResourceId,
            });
            entity.Property(head => head.ScopeType).HasColumnName("scope_type").HasConversion<string>().HasMaxLength(16);
            entity.Property(head => head.ScopeId).HasColumnName("scope_id").HasMaxLength(256);
            entity.Property(head => head.ResourceType).HasColumnName("resource_type").HasMaxLength(64);
            entity.Property(head => head.ResourceId).HasColumnName("resource_id").HasMaxLength(256);
            entity.Property(head => head.Sequence).HasColumnName("sequence");
            entity.Property(head => head.FenceGeneration).HasColumnName("fence_generation");
            entity.Property(head => head.HeadHash).HasColumnName("head_hash").HasMaxLength(64);
            entity.Property(head => head.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<ProjectGovernanceDecisionRecord>(entity =>
        {
            entity.ToTable("project_governance_decisions", table =>
            {
                table.HasCheckConstraint(
                    "ck_project_governance_decisions_scope",
                    "scope_type IN ('Platform', 'Project') AND (scope_type <> 'Platform' OR scope_id = 'default')");
                table.HasCheckConstraint("ck_project_governance_decisions_sequence", "sequence > 0");
                table.HasCheckConstraint(
                    "ck_project_governance_decisions_hash",
                    "request_digest ~ '^[0-9a-f]{64}$' AND previous_hash ~ '^[0-9a-f]{64}$' AND record_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_project_governance_decisions_size",
                    "octet_length(canonical_record) BETWEEN 1 AND 65536");
            });
            entity.HasKey(record => new
            {
                record.ScopeType,
                record.ScopeId,
                record.ResourceType,
                record.ResourceId,
                record.Sequence,
            });
            entity.HasIndex(record => new
            {
                record.ScopeType,
                record.ScopeId,
                record.ResourceType,
                record.ResourceId,
                record.IdempotencyKey,
            }).IsUnique();
            entity.Property(record => record.ScopeType).HasColumnName("scope_type").HasConversion<string>().HasMaxLength(16);
            entity.Property(record => record.ScopeId).HasColumnName("scope_id").HasMaxLength(256);
            entity.Property(record => record.ResourceType).HasColumnName("resource_type").HasMaxLength(64);
            entity.Property(record => record.ResourceId).HasColumnName("resource_id").HasMaxLength(256);
            entity.Property(record => record.Sequence).HasColumnName("sequence");
            entity.Property(record => record.DecisionId).HasColumnName("decision_id");
            entity.Property(record => record.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(128);
            entity.Property(record => record.RequestDigest).HasColumnName("request_digest").HasMaxLength(64);
            entity.Property(record => record.PreviousHash).HasColumnName("previous_hash").HasMaxLength(64);
            entity.Property(record => record.RecordHash).HasColumnName("record_hash").HasMaxLength(64);
            entity.Property(record => record.CanonicalRecord).HasColumnName("canonical_record").HasColumnType("bytea");
            entity.Property(record => record.WriteTransactionId)
                .HasColumnName("write_transaction_id")
                .HasColumnType("text")
                .HasDefaultValueSql("pg_current_xact_id()::text")
                .ValueGeneratedOnAdd();
            entity.Property(record => record.RecordedAt).HasColumnName("recorded_at");
            entity.HasOne<ProjectGovernanceDecisionStreamHeadRecord>()
                .WithMany()
                .HasForeignKey(record => new
                {
                    record.ScopeType,
                    record.ScopeId,
                    record.ResourceType,
                    record.ResourceId,
                })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProjectGovernanceDecisionOutboxRecord>(entity =>
        {
            entity.ToTable("project_governance_decision_outbox", table =>
            {
                table.HasCheckConstraint(
                    "ck_project_governance_decision_outbox_event",
                    "event_type = 'project.governance_decision_appended.v1'");
                table.HasCheckConstraint(
                    "ck_project_governance_decision_outbox_hash",
                    "record_hash ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(item => item.OutboxId);
            entity.HasIndex(item => item.DecisionId).IsUnique();
            entity.Property(item => item.OutboxId).HasColumnName("outbox_id");
            entity.Property(item => item.DecisionId).HasColumnName("decision_id");
            entity.Property(item => item.EventType).HasColumnName("event_type").HasMaxLength(96);
            entity.Property(item => item.Sequence).HasColumnName("sequence");
            entity.Property(item => item.RecordHash).HasColumnName("record_hash").HasMaxLength(64);
            entity.Property(item => item.WriteTransactionId)
                .HasColumnName("write_transaction_id")
                .HasColumnType("text")
                .HasDefaultValueSql("pg_current_xact_id()::text")
                .ValueGeneratedOnAdd();
            entity.Property(item => item.CreatedAt).HasColumnName("created_at");
            entity.Property(item => item.PayloadJson).HasColumnName("payload").HasColumnType("jsonb");
            entity.HasOne<ProjectGovernanceDecisionRecord>()
                .WithOne()
                .HasForeignKey<ProjectGovernanceDecisionOutboxRecord>(item => item.DecisionId)
                .HasPrincipalKey<ProjectGovernanceDecisionRecord>(record => record.DecisionId)
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
