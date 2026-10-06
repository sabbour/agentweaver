using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Agentweaver.Projects.Config;

public sealed class ProjectsConfigDbContext(DbContextOptions<ProjectsConfigDbContext> options) : DbContext(options)
{
    public const string Schema = "projects_config";

    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
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
            entity.Property(project => project.OwnerActorId).HasColumnName("owner_actor_id").HasMaxLength(256).IsRequired();
            entity.Property(project => project.Name).HasColumnName("name").HasMaxLength(160).IsRequired();
            entity.Property(project => project.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            entity.Property(project => project.Revision).HasColumnName("revision").IsConcurrencyToken();
            entity.Property(project => project.ConfigurationRevision)
                .HasColumnName("configuration_revision").IsConcurrencyToken();
            entity.Property(project => project.CreatedAt).HasColumnName("created_at");
            entity.Property(project => project.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(project => new { project.TenantId, project.OwnerActorId, project.State });
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
