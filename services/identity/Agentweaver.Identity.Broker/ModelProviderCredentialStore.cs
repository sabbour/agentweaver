using System.Data;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Secrets.AzureKeyVault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Identity.Broker;

internal sealed class ModelProviderCredentialVaults
{
    private ModelProviderCredentialVaults(
        AzureKeyVaultConfiguration existing,
        AzureKeyVaultConfiguration? byok)
    {
        Existing = existing;
        Byok = byok;
    }

    internal AzureKeyVaultConfiguration Existing { get; }
    internal AzureKeyVaultConfiguration? Byok { get; }

    internal static ModelProviderCredentialVaults Create(string? existingUri, string? byokUri)
    {
        if (string.IsNullOrWhiteSpace(existingUri) ||
            !Uri.TryCreate(existingUri, UriKind.Absolute, out var existingRoot))
            throw ModelProviderCredentialException.InvalidRequest();

        var existing = new AzureKeyVaultConfiguration(existingRoot);
        if (byokUri is null)
            return new(existing, null);
        if (string.IsNullOrWhiteSpace(byokUri) ||
            !Uri.TryCreate(byokUri, UriKind.Absolute, out var byokRoot))
            throw ModelProviderCredentialException.InvalidRequest();

        var byok = new AzureKeyVaultConfiguration(byokRoot);
        if (existing.VaultUri.Equals(byok.VaultUri))
            throw ModelProviderCredentialException.InvalidRequest();
        return new(existing, byok);
    }

    internal string Resolve(ModelProviderCredentialVaultSource source) =>
        source switch
        {
            ModelProviderCredentialVaultSource.Existing => Existing.VaultUri.AbsoluteUri,
            ModelProviderCredentialVaultSource.Byok when Byok is not null => Byok.VaultUri.AbsoluteUri,
            ModelProviderCredentialVaultSource.Byok => throw ModelProviderCredentialException.VaultUnavailable(),
            _ => throw ModelProviderCredentialException.InvalidRequest(),
        };

    internal void RequireConfigured(ModelProviderCredentialVaultSource source, string vaultUri)
    {
        var configuredUri = Resolve(source);
        if (!StringComparer.Ordinal.Equals(configuredUri, vaultUri))
            throw ModelProviderCredentialException.VaultUnavailable();
    }

    internal string ResolveEgressHost(string? configuredHost)
    {
        var expectedHost = (Byok ?? Existing).VaultUri.Host;
        if (Byok is not null && string.IsNullOrWhiteSpace(configuredHost) ||
            configuredHost is not null &&
                !StringComparer.OrdinalIgnoreCase.Equals(configuredHost, expectedHost))
            throw ModelProviderCredentialException.InvalidRequest();
        return configuredHost ?? expectedHost;
    }
}

internal sealed class ModelProviderCredentialStore(
    IdentityBrokerDbContext db,
    ModelProviderCredentialVaults vaults,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions FingerprintJson = new(JsonSerializerDefaults.Web);

    internal Task<ModelProviderCredentialReference> RecordReferenceAsync(
        RecordModelProviderCredentialReference request,
        CancellationToken cancellationToken = default) =>
        RecordReferenceCoreAsync(request, null, cancellationToken);

    internal Task<ModelProviderCredentialWriteClaimResult> ClaimWriteAsync(
        ClaimModelProviderCredentialWrite request,
        CancellationToken cancellationToken = default) =>
        ClaimWriteCoreAsync(request, null, cancellationToken);

    internal async Task<ModelProviderCredentialPublicationClaimResult> ClaimPublicationWriteAsync(
        ClaimModelProviderCredentialPublication request,
        CancellationToken cancellationToken = default)
    {
        ValidatePublicationClaim(request);
        var result = await ClaimWriteCoreAsync(request.Claim, request, cancellationToken).ConfigureAwait(false);
        return await ToPublicationResultAsync(request.Claim.OperationId, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelProviderCredentialWriteClaimResult> ClaimWriteCoreAsync(
        ClaimModelProviderCredentialWrite request,
        ClaimModelProviderCredentialPublication? publication,
        CancellationToken cancellationToken)
    {
        ValidateClaimRequest(request);
        var vaultUri = vaults.Resolve(request.VaultSource);
        ValidateSecretName(request.SecretName);
        ValidateByokName(request.Scope, request.ProviderId, request.VaultSource, request.SecretName);
        var fingerprint = BuildClaimFingerprint(request, publication, vaultUri);
        var replay = await ReadWriteClaimReplayAsync(request.OperationId, fingerprint, publication is not null, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
            return replay;
        if (await db.ModelProviderCredentialRevisions.AsNoTracking()
                .AnyAsync(row => row.OperationId == request.OperationId, cancellationToken)
                .ConfigureAwait(false))
            throw ModelProviderCredentialException.Conflict();

        try
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            await LockCredentialWriteAsync(request.ProviderId, request.CredentialSlotId, request.OperationId, cancellationToken).ConfigureAwait(false);
            if (await db.ModelProviderCredentialRevisions.AsNoTracking()
                    .AnyAsync(row => row.OperationId == request.OperationId, cancellationToken)
                    .ConfigureAwait(false))
                throw ModelProviderCredentialException.Conflict();
            if (await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                    .AnyAsync(row => row.OperationId == request.OperationId ||
                        ((row.ProviderId == request.ProviderId ||
                          row.CredentialSlotId == request.CredentialSlotId) &&
                         (row.State == ModelProviderCredentialWriteClaimState.Claimed ||
                          row.State == ModelProviderCredentialWriteClaimState.Quarantined ||
                          row.State == ModelProviderCredentialWriteClaimState.PublicationPending)), cancellationToken)
                    .ConfigureAwait(false))
                throw ModelProviderCredentialException.Conflict();
            var head = await db.ModelProviderCredentialHeads.AsNoTracking()
                .SingleOrDefaultAsync(row => row.CredentialSlotId == request.CredentialSlotId, cancellationToken)
                .ConfigureAwait(false);
            if (head is null)
            {
                if (request.ExpectedRevision != 0 ||
                    await db.ModelProviderCredentialHeads.AsNoTracking()
                        .AnyAsync(row => row.ProviderId == request.ProviderId, cancellationToken)
                        .ConfigureAwait(false))
                    throw ModelProviderCredentialException.Conflict();
            }
            else
            {
                RequireIdentity(head, request.CredentialSlotId, request.Scope, request.ProjectId, request.ProviderId);
                vaults.RequireConfigured(head.VaultSource, head.VaultUri);
                if (head.VaultSource != request.VaultSource ||
                    !StringComparer.Ordinal.Equals(head.VaultUri, vaultUri) ||
                    head.State != ModelProviderCredentialState.Enabled ||
                    head.Revision != request.ExpectedRevision)
                    throw ModelProviderCredentialException.Conflict();
            }

            db.ModelProviderCredentialWriteClaims.Add(new ModelProviderCredentialWriteClaim
            {
                OperationId = request.OperationId,
                CredentialSlotId = request.CredentialSlotId,
                Scope = request.Scope,
                ProjectId = request.ProjectId,
                ProviderId = request.ProviderId,
                VaultSource = request.VaultSource,
                VaultUri = vaultUri,
                SecretName = request.SecretName,
                ExpectedRevision = request.ExpectedRevision,
                RequestFingerprint = fingerprint,
                ActorId = request.ActorId,
                State = ModelProviderCredentialWriteClaimState.Claimed,
                ClaimedAt = timeProvider.GetUtcNow(),
                ExpectedProviderRevision = publication?.ExpectedProviderRevision,
                Purpose = publication?.Purpose,
                Issuer = publication?.Issuer,
                Tenant = publication?.Tenant,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(ModelProviderCredentialWriteClaimStatus.Acquired, true, null);
        }
        catch (Exception exception)
        {
            DetachCredentialEntities();
            if (!IsConflict(exception))
                throw;
            var concurrentReplay = await ReadWriteClaimReplayAsync(request.OperationId, fingerprint, publication is not null, cancellationToken)
                .ConfigureAwait(false);
            return concurrentReplay ?? throw ModelProviderCredentialException.Conflict();
        }
    }

    internal Task<ModelProviderCredentialWriteClaimResult> MarkWriteUncertainAsync(
        ClaimModelProviderCredentialWrite request,
        CancellationToken cancellationToken = default) =>
        MarkWriteUncertainCoreAsync(request, null, cancellationToken);

    internal async Task<ModelProviderCredentialPublicationClaimResult> MarkPublicationWriteUncertainAsync(
        ClaimModelProviderCredentialPublication request,
        CancellationToken cancellationToken = default)
    {
        ValidatePublicationClaim(request);
        var result = await MarkWriteUncertainCoreAsync(request.Claim, request, cancellationToken).ConfigureAwait(false);
        return await ToPublicationResultAsync(request.Claim.OperationId, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelProviderCredentialWriteClaimResult> MarkWriteUncertainCoreAsync(
        ClaimModelProviderCredentialWrite request,
        ClaimModelProviderCredentialPublication? publication,
        CancellationToken cancellationToken)
    {
        ValidateClaimRequest(request);
        var fingerprint = BuildClaimFingerprint(request, publication, vaults.Resolve(request.VaultSource));
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockCredentialWriteAsync(request.ProviderId, request.CredentialSlotId, request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var claim = await db.ModelProviderCredentialWriteClaims.AsNoTracking()
            .SingleOrDefaultAsync(row => row.OperationId == request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (claim is null || (claim.ExpectedProviderRevision is not null) != (publication is not null) ||
            !StringComparer.Ordinal.Equals(claim.RequestFingerprint, fingerprint))
            throw ModelProviderCredentialException.Conflict();

        if (claim.State == ModelProviderCredentialWriteClaimState.Claimed)
        {
            var now = timeProvider.GetUtcNow();
            _ = await db.ModelProviderCredentialWriteClaims
                .Where(row => row.OperationId == request.OperationId &&
                    row.RequestFingerprint == fingerprint &&
                    row.State == ModelProviderCredentialWriteClaimState.Claimed)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.State, ModelProviderCredentialWriteClaimState.Quarantined)
                    .SetProperty(row => row.QuarantinedAt, now), cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await ReadWriteClaimReplayAsync(request.OperationId, fingerprint, publication is not null, cancellationToken)
            .ConfigureAwait(false) ?? throw ModelProviderCredentialException.Conflict();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal async Task<ModelProviderCredentialReference> CompleteWriteAsync(
        RecordModelProviderCredentialReference request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var vaultUri = vaults.Resolve(request.VaultSource);
        ValidateReference(request.Reference);
        ValidateByokName(request.Scope, request.ProviderId, request.VaultSource, request.Reference.Id);
        var claimRequest = ToWriteClaimRequest(request);
        var claimFingerprint = BuildWriteClaimFingerprint(claimRequest, vaultUri);
        var claim = await db.ModelProviderCredentialWriteClaims.AsNoTracking()
            .SingleOrDefaultAsync(row => row.OperationId == request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (claim is null || claim.ExpectedProviderRevision is not null ||
            !StringComparer.Ordinal.Equals(claim.RequestFingerprint, claimFingerprint))
            throw ModelProviderCredentialException.Conflict();
        if (claim.State == ModelProviderCredentialWriteClaimState.Quarantined)
            throw ModelProviderCredentialException.Conflict();

        var recordFingerprint = BuildRecordFingerprint(request, vaultUri);
        if (claim.State == ModelProviderCredentialWriteClaimState.Completed)
        {
            var completed = await ReadReplayAsync(request.OperationId, recordFingerprint, cancellationToken)
                .ConfigureAwait(false);
            return completed is not null && completed.Revision == claim.CompletedRevision
                ? completed
                : throw ModelProviderCredentialException.Conflict();
        }

        if (await db.ModelProviderCredentialRevisions.AsNoTracking()
                .AnyAsync(row => row.OperationId == request.OperationId, cancellationToken)
                .ConfigureAwait(false))
            throw ModelProviderCredentialException.Conflict();
        return await RecordReferenceCoreAsync(request, claimFingerprint, cancellationToken).ConfigureAwait(false);
    }

    internal Task<ModelProviderCredentialPublicationClaimResult> CompletePublicationWriteAsync(
        ClaimModelProviderCredentialPublication request,
        SecretRef reference,
        CancellationToken cancellationToken = default) =>
        StagePendingPublicationAsync(request, reference, recoverFromQuarantine: false, cancellationToken);

    internal async Task<ModelProviderCredentialPublicationClaimResult> AcknowledgePublicationAsync(
        ClaimModelProviderCredentialPublication request,
        ModelProviderCredentialPublicationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ValidatePublicationClaim(request);
        if (receipt is null || !receipt.IsValid)
            throw ModelProviderCredentialException.InvalidRequest();
        var claim = request.Claim;
        var vaultUri = vaults.Resolve(claim.VaultSource);
        var fingerprint = BuildClaimFingerprint(claim, request, vaultUri);
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockCredentialWriteAsync(claim.ProviderId, claim.CredentialSlotId, claim.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var replay = await ReadWriteClaimReplayAsync(claim.OperationId, fingerprint, true, cancellationToken)
            .ConfigureAwait(false) ?? throw ModelProviderCredentialException.Conflict();
        var credential = replay.Credential;
        var scope = claim.Scope == ModelProviderCredentialScope.Platform
            ? ModelProviderBindingScope.Platform : ModelProviderBindingScope.Project;
        if (replay.Status is not (ModelProviderCredentialWriteClaimStatus.PublicationPending or ModelProviderCredentialWriteClaimStatus.Published) ||
            credential is null || receipt.OperationId != claim.OperationId || receipt.Scope != scope ||
            receipt.ProjectId != claim.ProjectId || receipt.ProviderId != claim.ProviderId ||
            receipt.CredentialIdentity.CredentialSlotId != claim.CredentialSlotId ||
            receipt.ProviderRevision != request.ExpectedProviderRevision + 1 ||
            receipt.CredentialReference != credential.Reference ||
            receipt.CredentialVersion != credential.Reference.Version)
            throw ModelProviderCredentialException.Conflict();

        if (replay.Status == ModelProviderCredentialWriteClaimStatus.PublicationPending)
        {
            var now = timeProvider.GetUtcNow();
            var nextRevision = claim.ExpectedRevision + 1;
            var record = new RecordModelProviderCredentialReference(
                claim.OperationId, claim.ExpectedRevision, claim.CredentialSlotId,
                claim.Scope, claim.ProjectId, claim.ProviderId, claim.VaultSource,
                credential.Reference, claim.ActorId);
            var recordFingerprint = BuildRecordFingerprint(record, vaultUri);

            var head = await db.ModelProviderCredentialHeads.AsNoTracking()
                .SingleOrDefaultAsync(row => row.CredentialSlotId == claim.CredentialSlotId, cancellationToken)
                .ConfigureAwait(false);
            if (head is null)
            {
                if (claim.ExpectedRevision != 0)
                    throw ModelProviderCredentialException.Conflict();
                db.ModelProviderCredentialHeads.Add(new ModelProviderCredentialHead
                {
                    CredentialSlotId = claim.CredentialSlotId,
                    Scope = claim.Scope,
                    ProjectId = claim.ProjectId,
                    ProviderId = claim.ProviderId,
                    VaultSource = claim.VaultSource,
                    VaultUri = vaultUri,
                    SecretName = claim.SecretName,
                    CurrentVersion = credential.Reference.Version,
                    State = ModelProviderCredentialState.Enabled,
                    Revision = nextRevision,
                    SetBy = claim.ActorId,
                    SetAt = now,
                });
            }
            else
            {
                RequireIdentity(head, claim.CredentialSlotId, claim.Scope, claim.ProjectId, claim.ProviderId);
                vaults.RequireConfigured(head.VaultSource, head.VaultUri);
                if (head.VaultSource != claim.VaultSource ||
                    !StringComparer.Ordinal.Equals(head.VaultUri, vaultUri) ||
                    head.State != ModelProviderCredentialState.Enabled ||
                    head.Revision != claim.ExpectedRevision)
                    throw ModelProviderCredentialException.Conflict();

                var headUpdated = await db.ModelProviderCredentialHeads
                    .Where(row => row.CredentialSlotId == claim.CredentialSlotId &&
                        row.Revision == claim.ExpectedRevision &&
                        row.State == ModelProviderCredentialState.Enabled)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.SecretName, claim.SecretName)
                        .SetProperty(row => row.CurrentVersion, credential.Reference.Version)
                        .SetProperty(row => row.Revision, nextRevision)
                        .SetProperty(row => row.SetBy, claim.ActorId)
                        .SetProperty(row => row.SetAt, now), cancellationToken)
                    .ConfigureAwait(false);
                if (headUpdated != 1)
                    throw ModelProviderCredentialException.Conflict();
            }

            db.ModelProviderCredentialRevisions.Add(new ModelProviderCredentialRevision
            {
                CredentialSlotId = claim.CredentialSlotId,
                Revision = nextRevision,
                Scope = claim.Scope,
                ProjectId = claim.ProjectId,
                ProviderId = claim.ProviderId,
                VaultSource = claim.VaultSource,
                VaultUri = vaultUri,
                SecretName = claim.SecretName,
                SecretVersion = credential.Reference.Version,
                State = ModelProviderCredentialState.Enabled,
                MutationKind = ModelProviderCredentialMutationKind.RecordReference,
                OperationId = claim.OperationId,
                RequestFingerprint = recordFingerprint,
                ActorId = claim.ActorId,
                RecordedAt = now,
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var updated = await db.ModelProviderCredentialWriteClaims
                .Where(row => row.OperationId == claim.OperationId && row.RequestFingerprint == fingerprint &&
                    row.State == ModelProviderCredentialWriteClaimState.PublicationPending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.State, ModelProviderCredentialWriteClaimState.Published)
                    .SetProperty(row => row.CompletedRevision, nextRevision)
                    .SetProperty(row => row.CompletedAt, now)
                    .SetProperty(row => row.PublishedProviderRevision, receipt.ProviderRevision)
                    .SetProperty(row => row.PublishedConfigurationHash, receipt.ConfigurationHash)
                    .SetProperty(row => row.PublishedAt, now), cancellationToken).ConfigureAwait(false);
            if (updated != 1)
                throw ModelProviderCredentialException.Conflict();
            replay = await ReadWriteClaimReplayAsync(claim.OperationId, fingerprint, true, cancellationToken)
                .ConfigureAwait(false) ?? throw ModelProviderCredentialException.Conflict();
        }
        var result = await ToPublicationResultAsync(claim.OperationId, replay, cancellationToken).ConfigureAwait(false);
        if (result.PublishedProviderRevision != receipt.ProviderRevision ||
            !StringComparer.Ordinal.Equals(result.PublishedConfigurationHash, receipt.ConfigurationHash))
            throw ModelProviderCredentialException.Conflict();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal async Task<ModelProviderCredentialRevision?> ReadDisableByOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        operationId == Guid.Empty
            ? null
            : await db.ModelProviderCredentialRevisions.AsNoTracking()
                .SingleOrDefaultAsync(row =>
                    row.OperationId == operationId &&
                    row.MutationKind == ModelProviderCredentialMutationKind.Disable, cancellationToken)
                .ConfigureAwait(false);

    internal async Task<bool> HasActiveWriteClaimForProviderAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        await db.ModelProviderCredentialWriteClaims.AsNoTracking()
            .AnyAsync(row =>
                row.ProviderId == providerId &&
                (row.State == ModelProviderCredentialWriteClaimState.Claimed ||
                 row.State == ModelProviderCredentialWriteClaimState.Quarantined ||
                 row.State == ModelProviderCredentialWriteClaimState.PublicationPending), cancellationToken)
            .ConfigureAwait(false);

    internal async Task<ModelProviderCredentialWriteClaim?> ReadWriteClaimByOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        operationId == Guid.Empty
            ? null
            : await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                .SingleOrDefaultAsync(row => row.OperationId == operationId, cancellationToken)
                .ConfigureAwait(false);

    internal async Task<ModelProviderCredentialHead?> ReadHeadByProviderAsync(
        ModelProviderCredentialScope scope,
        string? projectId,
        string providerId,
        CancellationToken cancellationToken = default) =>
        await db.ModelProviderCredentialHeads.AsNoTracking()
            .SingleOrDefaultAsync(row =>
                row.Scope == scope &&
                row.ProjectId == projectId &&
                row.ProviderId == providerId, cancellationToken)
            .ConfigureAwait(false);

    internal async Task MarkPublicationDispatchedAsync(
        ClaimModelProviderCredentialPublication request,
        CancellationToken cancellationToken = default)
    {
        ValidatePublicationClaim(request);
        var claim = request.Claim;
        var fingerprint = BuildClaimFingerprint(claim, request, vaults.Resolve(claim.VaultSource));
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockCredentialWriteAsync(claim.ProviderId, claim.CredentialSlotId, claim.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var row = await db.ModelProviderCredentialWriteClaims.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == claim.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.ExpectedProviderRevision is null ||
            !StringComparer.Ordinal.Equals(row.RequestFingerprint, fingerprint) ||
            row.State != ModelProviderCredentialWriteClaimState.Claimed ||
            row.DispatchedAt is not null)
            throw ModelProviderCredentialException.Conflict();

        var now = timeProvider.GetUtcNow();
        var updated = await db.ModelProviderCredentialWriteClaims
            .Where(item => item.OperationId == claim.OperationId &&
                item.RequestFingerprint == fingerprint &&
                item.State == ModelProviderCredentialWriteClaimState.Claimed &&
                item.DispatchedAt == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.DispatchedAt, now), cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
            throw ModelProviderCredentialException.Conflict();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task<ModelProviderCredentialPublicationClaimResult> RecoverPublicationWriteAsync(
        ClaimModelProviderCredentialPublication request,
        SecretRef reference,
        CancellationToken cancellationToken = default) =>
        StagePendingPublicationAsync(request, reference, recoverFromQuarantine: true, cancellationToken);

    private async Task<ModelProviderCredentialPublicationClaimResult> StagePendingPublicationAsync(
        ClaimModelProviderCredentialPublication request,
        SecretRef reference,
        bool recoverFromQuarantine,
        CancellationToken cancellationToken)
    {
        ValidatePublicationClaim(request);
        ValidateReference(reference);
        var claim = request.Claim;
        if (!StringComparer.Ordinal.Equals(claim.SecretName, reference.Id))
            throw ModelProviderCredentialException.Conflict();
        var vaultUri = vaults.Resolve(claim.VaultSource);
        ValidateSecretName(claim.SecretName);
        ValidateByokName(claim.Scope, claim.ProviderId, claim.VaultSource, claim.SecretName);
        var fingerprint = BuildClaimFingerprint(claim, request, vaultUri);

        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockCredentialWriteAsync(claim.ProviderId, claim.CredentialSlotId, claim.OperationId, cancellationToken)
            .ConfigureAwait(false);

        var replay = await ReadWriteClaimReplayAsync(claim.OperationId, fingerprint, true, cancellationToken)
            .ConfigureAwait(false) ?? throw ModelProviderCredentialException.Conflict();
        if (replay.Status is ModelProviderCredentialWriteClaimStatus.PublicationPending or ModelProviderCredentialWriteClaimStatus.Published)
        {
            if (replay.Credential?.Reference != reference)
                throw ModelProviderCredentialException.Conflict();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ToPublicationResultAsync(claim.OperationId, replay, cancellationToken).ConfigureAwait(false);
        }

        var expectedState = recoverFromQuarantine
            ? ModelProviderCredentialWriteClaimState.Quarantined
            : ModelProviderCredentialWriteClaimState.Claimed;
        var row = await db.ModelProviderCredentialWriteClaims.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == claim.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null ||
            row.ExpectedProviderRevision is null ||
            !StringComparer.Ordinal.Equals(row.RequestFingerprint, fingerprint) ||
            row.State != expectedState ||
            (recoverFromQuarantine && row.DispatchedAt is null))
            throw ModelProviderCredentialException.Conflict();

        var now = timeProvider.GetUtcNow();
        var updated = await db.ModelProviderCredentialWriteClaims
            .Where(item => item.OperationId == claim.OperationId &&
                item.RequestFingerprint == fingerprint &&
                item.State == expectedState)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, ModelProviderCredentialWriteClaimState.PublicationPending)
                .SetProperty(item => item.PendingSecretVersion, reference.Version)
                .SetProperty(item => item.QuarantinedAt, (DateTimeOffset?)null)
                .SetProperty(item => item.RecoveredAt, recoverFromQuarantine ? now : (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
        if (updated != 1)
            throw ModelProviderCredentialException.Conflict();

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        replay = await ReadWriteClaimReplayAsync(claim.OperationId, fingerprint, true, cancellationToken)
            .ConfigureAwait(false) ?? throw ModelProviderCredentialException.Conflict();
        return await ToPublicationResultAsync(claim.OperationId, replay, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ModelProviderCredentialPublicationClaimResult> CancelPublicationClaimAsync(
        ClaimModelProviderCredentialPublication request,
        CancellationToken cancellationToken = default)
    {
        ValidatePublicationClaim(request);
        var claim = request.Claim;
        var fingerprint = BuildClaimFingerprint(claim, request, vaults.Resolve(claim.VaultSource));
        await using var transaction = await db.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockCredentialWriteAsync(claim.ProviderId, claim.CredentialSlotId, claim.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var row = await db.ModelProviderCredentialWriteClaims.AsNoTracking()
            .SingleOrDefaultAsync(item => item.OperationId == claim.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.ExpectedProviderRevision is null ||
            !StringComparer.Ordinal.Equals(row.RequestFingerprint, fingerprint))
            throw ModelProviderCredentialException.Conflict();
        if (row.State == ModelProviderCredentialWriteClaimState.Published)
            throw ModelProviderCredentialException.Conflict();
        if (row.State != ModelProviderCredentialWriteClaimState.Cancelled)
        {
            var now = timeProvider.GetUtcNow();
            var updated = await db.ModelProviderCredentialWriteClaims
                .Where(item => item.OperationId == claim.OperationId &&
                    item.RequestFingerprint == fingerprint &&
                    (item.State == ModelProviderCredentialWriteClaimState.Claimed ||
                     item.State == ModelProviderCredentialWriteClaimState.Quarantined ||
                     item.State == ModelProviderCredentialWriteClaimState.PublicationPending))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, ModelProviderCredentialWriteClaimState.Cancelled)
                    .SetProperty(item => item.CancelledAt, now), cancellationToken)
                .ConfigureAwait(false);
            if (updated != 1)
                throw ModelProviderCredentialException.Conflict();
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(ModelProviderCredentialWriteClaimStatus.Cancelled, false, null, null, null, null);
    }

    internal async Task<ModelProviderCredentialPublicationClaimResult> ReconcilePublishedFromReceiptAsync(
        ClaimModelProviderCredentialPublication request,
        ModelProviderCredentialPublicationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ValidatePublicationClaim(request);
        var claim = request.Claim;
        var fingerprint = BuildClaimFingerprint(claim, request, vaults.Resolve(claim.VaultSource));
        var current = await ReadWriteClaimReplayAsync(claim.OperationId, fingerprint, true, cancellationToken)
            .ConfigureAwait(false) ?? throw ModelProviderCredentialException.Conflict();
        if (current.Status == ModelProviderCredentialWriteClaimStatus.Claimed)
        {
            _ = await CompletePublicationWriteAsync(request, receipt.CredentialReference, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (current.Status == ModelProviderCredentialWriteClaimStatus.Quarantined)
        {
            _ = await RecoverPublicationWriteAsync(request, receipt.CredentialReference, cancellationToken)
                .ConfigureAwait(false);
        }
        return await AcknowledgePublicationAsync(request, receipt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelProviderCredentialReference> RecordReferenceCoreAsync(
        RecordModelProviderCredentialReference request,
        string? claimFingerprint,
        CancellationToken cancellationToken,
        bool publication = false,
        bool recoverFromQuarantine = false)
    {
        ValidateRequest(request);
        var vaultUri = vaults.Resolve(request.VaultSource);
        var secretName = request.Reference.Id;
        ValidateReference(request.Reference);
        ValidateByokName(request.Scope, request.ProviderId, request.VaultSource, secretName);
        var fingerprint = BuildRecordFingerprint(request, vaultUri);

        if (!publication && await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                .AnyAsync(row => row.OperationId == request.OperationId && row.ExpectedProviderRevision != null, cancellationToken)
                .ConfigureAwait(false))
            throw ModelProviderCredentialException.Conflict();
        var replay = await ReadReplayAsync(request.OperationId, fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
        {
            if (!publication && await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                    .AnyAsync(row => row.OperationId == request.OperationId && row.ExpectedProviderRevision != null, cancellationToken)
                    .ConfigureAwait(false))
                throw ModelProviderCredentialException.Conflict();
            return replay;
        }
        if (claimFingerprint is null &&
            await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                .AnyAsync(row => row.OperationId == request.OperationId ||
                    ((row.ProviderId == request.ProviderId ||
                      row.CredentialSlotId == request.CredentialSlotId) &&
                     (row.State == ModelProviderCredentialWriteClaimState.Claimed ||
                      row.State == ModelProviderCredentialWriteClaimState.Quarantined ||
                      row.State == ModelProviderCredentialWriteClaimState.PublicationPending)), cancellationToken)
                .ConfigureAwait(false))
            throw ModelProviderCredentialException.Conflict();

        var now = timeProvider.GetUtcNow();
        var nextRevision = request.ExpectedRevision + 1;
        var revision = new ModelProviderCredentialRevision
        {
            CredentialSlotId = request.CredentialSlotId,
            Revision = nextRevision,
            Scope = request.Scope,
            ProjectId = request.ProjectId,
            ProviderId = request.ProviderId,
            VaultSource = request.VaultSource,
            VaultUri = vaultUri,
            SecretName = secretName,
            SecretVersion = request.Reference.Version,
            State = ModelProviderCredentialState.Enabled,
            MutationKind = ModelProviderCredentialMutationKind.RecordReference,
            OperationId = request.OperationId,
            RequestFingerprint = fingerprint,
            ActorId = request.ActorId,
            RecordedAt = now,
        };

        try
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            await LockCredentialWriteAsync(request.ProviderId, request.CredentialSlotId, request.OperationId, cancellationToken).ConfigureAwait(false);
            if (await db.ModelProviderCredentialRevisions.AsNoTracking()
                    .AnyAsync(row => row.OperationId == request.OperationId, cancellationToken)
                    .ConfigureAwait(false))
                throw ModelProviderCredentialException.Conflict();
            if (claimFingerprint is null &&
                await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                    .AnyAsync(row => row.OperationId == request.OperationId ||
                        ((row.ProviderId == request.ProviderId ||
                          row.CredentialSlotId == request.CredentialSlotId) &&
                         (row.State == ModelProviderCredentialWriteClaimState.Claimed ||
                          row.State == ModelProviderCredentialWriteClaimState.Quarantined ||
                          row.State == ModelProviderCredentialWriteClaimState.PublicationPending)), cancellationToken)
                    .ConfigureAwait(false))
                throw ModelProviderCredentialException.Conflict();
            var head = await db.ModelProviderCredentialHeads.AsNoTracking()
                .SingleOrDefaultAsync(row => row.CredentialSlotId == request.CredentialSlotId, cancellationToken)
                .ConfigureAwait(false);
            if (head is null)
            {
                if (request.ExpectedRevision != 0)
                    throw ModelProviderCredentialException.Conflict();
                db.ModelProviderCredentialHeads.Add(new ModelProviderCredentialHead
                {
                    CredentialSlotId = request.CredentialSlotId,
                    Scope = request.Scope,
                    ProjectId = request.ProjectId,
                    ProviderId = request.ProviderId,
                    VaultSource = request.VaultSource,
                    VaultUri = vaultUri,
                    SecretName = secretName,
                    CurrentVersion = request.Reference.Version,
                    State = ModelProviderCredentialState.Enabled,
                    Revision = nextRevision,
                    SetBy = request.ActorId,
                    SetAt = now,
                });
            }
            else
            {
                RequireIdentity(head, request.CredentialSlotId, request.Scope, request.ProjectId, request.ProviderId);
                vaults.RequireConfigured(head.VaultSource, head.VaultUri);
                if (head.VaultSource != request.VaultSource ||
                    !StringComparer.Ordinal.Equals(head.VaultUri, vaultUri) ||
                    head.State != ModelProviderCredentialState.Enabled ||
                    head.Revision != request.ExpectedRevision)
                    throw ModelProviderCredentialException.Conflict();

                var updated = await db.ModelProviderCredentialHeads
                    .Where(row => row.CredentialSlotId == request.CredentialSlotId &&
                        row.Revision == request.ExpectedRevision &&
                        row.State == ModelProviderCredentialState.Enabled)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.SecretName, secretName)
                        .SetProperty(row => row.CurrentVersion, request.Reference.Version)
                        .SetProperty(row => row.Revision, nextRevision)
                        .SetProperty(row => row.SetBy, request.ActorId)
                        .SetProperty(row => row.SetAt, now), cancellationToken)
                    .ConfigureAwait(false);
                if (updated != 1)
                    throw ModelProviderCredentialException.Conflict();
            }

            db.ModelProviderCredentialRevisions.Add(revision);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (claimFingerprint is not null)
            {
                var expectedState = recoverFromQuarantine
                    ? ModelProviderCredentialWriteClaimState.Quarantined
                    : ModelProviderCredentialWriteClaimState.Claimed;
                var completedClaim = await db.ModelProviderCredentialWriteClaims
                    .Where(row => row.OperationId == request.OperationId &&
                        row.RequestFingerprint == claimFingerprint &&
                        row.State == expectedState &&
                        (publication ? row.ExpectedProviderRevision != null : row.ExpectedProviderRevision == null))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.State, publication
                            ? ModelProviderCredentialWriteClaimState.PublicationPending
                            : ModelProviderCredentialWriteClaimState.Completed)
                        .SetProperty(row => row.QuarantinedAt, (DateTimeOffset?)null)
                        .SetProperty(row => row.RecoveredAt, recoverFromQuarantine ? now : (DateTimeOffset?)null)
                        .SetProperty(row => row.CompletedRevision, nextRevision)
                        .SetProperty(row => row.CompletedAt, now), cancellationToken)
                    .ConfigureAwait(false);
                if (completedClaim != 1)
                    throw ModelProviderCredentialException.Conflict();
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToReference(revision);
        }
        catch (Exception exception)
        {
            DetachCredentialEntities();
            if (!IsConflict(exception))
                throw;
            if (publication)
                throw ModelProviderCredentialException.Conflict();
            if (await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                    .AnyAsync(row => row.OperationId == request.OperationId && row.ExpectedProviderRevision != null, cancellationToken)
                    .ConfigureAwait(false))
                throw ModelProviderCredentialException.Conflict();
            var concurrentReplay = await ReadReplayAsync(request.OperationId, fingerprint, cancellationToken)
                .ConfigureAwait(false);
            if (concurrentReplay is not null && await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                    .AnyAsync(row => row.OperationId == request.OperationId && row.ExpectedProviderRevision != null, cancellationToken)
                    .ConfigureAwait(false))
                throw ModelProviderCredentialException.Conflict();
            return concurrentReplay ?? throw ModelProviderCredentialException.Conflict();
        }
    }

    internal async Task<ModelProviderCredentialCurrentRead> ReadCurrentAsync(
        ModelProviderCredentialIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        var head = await db.ModelProviderCredentialHeads.AsNoTracking()
            .SingleOrDefaultAsync(row => row.CredentialSlotId == identity.CredentialSlotId, cancellationToken)
            .ConfigureAwait(false);
        if (head is null ||
            head.Scope != identity.Scope ||
            head.ProjectId != identity.ProjectId ||
            !StringComparer.Ordinal.Equals(head.ProviderId, identity.ProviderId))
            return new(ModelProviderCredentialCurrentStatus.NotFound, null);

        vaults.RequireConfigured(head.VaultSource, head.VaultUri);
        if (head.State == ModelProviderCredentialState.Disabled)
            return new(ModelProviderCredentialCurrentStatus.Disabled, null);

        return new(ModelProviderCredentialCurrentStatus.Found, ToReference(head));
    }

    internal async Task<ModelProviderCredentialPinnedRead> ReadPinnedAsync(
        ModelProviderCredentialIdentity identity,
        SecretRef reference,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        ValidateReference(reference);
        var revision = await db.ModelProviderCredentialRevisions.AsNoTracking()
            .Where(row =>
                row.CredentialSlotId == identity.CredentialSlotId &&
                row.Scope == identity.Scope &&
                row.ProjectId == identity.ProjectId &&
                row.ProviderId == identity.ProviderId &&
                row.SecretName == reference.Id &&
                row.SecretVersion == reference.Version &&
                row.State == ModelProviderCredentialState.Enabled)
            .OrderByDescending(row => row.Revision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (revision is not null)
        {
            vaults.RequireConfigured(revision.VaultSource, revision.VaultUri);
            return new(ModelProviderCredentialPinnedStatus.Found, ToReference(revision));
        }

        var pending = await db.ModelProviderCredentialWriteClaims.AsNoTracking()
            .SingleOrDefaultAsync(row =>
                row.CredentialSlotId == identity.CredentialSlotId &&
                row.Scope == identity.Scope &&
                row.ProjectId == identity.ProjectId &&
                row.ProviderId == identity.ProviderId &&
                row.SecretName == reference.Id &&
                row.PendingSecretVersion == reference.Version &&
                (row.State == ModelProviderCredentialWriteClaimState.PublicationPending ||
                 row.State == ModelProviderCredentialWriteClaimState.Published), cancellationToken)
            .ConfigureAwait(false);
        if (pending is null)
            return new(ModelProviderCredentialPinnedStatus.NotFound, null);

        vaults.RequireConfigured(pending.VaultSource, pending.VaultUri);
        return new(
            ModelProviderCredentialPinnedStatus.Found,
            new ModelProviderCredentialReference(
                new ModelProviderCredentialIdentity(
                    pending.CredentialSlotId, pending.Scope, pending.ProjectId, pending.ProviderId),
                pending.VaultSource,
                pending.VaultUri,
                new SecretRef(pending.SecretName, pending.PendingSecretVersion!),
                ModelProviderCredentialState.Enabled,
                pending.CompletedRevision ?? (pending.ExpectedRevision + 1),
                pending.ActorId,
                pending.CompletedAt ?? pending.ClaimedAt));
    }

    internal Task<ModelProviderCredentialPinnedRead> ReadPinnedAsync(
        ModelCredentialIdentity identity,
        SecretRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var scope = identity.Scope switch
        {
            ModelProviderBindingScope.Platform => ModelProviderCredentialScope.Platform,
            ModelProviderBindingScope.Project => ModelProviderCredentialScope.Project,
            _ => throw ModelProviderCredentialException.InvalidRequest(),
        };
        return ReadPinnedAsync(new ModelProviderCredentialIdentity(
            identity.CredentialSlotId, scope, identity.ProjectId, identity.ProviderId),
            reference, cancellationToken);
    }

    internal async Task<ModelProviderCredentialReference> DisableAsync(
        DisableModelProviderCredential request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var fingerprint = Fingerprint(new DisableFingerprint(
            ModelProviderCredentialMutationKind.Disable,
            request.OperationId,
            request.ExpectedRevision,
            request.CredentialSlotId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.ActorId));
        var replay = await ReadReplayAsync(request.OperationId, fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
            return replay;

        try
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);
            await LockCredentialWriteAsync(request.ProviderId, request.CredentialSlotId, request.OperationId, cancellationToken).ConfigureAwait(false);
            if (await db.ModelProviderCredentialRevisions.AsNoTracking()
                    .AnyAsync(row => row.OperationId == request.OperationId, cancellationToken)
                    .ConfigureAwait(false) ||
                await db.ModelProviderCredentialWriteClaims.AsNoTracking()
                    .AnyAsync(row => row.OperationId == request.OperationId ||
                        ((row.ProviderId == request.ProviderId || row.CredentialSlotId == request.CredentialSlotId) &&
                         (row.State == ModelProviderCredentialWriteClaimState.Claimed ||
                          row.State == ModelProviderCredentialWriteClaimState.Quarantined ||
                          row.State == ModelProviderCredentialWriteClaimState.PublicationPending)), cancellationToken)
                    .ConfigureAwait(false))
                throw ModelProviderCredentialException.Conflict();
            var head = await db.ModelProviderCredentialHeads.AsNoTracking()
                .SingleOrDefaultAsync(row => row.CredentialSlotId == request.CredentialSlotId, cancellationToken)
                .ConfigureAwait(false);
            if (head is null)
                throw ModelProviderCredentialException.Conflict();
            RequireIdentity(head, request.CredentialSlotId, request.Scope, request.ProjectId, request.ProviderId);
            vaults.RequireConfigured(head.VaultSource, head.VaultUri);
            if (head.State != ModelProviderCredentialState.Enabled ||
                head.Revision != request.ExpectedRevision ||
                head.Revision == long.MaxValue)
                throw ModelProviderCredentialException.Conflict();

            var nextRevision = head.Revision + 1;
            var now = timeProvider.GetUtcNow();
            var updated = await db.ModelProviderCredentialHeads
                .Where(row => row.CredentialSlotId == request.CredentialSlotId &&
                    row.Revision == request.ExpectedRevision &&
                    row.State == ModelProviderCredentialState.Enabled)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.State, ModelProviderCredentialState.Disabled)
                    .SetProperty(row => row.Revision, nextRevision)
                    .SetProperty(row => row.SetBy, request.ActorId)
                    .SetProperty(row => row.SetAt, now), cancellationToken)
                .ConfigureAwait(false);
            if (updated != 1)
                throw ModelProviderCredentialException.Conflict();

            var revision = new ModelProviderCredentialRevision
            {
                CredentialSlotId = head.CredentialSlotId,
                Revision = nextRevision,
                Scope = head.Scope,
                ProjectId = head.ProjectId,
                ProviderId = head.ProviderId,
                VaultSource = head.VaultSource,
                VaultUri = head.VaultUri,
                SecretName = head.SecretName,
                SecretVersion = head.CurrentVersion,
                State = ModelProviderCredentialState.Disabled,
                MutationKind = ModelProviderCredentialMutationKind.Disable,
                OperationId = request.OperationId,
                RequestFingerprint = fingerprint,
                ActorId = request.ActorId,
                RecordedAt = now,
            };
            db.ModelProviderCredentialRevisions.Add(revision);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToReference(revision);
        }
        catch (Exception exception)
        {
            DetachCredentialEntities();
            if (!IsConflict(exception))
                throw;
            var concurrentReplay = await ReadReplayAsync(request.OperationId, fingerprint, cancellationToken)
                .ConfigureAwait(false);
            return concurrentReplay ?? throw ModelProviderCredentialException.Conflict();
        }
    }

    private async Task LockCredentialWriteAsync(
        string providerId,
        Guid credentialSlotId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using (var providerLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@providerId, 0))", connection, transaction))
        {
            providerLock.Parameters.AddWithValue("providerId", providerId);
            _ = await providerLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var slotLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('slot:' || @slotId, 1))", connection, transaction);
        slotLock.Parameters.AddWithValue("slotId", credentialSlotId.ToString("N"));
        _ = await slotLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var operationLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended('operation:' || @operationId, 2))", connection, transaction);
        operationLock.Parameters.AddWithValue("operationId", operationId.ToString("N"));
        _ = await operationLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelProviderCredentialWriteClaimResult?> ReadWriteClaimReplayAsync(
        Guid operationId,
        string fingerprint,
        bool publication,
        CancellationToken cancellationToken)
    {
        var claim = await db.ModelProviderCredentialWriteClaims.AsNoTracking()
            .SingleOrDefaultAsync(row => row.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (claim is null)
            return null;
        if ((claim.ExpectedProviderRevision is not null) != publication ||
            !StringComparer.Ordinal.Equals(claim.RequestFingerprint, fingerprint))
            throw ModelProviderCredentialException.Conflict();
        if (claim.State == ModelProviderCredentialWriteClaimState.Cancelled)
            return new(ModelProviderCredentialWriteClaimStatus.Cancelled, false, null);
        if (claim.State is ModelProviderCredentialWriteClaimState.Claimed or ModelProviderCredentialWriteClaimState.Quarantined)
            return new(publication && claim.State == ModelProviderCredentialWriteClaimState.Claimed
                ? ModelProviderCredentialWriteClaimStatus.Claimed
                : ModelProviderCredentialWriteClaimStatus.Quarantined, false, null);
        if (publication
                ? claim.State is not (ModelProviderCredentialWriteClaimState.PublicationPending or ModelProviderCredentialWriteClaimState.Published)
                : claim.State != ModelProviderCredentialWriteClaimState.Completed)
            throw ModelProviderCredentialException.Conflict();

        if (publication && claim.State == ModelProviderCredentialWriteClaimState.PublicationPending)
        {
            if (string.IsNullOrEmpty(claim.PendingSecretVersion))
                throw ModelProviderCredentialException.Conflict();
            vaults.RequireConfigured(claim.VaultSource, claim.VaultUri);
            return new(
                ModelProviderCredentialWriteClaimStatus.PublicationPending,
                false,
                new ModelProviderCredentialReference(
                    new ModelProviderCredentialIdentity(
                        claim.CredentialSlotId, claim.Scope, claim.ProjectId, claim.ProviderId),
                    claim.VaultSource,
                    claim.VaultUri,
                    new SecretRef(claim.SecretName, claim.PendingSecretVersion),
                    ModelProviderCredentialState.Enabled,
                    claim.ExpectedRevision + 1,
                    claim.ActorId,
                    claim.ClaimedAt));
        }

        var revision = await db.ModelProviderCredentialRevisions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (revision is null || revision.Revision != claim.CompletedRevision ||
            revision.CredentialSlotId != claim.CredentialSlotId ||
            revision.Scope != claim.Scope || revision.ProjectId != claim.ProjectId ||
            !StringComparer.Ordinal.Equals(revision.ProviderId, claim.ProviderId) ||
            revision.VaultSource != claim.VaultSource ||
            !StringComparer.Ordinal.Equals(revision.VaultUri, claim.VaultUri) ||
            !StringComparer.Ordinal.Equals(revision.SecretName, claim.SecretName) ||
            !StringComparer.Ordinal.Equals(revision.ActorId, claim.ActorId) ||
            revision.Revision != claim.ExpectedRevision + 1 ||
            revision.MutationKind != ModelProviderCredentialMutationKind.RecordReference ||
            revision.State != ModelProviderCredentialState.Enabled)
            throw ModelProviderCredentialException.Conflict();

        vaults.RequireConfigured(revision.VaultSource, revision.VaultUri);
        return new(claim.State switch
        {
            ModelProviderCredentialWriteClaimState.Completed => ModelProviderCredentialWriteClaimStatus.Completed,
            ModelProviderCredentialWriteClaimState.PublicationPending => ModelProviderCredentialWriteClaimStatus.PublicationPending,
            ModelProviderCredentialWriteClaimState.Published => ModelProviderCredentialWriteClaimStatus.Published,
            _ => throw ModelProviderCredentialException.Conflict(),
        }, false, ToReference(revision));
    }

    private async Task<ModelProviderCredentialPublicationClaimResult> ToPublicationResultAsync(
        Guid operationId,
        ModelProviderCredentialWriteClaimResult result,
        CancellationToken cancellationToken)
    {
        var claim = await db.ModelProviderCredentialWriteClaims.AsNoTracking()
            .SingleAsync(row => row.OperationId == operationId, cancellationToken).ConfigureAwait(false);
        if (!result.Acquired && claim.State == ModelProviderCredentialWriteClaimState.Published &&
            result.Status != ModelProviderCredentialWriteClaimStatus.Published)
            result = await ReadWriteClaimReplayAsync(operationId, claim.RequestFingerprint, true, cancellationToken)
                .ConfigureAwait(false) ?? throw ModelProviderCredentialException.Conflict();
        return new(result.Status, result.Acquired, result.Credential,
            claim.PublishedProviderRevision, claim.PublishedConfigurationHash, claim.PublishedAt);
    }

    private static void ValidatePublicationClaim(ClaimModelProviderCredentialPublication request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateClaimRequest(request.Claim);
        ValidateSecretName(request.Claim.SecretName);
        ValidateByokName(request.Claim.Scope, request.Claim.ProviderId, request.Claim.VaultSource, request.Claim.SecretName);
        if (request.ExpectedProviderRevision is <= 0 or long.MaxValue ||
            request.Purpose != "credential-publication" ||
            request.Issuer is not { Length: > 0 and <= 2048 } ||
            request.Issuer.Any(c => c is < '!' or > '~') ||
            !Uri.TryCreate(request.Issuer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps || issuer.UserInfo.Length != 0 || issuer.Fragment.Length != 0 ||
            !StringComparer.Ordinal.Equals(request.Issuer, issuer.AbsoluteUri))
            throw ModelProviderCredentialException.InvalidRequest();
        ValidateOpaque(request.Tenant, 256);
    }

    private static string BuildClaimFingerprint(
        ClaimModelProviderCredentialWrite request,
        ClaimModelProviderCredentialPublication? publication,
        string vaultUri)
    {
        if (publication is null)
            return BuildWriteClaimFingerprint(request, vaultUri);
        var fields = new string?[]
        {
            "credential-write-claim-v2", request.OperationId.ToString("D"),
            request.ExpectedRevision.ToString(CultureInfo.InvariantCulture), request.CredentialSlotId.ToString("D"),
            request.Scope.ToString(), request.ProjectId, request.ProviderId, request.VaultSource.ToString(),
            vaultUri, request.SecretName, request.ActorId,
            publication.ExpectedProviderRevision.ToString(CultureInfo.InvariantCulture),
            publication.Purpose, publication.Issuer, publication.Tenant,
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in fields)
        {
            var bytes = field is null ? null : Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1);
            hash.AppendData(length);
            if (bytes is not null)
                hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string BuildRecordFingerprint(
        RecordModelProviderCredentialReference request,
        string vaultUri) =>
        Fingerprint(new RecordFingerprint(
            ModelProviderCredentialMutationKind.RecordReference,
            request.OperationId,
            request.ExpectedRevision,
            request.CredentialSlotId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.VaultSource,
            vaultUri,
            request.Reference.Id,
            request.Reference.Version,
            request.ActorId));

    private static ClaimModelProviderCredentialWrite ToWriteClaimRequest(
        RecordModelProviderCredentialReference request) =>
        new(request.OperationId, request.ExpectedRevision, request.CredentialSlotId,
            request.Scope, request.ProjectId, request.ProviderId, request.VaultSource,
            request.Reference.Id, request.ActorId);

    private static string BuildWriteClaimFingerprint(
        ClaimModelProviderCredentialWrite request,
        string vaultUri) =>
        Fingerprint(new WriteClaimFingerprint(
            request.OperationId,
            request.ExpectedRevision,
            request.CredentialSlotId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.VaultSource,
            vaultUri,
            request.SecretName,
            request.ActorId));

    private async Task<ModelProviderCredentialReference?> ReadReplayAsync(
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var revision = await db.ModelProviderCredentialRevisions.AsNoTracking()
            .SingleOrDefaultAsync(row => row.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);
        if (revision is null)
            return null;
        if (!StringComparer.Ordinal.Equals(revision.RequestFingerprint, fingerprint))
            throw ModelProviderCredentialException.Conflict();
        vaults.RequireConfigured(revision.VaultSource, revision.VaultUri);
        return ToReference(revision);
    }

    private static ModelProviderCredentialReference ToReference(ModelProviderCredentialHead head) =>
        new(
            new(head.CredentialSlotId, head.Scope, head.ProjectId, head.ProviderId),
            head.VaultSource,
            head.VaultUri,
            new SecretRef(head.SecretName, head.CurrentVersion),
            head.State,
            head.Revision,
            head.SetBy,
            head.SetAt);

    private static ModelProviderCredentialReference ToReference(ModelProviderCredentialRevision revision) =>
        new(
            new(revision.CredentialSlotId, revision.Scope, revision.ProjectId, revision.ProviderId),
            revision.VaultSource,
            revision.VaultUri,
            new SecretRef(revision.SecretName, revision.SecretVersion),
            revision.State,
            revision.Revision,
            revision.ActorId,
            revision.RecordedAt);

    private static void RequireIdentity(
        ModelProviderCredentialHead head,
        Guid slotId,
        ModelProviderCredentialScope scope,
        string? projectId,
        string providerId)
    {
        if (head.CredentialSlotId != slotId || head.Scope != scope ||
            head.ProjectId != projectId || !StringComparer.Ordinal.Equals(head.ProviderId, providerId))
            throw ModelProviderCredentialException.Conflict();
    }

    private static void ValidateRequest(RecordModelProviderCredentialReference request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateMutationIdentity(request.OperationId, request.ExpectedRevision, request.CredentialSlotId,
            request.Scope, request.ProjectId, request.ProviderId, request.ActorId);
        if (!Enum.IsDefined(request.VaultSource))
            throw ModelProviderCredentialException.InvalidRequest();
    }

    private static void ValidateClaimRequest(ClaimModelProviderCredentialWrite request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateMutationIdentity(request.OperationId, request.ExpectedRevision, request.CredentialSlotId,
            request.Scope, request.ProjectId, request.ProviderId, request.ActorId);
        if (!Enum.IsDefined(request.VaultSource))
            throw ModelProviderCredentialException.InvalidRequest();
    }

    private static void ValidateRequest(DisableModelProviderCredential request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateMutationIdentity(request.OperationId, request.ExpectedRevision, request.CredentialSlotId,
            request.Scope, request.ProjectId, request.ProviderId, request.ActorId);
    }

    private static void ValidateMutationIdentity(
        Guid operationId,
        long expectedRevision,
        Guid slotId,
        ModelProviderCredentialScope scope,
        string? projectId,
        string providerId,
        string actorId)
    {
        if (operationId == Guid.Empty || expectedRevision < 0 || expectedRevision == long.MaxValue ||
            slotId == Guid.Empty)
            throw ModelProviderCredentialException.InvalidRequest();
        ValidateIdentity(new(slotId, scope, projectId, providerId));
        ValidateOpaque(actorId, 256);
    }

    internal static void ValidateIdentity(ModelProviderCredentialIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.CredentialSlotId == Guid.Empty ||
            !Enum.IsDefined(identity.Scope) ||
            identity.Scope == ModelProviderCredentialScope.Platform && identity.ProjectId is not null ||
            identity.Scope == ModelProviderCredentialScope.Project &&
                (string.IsNullOrEmpty(identity.ProjectId) || identity.ProjectId.Length > 32) ||
            string.IsNullOrEmpty(identity.ProviderId) || identity.ProviderId.Length > 128)
            throw ModelProviderCredentialException.InvalidRequest();
        ValidateOpaque(identity.ProviderId, 128);
        if (identity.ProjectId is not null)
            ValidateOpaque(identity.ProjectId, 32);
    }

    private static void ValidateSecretName(string secretName)
    {
        ArgumentNullException.ThrowIfNull(secretName);
        if (secretName.Length is < 1 or > 127 ||
            secretName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw ModelProviderCredentialException.InvalidRequest();
    }

    internal static void ValidateReference(SecretRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Id.Length is < 1 or > 127 ||
            reference.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
            reference.Version.Length != 32 ||
            reference.Version.Any(character => !Uri.IsHexDigit(character)))
            throw ModelProviderCredentialException.InvalidRequest();
    }

    internal static void ValidateByokName(
        ModelProviderCredentialScope scope,
        string providerId,
        ModelProviderCredentialVaultSource source,
        string secretName)
    {
        if (source != ModelProviderCredentialVaultSource.Byok)
            return;
        var prefix = $"byok-{scope.ToString().ToLowerInvariant()}-";
        if (scope is not (ModelProviderCredentialScope.Platform or ModelProviderCredentialScope.Project) ||
            secretName.Length is < 1 or > 127 ||
            !StringComparer.Ordinal.Equals(secretName, prefix + providerId) ||
            secretName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw ModelProviderCredentialException.InvalidRequest();
    }

    private static void ValidateOpaque(string value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximumLength ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw ModelProviderCredentialException.InvalidRequest();
    }

    private static string Fingerprint<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, FingerprintJson);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool IsConflict(Exception exception) =>
        exception is ModelProviderCredentialException { Code: "credential_slot_conflict" } ||
        exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } };

    private void DetachCredentialEntities()
    {
        foreach (var entry in db.ChangeTracker.Entries()
                     .Where(entry => entry.Entity is ModelProviderCredentialHead or
                         ModelProviderCredentialRevision or ModelProviderCredentialWriteClaim))
            entry.State = EntityState.Detached;
    }

    private sealed record RecordFingerprint(
        ModelProviderCredentialMutationKind Mutation,
        Guid OperationId,
        long ExpectedRevision,
        Guid CredentialSlotId,
        ModelProviderCredentialScope Scope,
        string? ProjectId,
        string ProviderId,
        ModelProviderCredentialVaultSource VaultSource,
        string VaultUri,
        string SecretName,
        string SecretVersion,
        string ActorId);

    private sealed record WriteClaimFingerprint(
        Guid OperationId,
        long ExpectedRevision,
        Guid CredentialSlotId,
        ModelProviderCredentialScope Scope,
        string? ProjectId,
        string ProviderId,
        ModelProviderCredentialVaultSource VaultSource,
        string VaultUri,
        string SecretName,
        string ActorId);

    private sealed record DisableFingerprint(
        ModelProviderCredentialMutationKind Mutation,
        Guid OperationId,
        long ExpectedRevision,
        Guid CredentialSlotId,
        ModelProviderCredentialScope Scope,
        string? ProjectId,
        string ProviderId,
        string ActorId);
}
