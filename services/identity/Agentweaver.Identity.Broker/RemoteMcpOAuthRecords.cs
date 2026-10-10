using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

public sealed class RemoteMcpOAuthConnectionRecord
{
    public Guid Id { get; set; }
    public required Guid OwnerId { get; set; }
    public required string OwnerIssuer { get; set; }
    public required string OwnerActorId { get; set; }
    public required string TenantId { get; set; }
    public required string ProjectId { get; set; }
    public required Guid ConnectionId { get; set; }
    public required long ConfigurationRevision { get; set; }
    public required string EnvironmentConfigurationHash { get; set; }
    public required string IdentityBindingReference { get; set; }
    public required string EndpointUri { get; set; }
    public required string ResourceUri { get; set; }
    public required string IssuerUri { get; set; }
    public required string ClientId { get; set; }
    public required string RedirectUri { get; set; }
    public required string TransportProfile { get; set; }
    public required string ScopesJson { get; set; }
    public required string BindingHash { get; set; }
    public required long ConnectionRevision { get; set; }
    public required long CredentialRevision { get; set; }
    public required RemoteMcpOAuthConnectionState State { get; set; }
    public string? AccessTokenSecretId { get; set; }
    public string? AccessTokenSecretVersion { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public string? RefreshTokenSecretId { get; set; }
    public string? RefreshTokenSecretVersion { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
    public Guid? RefreshAttemptId { get; set; }
    public DateTimeOffset? RefreshStartedAt { get; set; }
    public string? LastDisconnectIdempotencyHash { get; set; }
    public string? LastDisconnectRequestHash { get; set; }
    public long? LastDisconnectResultRevision { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RemoteMcpOAuthConsentRecord
{
    public Guid Id { get; set; }
    public required Guid ConnectionRecordId { get; set; }
    public required Guid CorrelationId { get; set; }
    public required string BindingHash { get; set; }
    public required long ConnectionRevision { get; set; }
    public required string StateHash { get; set; }
    public required string PkceChallenge { get; set; }
    public required string VerifierSecretId { get; set; }
    public required string VerifierSecretVersion { get; set; }
    public required string AuthorizationEndpointUri { get; set; }
    public required string TokenEndpointUri { get; set; }
    public required long Revision { get; set; }
    public required RemoteMcpOAuthConsentState State { get; set; }
    public Guid? ClaimAttemptId { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal static class RemoteMcpOAuthModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<RemoteMcpOAuthConnectionRecord>(entity =>
        {
            entity.ToTable("remote_mcp_oauth_connections", table =>
            {
                table.HasCheckConstraint(
                    "ck_remote_mcp_oauth_connection_revisions",
                    "configuration_revision > 0 AND connection_revision > 0 AND credential_revision >= 0");
                table.HasCheckConstraint(
                    "ck_remote_mcp_oauth_connection_hashes",
                    "environment_configuration_hash ~ '^[0-9a-f]{64}$' AND binding_hash ~ '^[0-9a-f]{64}$' " +
                    "AND (last_disconnect_idempotency_hash IS NULL OR " +
                    "last_disconnect_idempotency_hash ~ '^[0-9a-f]{64}$') AND " +
                    "(last_disconnect_request_hash IS NULL OR last_disconnect_request_hash ~ '^[0-9a-f]{64}$')");
                table.HasCheckConstraint(
                    "ck_remote_mcp_oauth_connection_refs",
                    "(access_token_secret_id IS NULL) = (access_token_secret_version IS NULL) AND " +
                    "(access_token_secret_id IS NULL) = (access_token_expires_at IS NULL) AND " +
                    "(refresh_token_secret_id IS NULL) = (refresh_token_secret_version IS NULL) AND " +
                    "(refresh_token_secret_id IS NOT NULL OR refresh_token_expires_at IS NULL)");
                table.HasCheckConstraint(
                    "ck_remote_mcp_oauth_connection_state",
                    "state IN (0, 1, 2, 3, 4, 5, 6) AND " +
                    "((state IN (2, 3, 4) AND access_token_secret_id IS NOT NULL) OR " +
                    "(state NOT IN (2, 3, 4) AND access_token_secret_id IS NULL)) AND " +
                    "((state IN (3, 4) AND refresh_token_secret_id IS NOT NULL AND " +
                    "refresh_attempt_id IS NOT NULL AND refresh_started_at IS NOT NULL) OR " +
                    "(state NOT IN (3, 4) AND refresh_attempt_id IS NULL AND refresh_started_at IS NULL))");
                table.HasCheckConstraint(
                    "ck_remote_mcp_oauth_connection_disconnect",
                    "(last_disconnect_idempotency_hash IS NULL) = (last_disconnect_request_hash IS NULL) AND " +
                    "(last_disconnect_idempotency_hash IS NULL) = (last_disconnect_result_revision IS NULL)");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.OwnerId).HasColumnName("owner_id");
            entity.Property(row => row.OwnerIssuer).HasColumnName("owner_issuer").HasMaxLength(2048).IsRequired();
            entity.Property(row => row.OwnerActorId).HasColumnName("owner_actor_id").HasMaxLength(512).IsRequired();
            entity.Property(row => row.TenantId).HasColumnName("tenant_id").HasMaxLength(256).IsRequired();
            entity.Property(row => row.ProjectId).HasColumnName("project_id").HasMaxLength(256).IsRequired();
            entity.Property(row => row.ConnectionId).HasColumnName("connection_id");
            entity.Property(row => row.ConfigurationRevision).HasColumnName("configuration_revision");
            entity.Property(row => row.EnvironmentConfigurationHash)
                .HasColumnName("environment_configuration_hash").HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(row => row.IdentityBindingReference)
                .HasColumnName("identity_binding_reference").HasMaxLength(32).IsFixedLength().IsRequired();
            entity.Property(row => row.EndpointUri).HasColumnName("endpoint_uri").HasMaxLength(2048).IsRequired();
            entity.Property(row => row.ResourceUri).HasColumnName("resource_uri").HasMaxLength(2048).IsRequired();
            entity.Property(row => row.IssuerUri).HasColumnName("issuer_uri").HasMaxLength(2048).IsRequired();
            entity.Property(row => row.ClientId).HasColumnName("client_id").HasMaxLength(256).IsRequired();
            entity.Property(row => row.RedirectUri).HasColumnName("redirect_uri").HasMaxLength(2048).IsRequired();
            entity.Property(row => row.TransportProfile).HasColumnName("transport_profile").HasMaxLength(256).IsRequired();
            entity.Property(row => row.ScopesJson).HasColumnName("scopes_json").HasMaxLength(4096).IsRequired();
            entity.Property(row => row.BindingHash)
                .HasColumnName("binding_hash").HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(row => row.ConnectionRevision).HasColumnName("connection_revision");
            entity.Property(row => row.CredentialRevision).HasColumnName("credential_revision");
            entity.Property(row => row.State).HasColumnName("state").HasConversion<int>();
            entity.Property(row => row.AccessTokenSecretId)
                .HasColumnName("access_token_secret_id").HasMaxLength(256);
            entity.Property(row => row.AccessTokenSecretVersion)
                .HasColumnName("access_token_secret_version").HasMaxLength(256);
            entity.Property(row => row.AccessTokenExpiresAt).HasColumnName("access_token_expires_at");
            entity.Property(row => row.RefreshTokenSecretId)
                .HasColumnName("refresh_token_secret_id").HasMaxLength(256);
            entity.Property(row => row.RefreshTokenSecretVersion)
                .HasColumnName("refresh_token_secret_version").HasMaxLength(256);
            entity.Property(row => row.RefreshTokenExpiresAt).HasColumnName("refresh_token_expires_at");
            entity.Property(row => row.RefreshAttemptId).HasColumnName("refresh_attempt_id");
            entity.Property(row => row.RefreshStartedAt).HasColumnName("refresh_started_at");
            entity.Property(row => row.LastDisconnectIdempotencyHash)
                .HasColumnName("last_disconnect_idempotency_hash").HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.LastDisconnectRequestHash)
                .HasColumnName("last_disconnect_request_hash").HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.LastDisconnectResultRevision)
                .HasColumnName("last_disconnect_result_revision");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.UpdatedAt).HasColumnName("updated_at");
            entity.Property(row => row.ConnectionRevision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.OwnerId, row.TenantId, row.ProjectId, row.ConnectionId }).IsUnique();
            entity.HasIndex(row => row.IdentityBindingReference).IsUnique();
            entity.HasOne<BrokerUser>().WithMany()
                .HasForeignKey(row => row.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<RemoteMcpOAuthConsentRecord>(entity =>
        {
            entity.ToTable("remote_mcp_oauth_consents", table =>
            {
                table.HasCheckConstraint(
                    "ck_remote_mcp_oauth_consent_revisions",
                    "connection_revision > 0 AND revision > 0 AND expires_at > created_at");
                table.HasCheckConstraint(
                    "ck_remote_mcp_oauth_consent_hashes",
                    "binding_hash ~ '^[0-9a-f]{64}$' AND state_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_remote_mcp_oauth_consent_challenge",
                    "pkce_challenge ~ '^[A-Za-z0-9_-]{43}$'");
                table.HasCheckConstraint(
                    "ck_remote_mcp_oauth_consent_state",
                    "state IN (0, 1, 2, 3, 4) AND " +
                    "((state = 1 AND claim_attempt_id IS NOT NULL) OR " +
                    "(state <> 1 AND claim_attempt_id IS NULL))");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).HasColumnName("id");
            entity.Property(row => row.ConnectionRecordId).HasColumnName("connection_record_id");
            entity.Property(row => row.CorrelationId).HasColumnName("correlation_id");
            entity.Property(row => row.BindingHash)
                .HasColumnName("binding_hash").HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(row => row.ConnectionRevision).HasColumnName("connection_revision");
            entity.Property(row => row.StateHash)
                .HasColumnName("state_hash").HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(row => row.PkceChallenge).HasColumnName("pkce_challenge").HasMaxLength(43).IsRequired();
            entity.Property(row => row.VerifierSecretId)
                .HasColumnName("verifier_secret_id").HasMaxLength(256).IsRequired();
            entity.Property(row => row.VerifierSecretVersion)
                .HasColumnName("verifier_secret_version").HasMaxLength(256).IsRequired();
            entity.Property(row => row.AuthorizationEndpointUri)
                .HasColumnName("authorization_endpoint_uri").HasMaxLength(2048).IsRequired();
            entity.Property(row => row.TokenEndpointUri)
                .HasColumnName("token_endpoint_uri").HasMaxLength(2048).IsRequired();
            entity.Property(row => row.Revision).HasColumnName("revision");
            entity.Property(row => row.State).HasColumnName("state").HasConversion<int>();
            entity.Property(row => row.ClaimAttemptId).HasColumnName("claim_attempt_id");
            entity.Property(row => row.CreatedAt).HasColumnName("created_at");
            entity.Property(row => row.ExpiresAt).HasColumnName("expires_at");
            entity.Property(row => row.CompletedAt).HasColumnName("completed_at");
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => row.CorrelationId).IsUnique();
            entity.HasIndex(row => row.StateHash).IsUnique();
            entity.HasIndex(row => row.ExpiresAt);
            entity.HasOne<RemoteMcpOAuthConnectionRecord>().WithMany()
                .HasForeignKey(row => row.ConnectionRecordId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
