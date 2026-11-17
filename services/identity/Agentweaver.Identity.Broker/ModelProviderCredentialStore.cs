using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Secrets.AzureKeyVault;
using Microsoft.EntityFrameworkCore;
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

    internal async Task<ModelProviderCredentialReference> RecordReferenceAsync(
        RecordModelProviderCredentialReference request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var vaultUri = vaults.Resolve(request.VaultSource);
        var secretName = request.Reference.Id;
        ValidateReference(request.Reference);
        ValidateByokName(request.Scope, request.ProviderId, request.VaultSource, secretName);
        var fingerprint = Fingerprint(new RecordFingerprint(
            ModelProviderCredentialMutationKind.RecordReference,
            request.OperationId,
            request.ExpectedRevision,
            request.CredentialSlotId,
            request.Scope,
            request.ProjectId,
            request.ProviderId,
            request.VaultSource,
            vaultUri,
            secretName,
            request.Reference.Version,
            request.ActorId));

        var replay = await ReadReplayAsync(request.OperationId, fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (replay is not null)
            return replay;

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
        if (revision is null)
            return new(ModelProviderCredentialPinnedStatus.NotFound, null);

        vaults.RequireConfigured(revision.VaultSource, revision.VaultUri);
        return new(ModelProviderCredentialPinnedStatus.Found, ToReference(revision));
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
                     .Where(entry => entry.Entity is ModelProviderCredentialHead or ModelProviderCredentialRevision))
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
