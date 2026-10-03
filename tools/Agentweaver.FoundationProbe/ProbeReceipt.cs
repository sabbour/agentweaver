namespace Agentweaver.FoundationProbe;

internal sealed record ProbeReceipt(
    string SourceSha,
    string SourceTree,
    string SourceHash,
    string Nonce,
    ProbeDeploymentBinding Deployment,
    ProbeIdentityEvidence WorkloadIdentity,
    MonitorConfigurationEvidence MonitorConfiguration,
    IReadOnlyList<ProviderPinEvidence> ProviderBindings,
    KeyVaultEvidence KeyVault,
    BlobEvidence Blob,
    PostgresEvidence Postgres,
    TelemetryEvidence Telemetry);

internal sealed record ProbeDeploymentBinding(
    string SubscriptionId,
    string TenantId,
    string ResourceGroup,
    string ResourceGroupId,
    string DeploymentName,
    string DeploymentId,
    string AksOidcIssuerUrl,
    ProbeIdentity Identity,
    ProbeResources Resources);

internal sealed record ProbeIdentityEvidence(string Issuer, string Subject, string Audience);

internal sealed record ProviderPinEvidence(
    string Seam,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    string ResourceId,
    long Generation,
    IReadOnlyList<string> NegotiatedCapabilities);

internal sealed record KeyVaultEvidence(
    string ResourceId,
    string VaultUri,
    string SecretName,
    string SecretVersion,
    bool Redeemed);

internal sealed record BlobEvidence(
    string ContainerResourceId,
    string ContainerUri,
    string ObjectKey,
    string OwnershipNonce,
    string ETag,
    string ContentSha256,
    bool CleanupConfirmed);

internal sealed record PostgresEvidence(
    string ServerResourceId,
    string Host,
    string DatabaseName,
    string SchemaName,
    string RuntimeRole,
    string EffectId,
    string InboxMessageId,
    string InboxDisposition,
    string OutboxEventId,
    long OutboxSequence,
    bool TransactionCommitted);

internal sealed record TelemetryEvidence(
    string Name,
    string TraceId,
    string SpanId,
    DateTimeOffset StartedAt);
