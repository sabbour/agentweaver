using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Owns the broker's Postgres schema: OpenIddict's application/authorization/scope/token
/// stores plus the broker's own <see cref="BrokerUser"/> table. No other service reads or
/// writes this schema directly.
/// </summary>
public sealed class IdentityBrokerDbContext : DbContext
{
    public const string Schema = "identity_broker";

    public IdentityBrokerDbContext(DbContextOptions<IdentityBrokerDbContext> options) : base(options)
    {
    }

    public DbSet<BrokerUser> Users => Set<BrokerUser>();

    public DbSet<PendingAuthorization> PendingAuthorizations => Set<PendingAuthorization>();

    public DbSet<SecretGrantHead> SecretGrantHeads => Set<SecretGrantHead>();

    public DbSet<SecretGrantRevision> SecretGrantRevisions => Set<SecretGrantRevision>();

    public DbSet<SecretGrantOperation> SecretGrantOperations => Set<SecretGrantOperation>();

    public DbSet<RuntimeGrantHead> RuntimeGrantHeads => Set<RuntimeGrantHead>();
    public DbSet<RuntimeGrantRevision> RuntimeGrantRevisions => Set<RuntimeGrantRevision>();
    public DbSet<RuntimeGrantOperation> RuntimeGrantOperations => Set<RuntimeGrantOperation>();
    public DbSet<RuntimeGrantOperationReceipt> RuntimeGrantOperationReceipts => Set<RuntimeGrantOperationReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        RuntimeGrantModel.Configure(modelBuilder);

        modelBuilder.Entity<BrokerUser>(entity =>
        {
            entity.ToTable("broker_users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Issuer).HasMaxLength(2048).IsRequired();
            entity.Property(user => user.Subject).HasMaxLength(512).IsRequired();
            entity.Property(user => user.DisplayName).HasMaxLength(256);
            entity.Property(user => user.Email).HasMaxLength(320);
            entity.HasIndex(user => new { user.Issuer, user.Subject }).IsUnique();
        });

        modelBuilder.Entity<PendingAuthorization>(entity =>
        {
            entity.ToTable("pending_authorizations");
            entity.HasKey(pending => pending.Id);
            entity.Property(pending => pending.HandleHash).HasMaxLength(64).IsRequired();
            entity.Property(pending => pending.ClientId).HasMaxLength(256).IsRequired();
            entity.Property(pending => pending.RedirectUri).HasMaxLength(2048).IsRequired();
            entity.Property(pending => pending.Scope).HasMaxLength(2048).IsRequired();
            entity.Property(pending => pending.State).HasMaxLength(2048);
            entity.Property(pending => pending.CodeChallenge).HasMaxLength(256);
            entity.Property(pending => pending.CodeChallengeMethod).HasMaxLength(32);
            entity.Property(pending => pending.Nonce).HasMaxLength(256);
            entity.Property(pending => pending.ProjectId).HasMaxLength(256);
            entity.Property(pending => pending.RunId).HasMaxLength(256);
            entity.HasIndex(pending => pending.HandleHash).IsUnique();
            entity.HasIndex(pending => pending.ExpiresAt);
        });

        modelBuilder.Entity<SecretGrantHead>(entity =>
        {
            entity.ToTable("secret_grant_heads", table =>
                table.HasCheckConstraint("ck_secret_grant_heads_revision", "current_revision >= 0"));
            entity.HasKey(head => head.GrantId);
            entity.Property(head => head.GrantId).HasColumnName("grant_id").HasMaxLength(256);
            entity.Property(head => head.CurrentRevision).HasColumnName("current_revision");
        });

        modelBuilder.Entity<SecretGrantRevision>(entity =>
        {
            entity.ToTable("secret_grant_revisions");
            entity.HasKey(snapshot => new { snapshot.GrantId, snapshot.Revision });
            entity.Property(snapshot => snapshot.GrantId).HasColumnName("grant_id").HasMaxLength(256);
            entity.Property(snapshot => snapshot.Revision).HasColumnName("revision");
            entity.Property(snapshot => snapshot.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.Property(snapshot => snapshot.ProjectId).HasColumnName("project_id").HasMaxLength(256).IsRequired();
            entity.Property(snapshot => snapshot.RunId).HasColumnName("run_id").HasMaxLength(256).IsRequired();
            entity.Property(snapshot => snapshot.Purpose).HasColumnName("purpose").HasMaxLength(256).IsRequired();
            entity.Property(snapshot => snapshot.SecretId).HasColumnName("secret_id").HasMaxLength(256).IsRequired();
            entity.Property(snapshot => snapshot.SecretVersion).HasColumnName("secret_version").HasMaxLength(256).IsRequired();
            entity.Property(snapshot => snapshot.State).HasColumnName("state").HasConversion<int>();
            entity.Property(snapshot => snapshot.ExpiresAt).HasColumnName("expires_at");
            entity.HasIndex(snapshot => new { snapshot.ActorId, snapshot.ProjectId, snapshot.RunId });
            entity.HasOne<SecretGrantHead>()
                .WithMany()
                .HasForeignKey(snapshot => snapshot.GrantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SecretGrantOperation>(entity =>
        {
            entity.ToTable("secret_grant_operations");
            entity.HasKey(operation => operation.IdempotencyKey);
            entity.Property(operation => operation.IdempotencyKey)
                .HasColumnName("idempotency_key").HasMaxLength(256);
            entity.Property(operation => operation.RequestHash)
                .HasColumnName("request_hash").HasMaxLength(64).IsRequired();
            entity.Property(operation => operation.GrantId)
                .HasColumnName("grant_id").HasMaxLength(256).IsRequired();
            entity.Property(operation => operation.Revision).HasColumnName("revision");
        });

        // Registers OpenIddict's application/authorization/scope/token entity types;
        // they share this context's default schema set above.
        modelBuilder.UseOpenIddict();
    }
}
