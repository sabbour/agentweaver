using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentweaver.Projects.Config;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
    }
}

public sealed class ProjectsConfigDbContextFactory : IDesignTimeDbContextFactory<ProjectsConfigDbContext>
{
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
