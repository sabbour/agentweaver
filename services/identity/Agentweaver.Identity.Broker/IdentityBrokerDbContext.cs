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
    public DbSet<IssueSyncGrant> IssueSyncGrants => Set<IssueSyncGrant>();
    public DbSet<IssueSyncMintAudit> IssueSyncMintAudit => Set<IssueSyncMintAudit>();

    public DbSet<PendingAuthorization> PendingAuthorizations => Set<PendingAuthorization>();

    public DbSet<SecretGrantHead> SecretGrantHeads => Set<SecretGrantHead>();

    public DbSet<SecretGrantRevision> SecretGrantRevisions => Set<SecretGrantRevision>();

    public DbSet<SecretGrantOperation> SecretGrantOperations => Set<SecretGrantOperation>();

    public DbSet<ModelProviderCredentialHead> ModelProviderCredentialHeads => Set<ModelProviderCredentialHead>();
    public DbSet<ModelProviderCredentialRevision> ModelProviderCredentialRevisions =>
        Set<ModelProviderCredentialRevision>();
    public DbSet<ModelProviderCredentialWriteClaim> ModelProviderCredentialWriteClaims =>
        Set<ModelProviderCredentialWriteClaim>();

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

    /// <summary>Registers broker-owned entities and the OpenIddict stores under the isolated broker schema.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfiguration(new IssueSyncGrantModel());
        modelBuilder.ApplyConfiguration(new IssueSyncMintAuditModel());
        RuntimeGrantModel.Configure(modelBuilder);
        ConfigureModelProviderCredentials(modelBuilder);
        ConfigureModelProviderCredentialWriteClaims(modelBuilder);
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
            entity.Property(pending => pending.PromptNone).HasDefaultValue(false);
            entity.Property(pending => pending.ProjectId).HasMaxLength(256);
            entity.Property(pending => pending.RunId).HasMaxLength(256);
            entity.Property(pending => pending.WorkspaceSelectionRequest).HasMaxLength(8192);
            entity.Property(pending => pending.ProtectedWorkspaceSelectionPayload).HasMaxLength(16384);
            entity.Property(pending => pending.NativeRepoAppSelectionReview).HasDefaultValue(false);
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

    private static void ConfigureModelProviderCredentialWriteClaims(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelProviderCredentialWriteClaim>(entity =>
        {
            entity.ToTable("model_provider_credential_write_claims", table =>
            {
                table.HasCheckConstraint(
                    "ck_model_provider_credential_write_claims_scope_project",
                    "(scope = 'Platform' AND project_id IS NULL) OR " +
                    "(scope = 'Project' AND project_id IS NOT NULL AND length(project_id) > 0)");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_write_claims_vault_source",
                    "vault_source IN ('Existing', 'Byok')");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_write_claims_state",
                    "state IN ('Claimed', 'Quarantined', 'Completed', 'PublicationPending', 'Published', 'Cancelled', 'DisablePending', 'Disabled')");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_write_claims_revision",
                    "expected_revision >= 0");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_write_claims_secret_name",
                    "secret_name ~ '^[A-Za-z0-9-]{1,127}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_write_claims_fingerprint",
                    "request_fingerprint ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_write_claims_byok_name",
                    "vault_source <> 'Byok' OR secret_name = 'byok-' || lower(scope) || '-' || provider_id");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_write_claims_publication_identity",
                    "(expected_provider_revision IS NULL AND purpose IS NULL AND issuer IS NULL AND tenant IS NULL) OR " +
                    "(expected_provider_revision IS NOT NULL AND expected_provider_revision > 0 AND expected_provider_revision < 9223372036854775807 AND " +
                    "purpose IS NOT NULL AND purpose = 'credential-publication' AND issuer IS NOT NULL AND octet_length(issuer) = length(issuer) AND issuer COLLATE \"C\" ~ '^[!-~]+$' AND " +
                    "tenant IS NOT NULL AND octet_length(tenant) = length(tenant) AND tenant COLLATE \"C\" ~ '^[A-Za-z0-9._:-]+$') OR " +
                    "((expected_provider_revision IS NULL OR (expected_provider_revision > 0 AND expected_provider_revision < 9223372036854775807)) AND " +
                    "purpose IS NOT NULL AND purpose = 'credential-disable' AND issuer IS NOT NULL AND octet_length(issuer) = length(issuer) AND issuer COLLATE \"C\" ~ '^[!-~]+$' AND " +
                    "(tenant IS NULL OR (octet_length(tenant) = length(tenant) AND tenant COLLATE \"C\" ~ '^[A-Za-z0-9._:-]+$')))");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_write_claims_outcome",
                    "((state = 'Claimed' AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NULL AND completed_revision IS NULL AND completed_at IS NULL) OR " +
                    "(state = 'Quarantined' AND quarantined_at IS NOT NULL AND cancelled_at IS NULL AND pending_secret_version IS NULL AND completed_revision IS NULL AND completed_at IS NULL) OR " +
                    "(state = 'Cancelled' AND cancelled_at IS NOT NULL) OR " +
                    "(state = 'PublicationPending' AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND completed_revision IS NULL AND completed_at IS NULL AND expected_provider_revision IS NOT NULL) OR " +
                    "(state IN ('Completed', 'Published') AND quarantined_at IS NULL AND cancelled_at IS NULL AND completed_revision IS NOT NULL AND completed_at IS NOT NULL AND " +
                    "CASE WHEN expected_revision < 9223372036854775807 THEN completed_revision = expected_revision + 1 ELSE FALSE END AND " +
                    "((state = 'Completed' AND pending_secret_version IS NULL AND expected_provider_revision IS NULL) OR (state = 'Published' AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND expected_provider_revision IS NOT NULL))) OR " +
                    "(state = 'DisablePending' AND purpose = 'credential-disable' AND tenant IS NULL AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND completed_revision IS NULL AND completed_at IS NULL) OR " +
                    "(state = 'Disabled' AND purpose = 'credential-disable' AND tenant IS NOT NULL AND quarantined_at IS NULL AND cancelled_at IS NULL AND pending_secret_version IS NOT NULL AND pending_secret_version ~ '^[0-9A-Fa-f]{32}$' AND completed_revision IS NOT NULL AND completed_at IS NOT NULL AND " +
                    "CASE WHEN expected_revision < 9223372036854775807 THEN completed_revision = expected_revision + 1 ELSE FALSE END)) AND " +
                    "((state NOT IN ('Published', 'Disabled') AND published_provider_revision IS NULL AND published_configuration_hash IS NULL AND published_at IS NULL) OR " +
                    "(state = 'Published' AND published_provider_revision IS NOT NULL AND published_configuration_hash IS NOT NULL AND published_at IS NOT NULL AND " +
                    "CASE WHEN expected_provider_revision IS NOT NULL AND expected_provider_revision < 9223372036854775807 THEN published_provider_revision = expected_provider_revision + 1 ELSE FALSE END AND " +
                    "published_configuration_hash COLLATE \"C\" ~ '^[0-9A-Fa-f]{64}$') OR " +
                    "(state = 'Disabled' AND published_provider_revision IS NOT NULL AND published_configuration_hash IS NOT NULL AND published_at IS NOT NULL AND " +
                    "(expected_provider_revision IS NULL OR (expected_provider_revision < 9223372036854775807 AND published_provider_revision = expected_provider_revision + 1)) AND " +
                    "published_configuration_hash COLLATE \"C\" ~ '^[0-9A-Fa-f]{64}$'))");
            });
            entity.HasKey(claim => claim.OperationId);
            entity.Property(claim => claim.OperationId).HasColumnName("operation_id");
            entity.Property(claim => claim.CredentialSlotId).HasColumnName("credential_slot_id");
            entity.Property(claim => claim.Scope).HasColumnName("scope").HasConversion<string>().HasMaxLength(16);
            entity.Property(claim => claim.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(claim => claim.ProviderId).HasColumnName("provider_id").HasMaxLength(128).IsRequired();
            entity.Property(claim => claim.VaultSource).HasColumnName("vault_source")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(claim => claim.VaultUri).HasColumnName("vault_uri").HasMaxLength(256).IsRequired();
            entity.Property(claim => claim.SecretName).HasColumnName("secret_name").HasMaxLength(127).IsRequired();
            entity.Property(claim => claim.ExpectedRevision).HasColumnName("expected_revision");
            entity.Property(claim => claim.RequestFingerprint).HasColumnName("request_fingerprint")
                .HasMaxLength(64).IsRequired();
            entity.Property(claim => claim.ActorId).HasColumnName("actor_id").HasMaxLength(256).IsRequired();
            entity.Property(claim => claim.State).HasColumnName("state")
                .HasConversion<string>().HasMaxLength(18);
            entity.Property(claim => claim.ClaimedAt).HasColumnName("claimed_at");
            entity.Property(claim => claim.DispatchedAt).HasColumnName("dispatched_at");
            entity.Property(claim => claim.QuarantinedAt).HasColumnName("quarantined_at");
            entity.Property(claim => claim.RecoveredAt).HasColumnName("recovered_at");
            entity.Property(claim => claim.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(claim => claim.PendingSecretVersion).HasColumnName("pending_secret_version").HasMaxLength(32);
            entity.Property(claim => claim.CompletedRevision).HasColumnName("completed_revision");
            entity.Property(claim => claim.CompletedAt).HasColumnName("completed_at");
            entity.Property(claim => claim.ExpectedProviderRevision).HasColumnName("expected_provider_revision");
            entity.Property(claim => claim.Purpose).HasColumnName("purpose").HasMaxLength(32);
            entity.Property(claim => claim.Issuer).HasColumnName("issuer").HasMaxLength(2048);
            entity.Property(claim => claim.Tenant).HasColumnName("tenant").HasMaxLength(256);
            entity.Property(claim => claim.PublishedProviderRevision).HasColumnName("published_provider_revision");
            entity.Property(claim => claim.PublishedConfigurationHash).HasColumnName("published_configuration_hash").HasMaxLength(64);
            entity.Property(claim => claim.PublishedProviderState).HasColumnName("published_provider_state").HasMaxLength(16);
            entity.Property(claim => claim.PublishedAt).HasColumnName("published_at");
            entity.HasIndex(claim => claim.CredentialSlotId).IsUnique()
                .HasFilter("state IN ('Claimed', 'Quarantined', 'PublicationPending', 'DisablePending')");
            entity.HasIndex(claim => claim.ProviderId).IsUnique()
                .HasFilter("state IN ('Claimed', 'Quarantined', 'PublicationPending', 'DisablePending')");
            entity.HasOne<ModelProviderCredentialRevision>()
                .WithMany()
                .HasForeignKey(claim => new { claim.CredentialSlotId, claim.CompletedRevision })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureModelProviderCredentials(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelProviderCredentialHead>(entity =>
        {
            entity.ToTable("model_provider_credentials", table =>
            {
                table.HasCheckConstraint(
                    "ck_model_provider_credentials_scope_project",
                    "(scope = 'Platform' AND project_id IS NULL) OR " +
                    "(scope = 'Project' AND project_id IS NOT NULL AND length(project_id) > 0)");
                table.HasCheckConstraint("ck_model_provider_credentials_state", "state IN ('Enabled', 'Disabled')");
                table.HasCheckConstraint("ck_model_provider_credentials_revision", "revision > 0");
                table.HasCheckConstraint(
                    "ck_model_provider_credentials_secret_name",
                    "secret_name ~ '^[A-Za-z0-9-]{1,127}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_credentials_secret_version",
                    "current_version ~ '^[0-9A-Fa-f]{32}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_credentials_byok_name",
                    "vault_source <> 'Byok' OR secret_name = 'byok-' || lower(scope) || '-' || provider_id");
                table.HasCheckConstraint(
                    "ck_model_provider_credentials_vault_source",
                    "vault_source IN ('Existing', 'Byok')");
            });
            entity.HasKey(head => head.CredentialSlotId);
            entity.Property(head => head.CredentialSlotId).HasColumnName("credential_slot_id");
            entity.Property(head => head.Scope).HasColumnName("scope").HasConversion<string>().HasMaxLength(16);
            entity.Property(head => head.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(head => head.ProviderId).HasColumnName("provider_id").HasMaxLength(128).IsRequired();
            entity.Property(head => head.VaultSource).HasColumnName("vault_source").HasConversion<string>()
                .HasMaxLength(16);
            entity.Property(head => head.VaultUri).HasColumnName("vault_uri").HasMaxLength(256).IsRequired();
            entity.Property(head => head.SecretName).HasColumnName("secret_name").HasMaxLength(127).IsRequired();
            entity.Property(head => head.CurrentVersion).HasColumnName("current_version")
                .HasMaxLength(32).IsRequired();
            entity.Property(head => head.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
            entity.Property(head => head.Revision).HasColumnName("revision");
            entity.Property(head => head.SetBy).HasColumnName("set_by").HasMaxLength(256).IsRequired();
            entity.Property(head => head.SetAt).HasColumnName("set_at");
            entity.HasIndex(head => head.ProviderId).IsUnique();
        });

        modelBuilder.Entity<ModelProviderCredentialRevision>(entity =>
        {
            entity.ToTable("model_provider_credential_revisions", table =>
            {
                table.HasCheckConstraint(
                    "ck_model_provider_credential_revisions_scope_project",
                    "(scope = 'Platform' AND project_id IS NULL) OR " +
                    "(scope = 'Project' AND project_id IS NOT NULL AND length(project_id) > 0)");
                table.HasCheckConstraint("ck_model_provider_credential_revisions_state", "state IN ('Enabled', 'Disabled')");
                table.HasCheckConstraint("ck_model_provider_credential_revisions_revision", "revision > 0");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_revisions_mutation_kind",
                    "mutation_kind IN ('RecordReference', 'Disable')");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_revisions_secret_name",
                    "secret_name ~ '^[A-Za-z0-9-]{1,127}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_revisions_secret_version",
                    "secret_version ~ '^[0-9A-Fa-f]{32}$'");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_revisions_byok_name",
                    "vault_source <> 'Byok' OR secret_name = 'byok-' || lower(scope) || '-' || provider_id");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_revisions_vault_source",
                    "vault_source IN ('Existing', 'Byok')");
                table.HasCheckConstraint(
                    "ck_model_provider_credential_revisions_fingerprint",
                    "request_fingerprint ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(revision => new { revision.CredentialSlotId, revision.Revision });
            entity.Property(revision => revision.CredentialSlotId).HasColumnName("credential_slot_id");
            entity.Property(revision => revision.Revision).HasColumnName("revision");
            entity.Property(revision => revision.Scope).HasColumnName("scope").HasConversion<string>()
                .HasMaxLength(16);
            entity.Property(revision => revision.ProjectId).HasColumnName("project_id").HasMaxLength(32);
            entity.Property(revision => revision.ProviderId).HasColumnName("provider_id")
                .HasMaxLength(128).IsRequired();
            entity.Property(revision => revision.VaultSource).HasColumnName("vault_source")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(revision => revision.VaultUri).HasColumnName("vault_uri")
                .HasMaxLength(256).IsRequired();
            entity.Property(revision => revision.SecretName).HasColumnName("secret_name")
                .HasMaxLength(127).IsRequired();
            entity.Property(revision => revision.SecretVersion).HasColumnName("secret_version")
                .HasMaxLength(32).IsRequired();
            entity.Property(revision => revision.State).HasColumnName("state")
                .HasConversion<string>().HasMaxLength(16);
            entity.Property(revision => revision.MutationKind).HasColumnName("mutation_kind")
                .HasConversion<string>().HasMaxLength(32);
            entity.Property(revision => revision.OperationId).HasColumnName("operation_id");
            entity.Property(revision => revision.RequestFingerprint).HasColumnName("request_fingerprint")
                .HasMaxLength(64).IsRequired();
            entity.Property(revision => revision.ActorId).HasColumnName("actor_id")
                .HasMaxLength(256).IsRequired();
            entity.Property(revision => revision.RecordedAt).HasColumnName("recorded_at");
            entity.HasIndex(revision => revision.OperationId).IsUnique();
            entity.HasOne<ModelProviderCredentialHead>()
                .WithMany()
                .HasForeignKey(revision => revision.CredentialSlotId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
