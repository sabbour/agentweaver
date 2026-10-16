using System.Diagnostics;
using Agentweaver.Telemetry;

namespace Agentweaver.FoundationProbe;

internal interface IProbeOperations
{
    Task<KeyVaultEvidence> RedeemKeyVaultAsync(ProbeTarget target, string nonce, CancellationToken cancellationToken);
    Task<BlobEvidence> RunBlobRoundTripAsync(ProbeTarget target, ProbeSource source, string nonce, CancellationToken cancellationToken);
    Task<PostgresEvidence> WritePostgresEffectsAsync(ProbeTarget target, ProbeSource source, string nonce, CancellationToken cancellationToken);
}

internal sealed class ProbeRunner(
    IProbeOperations operations,
    Func<bool> forceFlush,
    Func<string>? createNonce = null)
{
    private static readonly System.Text.RegularExpressions.Regex NoncePattern =
        new("^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private readonly Func<string> _createNonce = createNonce ?? (() => Guid.NewGuid().ToString("N"));

    public async Task<ProbeReceipt> RunAsync(
        ProbeTarget target,
        ProbeSource source,
        ProbeIdentityEvidence identity,
        MonitorConfigurationEvidence monitorConfiguration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(forceFlush);
        ArgumentNullException.ThrowIfNull(identity);
        ProbeTargetValidator.Validate(target, source);
        if (identity.Issuer != target.AksOidcIssuerUrl ||
            identity.Subject != "system:serviceaccount:agentweaver-v1-p0:foundation-probe" ||
            identity.Audience != "api://AzureADTokenExchange")
            throw new ProbeException("workload_identity_claim_mismatch");
        if (!ProbeMonitorConfiguration.IsValidFor(monitorConfiguration, target))
            throw new ProbeException("monitor_configuration_invalid");
        cancellationToken.ThrowIfCancellationRequested();

        var nonce = _createNonce();
        if (nonce is null || !NoncePattern.IsMatch(nonce))
            throw new ProbeException("probe_nonce_invalid");
        var bindings = ProbeProviderBindings.ResolveAndPin(target, nonce);

        var activity = TelemetrySignals.Activities.StartActivity("foundation-probe", ActivityKind.Server)
            ?? throw new ProbeException("telemetry_activity_unavailable");
        var startedAt = new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero);
        activity.SetTag("probe.source_sha", source.Sha);
        activity.SetTag("probe.source_tree", source.Tree);
        activity.SetTag("probe.nonce", nonce);

        KeyVaultEvidence keyVault;
        BlobEvidence blob;
        PostgresEvidence postgres;
        try
        {
            if (!activity.Recorded)
                throw new ProbeException("telemetry_activity_not_recorded");
            keyVault = await operations.RedeemKeyVaultAsync(target, nonce, cancellationToken).ConfigureAwait(false);
            ValidateKeyVaultEvidence(target, keyVault);
            blob = await operations.RunBlobRoundTripAsync(target, source, nonce, cancellationToken).ConfigureAwait(false);
            ValidateBlobEvidence(target, nonce, blob);
            postgres = await operations.WritePostgresEffectsAsync(target, source, nonce, cancellationToken).ConfigureAwait(false);
            ValidatePostgresEvidence(target, nonce, postgres);
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception exception)
        {
            activity.SetStatus(ActivityStatusCode.Error, exception is ProbeException probe ? probe.Code : "probe_failed");
            activity.Dispose();
            throw;
        }

        var traceId = activity.TraceId.ToHexString();
        var spanId = activity.SpanId.ToHexString();
        activity.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        if (!forceFlush())
            throw new ProbeException("telemetry_flush_failed");

        return new ProbeReceipt(
            source.Sha,
            source.Tree,
            target.SourceHash,
            nonce,
            new ProbeDeploymentBinding(
                target.SubscriptionId, target.TenantId, target.ResourceGroup, target.ResourceGroupId,
                target.DeploymentName, target.DeploymentId, target.AksOidcIssuerUrl,
                target.FoundationProbeIdentity, target.FoundationResources, target.Infrastructure),
            identity,
            monitorConfiguration,
            bindings,
            keyVault,
            blob,
            postgres,
            new TelemetryEvidence("foundation-probe", traceId, spanId, startedAt));
    }

    private static void ValidateKeyVaultEvidence(ProbeTarget target, KeyVaultEvidence evidence)
    {
        if (evidence is null || !evidence.Redeemed ||
            evidence.ResourceId != target.FoundationResources.KeyVaultId ||
            evidence.VaultUri != target.FoundationResources.VaultUri ||
            evidence.SecretName != target.Runtime.KeyVaultSecretName ||
            evidence.SecretVersion != target.Runtime.KeyVaultSecretVersion)
            throw new ProbeException("key_vault_evidence_mismatch");
    }

    private static void ValidateBlobEvidence(ProbeTarget target, string nonce, BlobEvidence evidence)
    {
        if (evidence is null || !evidence.CleanupConfirmed ||
            evidence.ContainerResourceId != target.FoundationResources.BlobContainerId ||
            evidence.ContainerUri != target.FoundationResources.BlobContainerUri ||
            evidence.ObjectKey != $"foundation-probe/{nonce}/roundtrip.json" ||
            evidence.OwnershipNonce != nonce || string.IsNullOrWhiteSpace(evidence.ETag) ||
            !System.Text.RegularExpressions.Regex.IsMatch(evidence.ContentSha256, "^[0-9a-f]{64}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ProbeException("blob_evidence_mismatch");
    }

    private static void ValidatePostgresEvidence(ProbeTarget target, string nonce, PostgresEvidence evidence)
    {
        if (evidence is null || !evidence.TransactionCommitted ||
            evidence.ServerResourceId != target.FoundationResources.PostgresServerId ||
            evidence.Host != target.FoundationResources.PostgresHost ||
            evidence.DatabaseName != target.Runtime.DatabaseName ||
            evidence.SchemaName != target.Runtime.SchemaName ||
            evidence.RuntimeRole != target.Runtime.DatabaseRole ||
            evidence.InboxMessageId != nonce || evidence.InboxDisposition != "Admitted" ||
            evidence.OutboxSequence < 1 || !Guid.TryParse(evidence.EffectId, out _) ||
            !Guid.TryParse(evidence.OutboxEventId, out _))
            throw new ProbeException("postgres_evidence_mismatch");
    }
}
