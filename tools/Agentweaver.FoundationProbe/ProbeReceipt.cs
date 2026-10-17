namespace Agentweaver.FoundationProbe;

/// <summary>Machine-readable evidence produced after all target-bound foundation checks succeed.</summary>
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

/// <summary>Deployment and infrastructure provenance copied into the final receipt.</summary>
internal sealed record ProbeDeploymentBinding(
    string SubscriptionId,
    string TenantId,
    string ResourceGroup,
    string ResourceGroupId,
    string DeploymentName,
    string DeploymentId,
    string AksOidcIssuerUrl,
    ProbeIdentity Identity,
    ProbeResources Resources,
    ProbeInfrastructure Infrastructure);

/// <summary>Issuer, subject, and audience claims observed in the projected workload token.</summary>
internal sealed record ProbeIdentityEvidence(string Issuer, string Subject, string Audience);

/// <summary>Provider seam and pinned resource identity recorded for the probe run.</summary>
internal sealed record ProviderPinEvidence(
    string Seam,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    string ResourceId,
    long Generation,
    IReadOnlyList<string> NegotiatedCapabilities);

/// <summary>Evidence that the configured version of the probe secret was redeemed.</summary>
internal sealed record KeyVaultEvidence(
    string ResourceId,
    string VaultUri,
    string SecretName,
    string SecretVersion,
    bool Redeemed);

/// <summary>Evidence for a nonce-owned blob round trip and confirmed cleanup.</summary>
internal sealed record BlobEvidence(
    string ContainerResourceId,
    string ContainerUri,
    string ObjectKey,
    string OwnershipNonce,
    string ETag,
    string ContentSha256,
    bool CleanupConfirmed);

/// <summary>Evidence for the committed PostgreSQL effect, inbox receipt, and outbox event.</summary>
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

/// <summary>Trace identity and start time for the emitted probe operation.</summary>
internal sealed record TelemetryEvidence(
    string Name,
    string TraceId,
    string SpanId,
    DateTimeOffset StartedAt);
