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
    public DbSet<CopilotConnectionRecord> CopilotConnections => Set<CopilotConnectionRecord>();
    public DbSet<CopilotConnectionRevision> CopilotConnectionRevisions => Set<CopilotConnectionRevision>();
    public DbSet<RepoAppAuthorizationTransaction> RepoAppAuthorizationTransactions =>
        Set<RepoAppAuthorizationTransaction>();
    public DbSet<RepoAppConnectionRecord> RepoAppConnections => Set<RepoAppConnectionRecord>();
    public DbSet<RepoAppInstallationRecord> RepoAppInstallations => Set<RepoAppInstallationRecord>();
    public DbSet<RepoAppRepositorySelectionRecord> RepoAppRepositorySelections =>
        Set<RepoAppRepositorySelectionRecord>();
    public DbSet<RemoteMcpOAuthConnectionRecord> RemoteMcpOAuthConnections =>
        Set<RemoteMcpOAuthConnectionRecord>();
    public DbSet<RemoteMcpOAuthConsentRecord> RemoteMcpOAuthConsents =>
        Set<RemoteMcpOAuthConsentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        RuntimeGrantModel.Configure(modelBuilder);
        CopilotConnectionModel.Configure(modelBuilder);
        RemoteMcpOAuthModel.Configure(modelBuilder);

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

        modelBuilder.Entity<RepoAppAuthorizationTransaction>(entity =>
        {
            entity.ToTable("repo_app_authorization_transactions", table =>
                table.HasCheckConstraint("ck_repo_app_authorization_state", "state IN (0, 1, 2, 3)"));
            entity.HasKey(transaction => transaction.StateHash);
            entity.Property(transaction => transaction.StateHash)
                .HasColumnName("state_hash").HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(transaction => transaction.TransactionId)
                .HasColumnName("transaction_id").HasMaxLength(128).IsRequired();
            entity.Property(transaction => transaction.OwnerId).HasColumnName("owner_id");
            entity.Property(transaction => transaction.Purpose).HasColumnName("purpose").HasConversion<int>();
            entity.Property(transaction => transaction.CallbackCookieHash)
                .HasColumnName("callback_cookie_hash").HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(transaction => transaction.ProtectedCodeVerifier)
                .HasColumnName("protected_code_verifier").HasMaxLength(2048).IsRequired();
            entity.Property(transaction => transaction.ReturnRouteKey)
                .HasColumnName("return_route_key").HasMaxLength(32).IsRequired();
            entity.Property(transaction => transaction.CreatedAt).HasColumnName("created_at");
            entity.Property(transaction => transaction.ExpiresAt).HasColumnName("expires_at");
            entity.Property(transaction => transaction.State).HasColumnName("state").HasConversion<int>();
            entity.Property(transaction => transaction.CompletedAt).HasColumnName("completed_at");
            entity.Property(transaction => transaction.InstallationId).HasColumnName("installation_id");
            entity.HasIndex(transaction => transaction.TransactionId).IsUnique();
            entity.HasIndex(transaction => transaction.ExpiresAt);
            entity.HasOne<BrokerUser>()
                .WithMany()
                .HasForeignKey(transaction => transaction.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RepoAppConnectionRecord>(entity =>
        {
            entity.ToTable("repo_app_connections", table =>
            {
                table.HasCheckConstraint(
                    "ck_repo_app_connection_revisions",
                    "connection_revision > 0 AND credential_revision > 0");
                table.HasCheckConstraint(
                    "ck_repo_app_connection_state",
                    "state IN (0, 1, 2)");
                table.HasCheckConstraint(
                    "ck_repo_app_connection_refresh_lease",
                    "(refresh_lease_id IS NULL) = (refresh_lease_expires_at IS NULL)");
            });
            entity.HasKey(connection => connection.ConnectionId);
            entity.Property(connection => connection.ConnectionId)
                .HasColumnName("connection_id").HasMaxLength(128);
            entity.Property(connection => connection.OwnerId).HasColumnName("owner_id");
            entity.Property(connection => connection.GitHubLogin)
                .HasColumnName("github_login").HasMaxLength(256).IsRequired();
            entity.Property(connection => connection.AccessTokenSecretId)
                .HasColumnName("access_token_secret_id").HasMaxLength(256).IsRequired();
            entity.Property(connection => connection.AccessTokenSecretVersion)
                .HasColumnName("access_token_secret_version").HasMaxLength(256).IsRequired();
            entity.Property(connection => connection.AccessTokenExpiresAt)
                .HasColumnName("access_token_expires_at");
            entity.Property(connection => connection.RefreshTokenSecretId)
                .HasColumnName("refresh_token_secret_id").HasMaxLength(256).IsRequired();
            entity.Property(connection => connection.RefreshTokenSecretVersion)
                .HasColumnName("refresh_token_secret_version").HasMaxLength(256).IsRequired();
            entity.Property(connection => connection.RefreshTokenExpiresAt)
                .HasColumnName("refresh_token_expires_at");
            entity.Property(connection => connection.ConnectionRevision).HasColumnName("connection_revision");
            entity.Property(connection => connection.CredentialRevision).HasColumnName("credential_revision");
            entity.Property(connection => connection.State).HasColumnName("state").HasConversion<int>();
            entity.Property(connection => connection.RefreshLeaseId).HasColumnName("refresh_lease_id");
            entity.Property(connection => connection.RefreshLeaseExpiresAt).HasColumnName("refresh_lease_expires_at");
            entity.Property(connection => connection.CreatedAt).HasColumnName("created_at");
            entity.Property(connection => connection.UpdatedAt).HasColumnName("updated_at");
            entity.Property(connection => connection.ConnectionRevision).IsConcurrencyToken();
            entity.HasIndex(connection => connection.OwnerId).IsUnique();
            entity.HasOne<BrokerUser>()
                .WithMany()
                .HasForeignKey(connection => connection.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RepoAppInstallationRecord>(entity =>
        {
            entity.ToTable("repo_app_installations", table =>
                table.HasCheckConstraint(
                    "ck_repo_app_installation_identity",
                    "installation_id > 0 AND connection_revision > 0"));
            entity.HasKey(installation => new { installation.ConnectionId, installation.InstallationId });
            entity.Property(installation => installation.ConnectionId)
                .HasColumnName("connection_id").HasMaxLength(128);
            entity.Property(installation => installation.InstallationId).HasColumnName("installation_id");
            entity.Property(installation => installation.AccountLogin)
                .HasColumnName("account_login").HasMaxLength(256).IsRequired();
            entity.Property(installation => installation.AccountType)
                .HasColumnName("account_type").HasMaxLength(128).IsRequired();
            entity.Property(installation => installation.RepositorySelection)
                .HasColumnName("repository_selection").HasMaxLength(32).IsRequired();
            entity.Property(installation => installation.ConnectionRevision)
                .HasColumnName("connection_revision");
            entity.Property(installation => installation.AddedAt).HasColumnName("added_at");
            entity.Property(installation => installation.RevokedAt).HasColumnName("revoked_at");
            entity.HasOne<RepoAppConnectionRecord>()
                .WithMany()
                .HasForeignKey(installation => installation.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RepoAppRepositorySelectionRecord>(entity =>
        {
            entity.ToTable("repo_app_repository_selections", table =>
            {
                table.HasCheckConstraint(
                    "ck_repo_app_selection_binding",
                    "connection_revision > 0 AND installation_id > 0 AND repository_id > 0 AND expires_at > created_at");
                table.HasCheckConstraint(
                    "ck_repo_app_selection_permission_digest",
                    "permission_digest IS NULL OR permission_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(selection => selection.CodeHash);
            entity.Property(selection => selection.CodeHash)
                .HasColumnName("code_hash").HasMaxLength(64).IsFixedLength();
            entity.Property(selection => selection.OwnerId).HasColumnName("owner_id");
            entity.Property(selection => selection.ConnectionId).HasColumnName("connection_id").HasMaxLength(128);
            entity.Property(selection => selection.ConnectionRevision).HasColumnName("connection_revision");
            entity.Property(selection => selection.InstallationId).HasColumnName("installation_id");
            entity.Property(selection => selection.RepositoryId).HasColumnName("repository_id");
            entity.Property(selection => selection.RepositoryFullName)
                .HasColumnName("repository_full_name").HasMaxLength(201).IsRequired();
            entity.Property(selection => selection.CreatedAt).HasColumnName("created_at");
            entity.Property(selection => selection.ExpiresAt).HasColumnName("expires_at");
            entity.Property(selection => selection.ProjectId)
                .HasColumnName("project_id").HasMaxLength(256);
            entity.Property(selection => selection.PermissionDigest)
                .HasColumnName("permission_digest").HasMaxLength(64).IsFixedLength();
            entity.Property(selection => selection.IssueWriteRequested)
                .HasColumnName("issue_write_requested").HasDefaultValue(false);
            entity.Property(selection => selection.ConsumedAt).HasColumnName("consumed_at");
            entity.HasIndex(selection => selection.ExpiresAt);
            entity.HasOne<BrokerUser>()
                .WithMany()
                .HasForeignKey(selection => selection.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<RepoAppConnectionRecord>()
                .WithMany()
                .HasForeignKey(selection => selection.ConnectionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Registers OpenIddict's application/authorization/scope/token entity types;
        // they share this context's default schema set above.
        modelBuilder.UseOpenIddict();
    }
}
