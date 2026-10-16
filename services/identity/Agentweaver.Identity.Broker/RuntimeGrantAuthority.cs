using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Identity.Broker;

public sealed class RuntimeGrantAuthority(
    IdentityBrokerDbContext db,
    IRuntimeRegistrationOwner owner,
    IRuntimeBootstrapDelivery delivery,
    RuntimeCredentialPolicy policy,
    RuntimeActorAuthorization actor,
    TimeProvider timeProvider,
    CopilotConnectionAuthority? connections = null) : IRuntimePendingBootstrapVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public async Task<RuntimeModelCredentialGrantReceipt> IssueModelCredentialGrantAsync(
        Guid runtimeInstanceId, Guid operationId, CancellationToken cancellationToken = default)
    {
        ValidateOperation(operationId);
        var registration = await RequireCurrentAsync(runtimeInstanceId, cancellationToken);
        var binding = registration.Binding;
        var modelReference = binding.ModelSelectionReference;
        var credentialReference = binding.ModelCredentialReference;
        CopilotConnectionRecord? connection = null;
        if (binding.ModelSourceMode == ModelSourceMode.HostedCopilot)
        {
            connection = await (connections ??
                throw Denied("runtime_copilot_connection_writer_unavailable"))
                .RequireCurrentAsync(binding, cancellationToken);
            credentialReference = new SecretRef(connection.SecretId!, connection.SecretVersion!);
        }
        else if (binding.ModelSourceMode != ModelSourceMode.Byok || binding.ModelConnectionId is not null)
        {
            throw Denied("runtime_model_source_mode_unavailable");
        }
        if (string.IsNullOrWhiteSpace(modelReference) || credentialReference is null)
            throw Denied("runtime_model_credential_unavailable");
        var expiresAt = connection is null || registration.ExpiresAt <= connection.FreshUntil
            ? registration.ExpiresAt : connection.FreshUntil;
        if (actor.Bearer.ExpiresAt < expiresAt)
            expiresAt = actor.Bearer.ExpiresAt;
        expiresAt = new DateTimeOffset(expiresAt.UtcTicks - expiresAt.UtcTicks % 10, TimeSpan.Zero);

        var grantId = $"runtime-model-session:{runtimeInstanceId:N}";
        var idempotencyKey = $"runtime-model-session:{operationId:N}";
        var priorOperation = await db.SecretGrantOperations.AsNoTracking().SingleOrDefaultAsync(
            row => row.IdempotencyKey == idempotencyKey, cancellationToken);
        long expectedRevision;
        if (priorOperation is not null)
        {
            if (priorOperation.GrantId != grantId || priorOperation.Revision <= 0)
                throw Denied("runtime_model_grant_operation_conflict");
            expectedRevision = priorOperation.Revision - 1;
        }
        else
        {
            var current = await (
                from head in db.SecretGrantHeads.AsNoTracking()
                join revision in db.SecretGrantRevisions.AsNoTracking()
                    on new { head.GrantId, Revision = head.CurrentRevision }
                    equals new { revision.GrantId, revision.Revision }
                where head.GrantId == grantId
                select new
                {
                    head.CurrentRevision,
                    revision.ActorId,
                    revision.ProjectId,
                    revision.RunId,
                    revision.Purpose
                }).SingleOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                expectedRevision = 0;
            }
            else
            {
                if (current.CurrentRevision <= 0 ||
                    current.ActorId != binding.ActorId ||
                    current.ProjectId != binding.ProjectId ||
                    current.RunId != binding.RunId ||
                    current.Purpose != RuntimeSecretPurposes.ModelSession)
                    throw Denied("runtime_model_grant_binding_invalid");
                expectedRevision = current.CurrentRevision;
            }
        }

        var grant = new SecretRedemptionGrant(
            grantId, binding.ActorId, binding.ProjectId, binding.RunId,
            RuntimeSecretPurposes.ModelSession, credentialReference,
            GrantState.Active, expiresAt, timeProvider);
        GrantMutationResult mutation;
        try
        {
            mutation = await new IdentityGrantAuthority(db, timeProvider).ReplaceAsync(
                grant, expectedRevision, idempotencyKey, cancellationToken);
        }
        catch (GrantConcurrencyException)
        {
            throw Denied("runtime_model_grant_stale");
        }
        catch (GrantIdempotencyConflictException)
        {
            throw Denied("runtime_model_grant_operation_conflict");
        }

        await RequireSameCurrentAsync(registration, cancellationToken);
        if (connection is not null)
        {
            var currentConnection = await connections!.RequireCurrentAsync(binding, cancellationToken);
            RequireSameConnection(connection, currentConnection);
        }
        return new RuntimeModelCredentialGrantReceipt(
            grantId, mutation.Revision, registration.RuntimeInstanceId, registration.Revision,
            modelReference, credentialReference, RuntimeSecretPurposes.ModelSession, grant.ExpiresAt)
        {
            SourceMode = binding.ModelSourceMode,
            ConnectionId = connection?.ConnectionId,
            ConnectionRevision = connection?.Revision,
            CredentialKind = connection is null
                ? RuntimeModelCredentialKind.ByokKey : RuntimeModelCredentialKind.GitHubUserAccess
        };
    }

    public async Task<RuntimeModelCredentialGrantReceipt> VerifyModelCredentialAsync(
        RuntimeModelCredentialGrantReceipt receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var registration = await RequireCurrentAsync(receipt.RuntimeInstanceId, cancellationToken);
        var binding = registration.Binding;
        if (receipt.RegistrationRevision != registration.Revision ||
            receipt.ModelSelectionReference != binding.ModelSelectionReference ||
            receipt.SourceMode != binding.ModelSourceMode || receipt.Purpose != RuntimeSecretPurposes.ModelSession ||
            receipt.GrantId != $"runtime-model-session:{receipt.RuntimeInstanceId:N}" ||
            receipt.ExpiresAt <= Now() || receipt.ExpiresAt > registration.ExpiresAt ||
            receipt.CredentialReference is null)
            throw Denied("runtime_model_grant_receipt_invalid");
        if (binding.ModelSourceMode == ModelSourceMode.HostedCopilot)
        {
            var connection = await (connections ??
                throw Denied("runtime_copilot_connection_writer_unavailable"))
                .RequireCurrentAsync(binding, cancellationToken);
            if (receipt.ConnectionId != connection.ConnectionId ||
                receipt.ConnectionRevision != connection.Revision ||
                receipt.CredentialKind != RuntimeModelCredentialKind.GitHubUserAccess ||
                receipt.CredentialReference != new SecretRef(connection.SecretId!, connection.SecretVersion!) ||
                receipt.ExpiresAt > connection.FreshUntil)
                throw Denied("runtime_copilot_connection_stale");
        }
        else if (binding.ModelSourceMode != ModelSourceMode.Byok ||
            receipt.ConnectionId is not null || receipt.ConnectionRevision is not null ||
            receipt.CredentialKind != RuntimeModelCredentialKind.ByokKey ||
            receipt.CredentialReference != binding.ModelCredentialReference)
        {
            throw Denied("runtime_model_grant_receipt_invalid");
        }
        var grant = await (
            from head in db.SecretGrantHeads.AsNoTracking()
            join revision in db.SecretGrantRevisions.AsNoTracking()
                on new { head.GrantId, Revision = head.CurrentRevision }
                equals new { revision.GrantId, revision.Revision }
            where head.GrantId == receipt.GrantId
            select revision).SingleOrDefaultAsync(cancellationToken);
        if (grant is null || grant.Revision != receipt.Revision || grant.State != GrantState.Active ||
            grant.ActorId != binding.ActorId || grant.ProjectId != binding.ProjectId ||
            grant.RunId != binding.RunId || grant.Purpose != receipt.Purpose ||
            grant.SecretId != receipt.CredentialReference.Id ||
            grant.SecretVersion != receipt.CredentialReference.Version || grant.ExpiresAt != receipt.ExpiresAt)
            throw Denied("runtime_model_grant_stale");
        await RequireSameCurrentAsync(registration, cancellationToken);
        if (receipt.ConnectionId is not null)
        {
            var final = await connections!.RequireCurrentAsync(binding, cancellationToken);
            if (final.ConnectionId != receipt.ConnectionId || final.Revision != receipt.ConnectionRevision)
                throw Denied("runtime_copilot_connection_stale");
        }
        var currentHead = await db.SecretGrantHeads.AsNoTracking()
            .SingleOrDefaultAsync(head => head.GrantId == receipt.GrantId, cancellationToken);
        if (currentHead?.CurrentRevision != receipt.Revision || receipt.ExpiresAt <= Now())
            throw Denied("runtime_model_grant_stale");
        return receipt;
    }

    public async Task<RuntimeModelCredential> RedeemModelCredentialAsync(
        RuntimeModelCredentialGrantReceipt receipt, ISecretRedemption backend,
        CancellationToken cancellationToken)
    {
        await VerifyModelCredentialAsync(receipt, cancellationToken);
        var registration = await RequireCurrentAsync(receipt.RuntimeInstanceId, cancellationToken);
        var binding = registration.Binding;
        var redemption = new AuthorizedSecretRedemption(
            new TrustedActorContext(binding.ActorId, binding.ProjectId, binding.RunId),
            new IdentityGrantAuthority(db, timeProvider), backend, timeProvider);
        var credential = await redemption.RedeemAsync(new SecretRedemptionRequest(
            receipt.CredentialReference, RuntimeSecretPurposes.ModelSession, binding.RunId), cancellationToken);
        try
        {
            await VerifyModelCredentialAsync(receipt, cancellationToken);
            credential.LimitLifetime(receipt.ExpiresAt);
            if (receipt.SourceMode == ModelSourceMode.HostedCopilot)
            {
                using var envelope = JsonDocument.Parse(credential.GetValue());
                var root = envelope.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    root.EnumerateObject().Select(property => property.Name)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() != root.EnumerateObject().Count())
                    throw Denied("runtime_copilot_credential_envelope_invalid");
                var stored = root.Deserialize<CopilotUserCredential>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (stored?.Status != "signed-in" || string.IsNullOrWhiteSpace(stored.AccessToken) ||
                    !stored.AccessToken.StartsWith("ghu_", StringComparison.Ordinal) ||
                    stored.AccessToken.Any(char.IsWhiteSpace) || stored.ExpiresAt <= Now())
                    throw Denied("runtime_copilot_credential_envelope_invalid");
                credential.LimitLifetime(stored.ExpiresAt);
                var access = new SecretCredential(stored.AccessToken, credential.ExpiresAt, timeProvider);
                credential.Invalidate();
                return new(receipt, access);
            }
            return new(receipt, credential);
        }
        catch (JsonException)
        {
            credential.Invalidate();
            throw Denied("runtime_copilot_credential_envelope_invalid");
        }
        catch
        {
            credential.Invalidate();
            throw;
        }
    }

    private static void RequireSameConnection(CopilotConnectionRecord expected, CopilotConnectionRecord current)
    {
        if (expected.ConnectionId != current.ConnectionId || expected.Revision != current.Revision ||
            expected.SecretId != current.SecretId || expected.SecretVersion != current.SecretVersion)
            throw Denied("runtime_copilot_connection_stale");
    }

    public async Task<RuntimeBootstrapDeliveryReceipt> DeliverBootstrapAsync(
        Guid runtimeInstanceId, string configurationHash, Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateOperation(operationId);
        RuntimeContractValidation.ValidateHash(configurationHash);
        var registration = await RequireCurrentAsync(runtimeInstanceId, cancellationToken);
        var requestHash = Hash("deliver", registration, configurationHash);
        var prior = await ReadOperationAsync<RuntimeBootstrapDeliveryReceipt>(
            operationId, requestHash, cancellationToken);
        if (prior is not null)
        {
            await RequireSameCurrentAsync(registration, cancellationToken);
            return prior;
        }

        var grantId = Guid.NewGuid();
        var secret = new SecretCredential(
            OpaqueHandle.NewHandle(), Expiry(registration, policy.BootstrapLifetime), timeProvider);
        try
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                var inserted = await InsertOperationAsync(
                    operationId, grantId, requestHash, cancellationToken);
                if (!inserted)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    var completed = await ReadOperationAsync<RuntimeBootstrapDeliveryReceipt>(
                        operationId, requestHash, cancellationToken)
                        ?? throw Denied("runtime_delivery_pending");
                    await RequireSameCurrentAsync(registration, cancellationToken);
                    return completed;
                }
                AddGrant(grantId, registration, RuntimeCredentialPurpose.Configure,
                    registration.Binding.ConfigureEndpoint, configurationHash, secret, operationId);
                await RequireSameCurrentAsync(registration, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            var receipt = await delivery.DeliverAsync(
                registration, actor, operationId, grantId, configurationHash, secret, cancellationToken);
            if (receipt.OperationId != operationId || receipt.GrantId != grantId ||
                receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
                receipt.RegistrationRevision != registration.Revision ||
                receipt.PlacementUid != registration.Binding.PlacementUid ||
                receipt.PlacementGeneration != registration.Binding.PlacementGeneration ||
                receipt.EnvironmentCurrentFencingGeneration !=
                    registration.Binding.EnvironmentCurrentFencingGeneration ||
                receipt.EnvironmentProviderFencingGeneration !=
                    registration.Binding.EnvironmentProviderFencingGeneration ||
                receipt.ExecutionFence != registration.Binding.ExecutionFence ||
                receipt.ConfigurationHash != configurationHash ||
                receipt.DeliveredAt > Now() || receipt.DeliveredAt >= secret.ExpiresAt)
                throw Denied("runtime_delivery_binding_invalid");

            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                var current = await LockCurrentAsync(grantId, cancellationToken);
                RequireLive(current);
                if (current.State != RuntimeCredentialState.Active)
                    throw Denied("runtime_grant_revoked");
                await RequireSameCurrentAsync(registration, cancellationToken);
                AddReceipt(operationId, receipt);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            await RequireSameCurrentAsync(registration, cancellationToken);
            return receipt;
        }
        catch
        {
            // Delivery may fail after the verifier commit. Never leave that nonce usable.
            await RevokeFailedDeliveryAsync(grantId);
            throw;
        }
        finally
        {
            secret.Invalidate();
        }
    }

    public Task<RuntimeGrantReceipt> ConsumeBootstrapAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken = default) =>
        WithVerifiedProofAsync(proof, RuntimeCredentialPurpose.Configure,
            verified => ConsumeBootstrapCoreAsync(verified, operationId, cancellationToken));

    private async Task<RuntimeGrantReceipt> ConsumeBootstrapCoreAsync(
        VerifiedRuntimeProof verified, Guid operationId, CancellationToken cancellationToken)
    {
        var proof = verified.Proof;
        ValidateOperation(operationId);
        var registration = await RequireCurrentForProofAsync(verified, cancellationToken);
        var requestHash = ProofHash("consume", proof);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var current = await LockCurrentAsync(proof.GrantId, cancellationToken);
            await VerifyOriginalAsync(verified, registration, cancellationToken);
            var prior = await ReadOperationAsync<RuntimeGrantReceipt>(operationId, requestHash, cancellationToken);
            if (prior is not null)
            {
                await RequireSameCurrentAsync(registration, cancellationToken);
                RequireProofUsable(verified);
                RequireLive(current);
                await transaction.CommitAsync(cancellationToken);
                return prior;
            }
            RequireCurrentProof(current, proof, RuntimeCredentialState.Active);
            await RequireDeliveryAsync(current, cancellationToken);
            var consumed = NextRevision(current, RuntimeCredentialState.Consumed);
            await AdvanceAsync(current, consumed, cancellationToken);
            await InsertNewOperationAsync(operationId, proof.GrantId, requestHash, cancellationToken);
            var receipt = Receipt(consumed);
            AddReceipt(operationId, receipt);
            await RequireSameCurrentAsync(registration, cancellationToken);
            RequireLive(consumed);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
            await RequireCurrentForProofAsync(verified, cancellationToken);
            return receipt;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<RuntimeGrantReceipt> VerifyPendingBootstrapDeliveryAsync(
        RuntimeCredentialProof proof, Guid deliveryOperationId, CancellationToken cancellationToken) =>
        WithVerifiedProofAsync(proof, RuntimeCredentialPurpose.Configure,
            verified => VerifyPendingBootstrapCoreAsync(verified, deliveryOperationId, cancellationToken));

    private async Task<RuntimeGrantReceipt> VerifyPendingBootstrapCoreAsync(
        VerifiedRuntimeProof verified, Guid deliveryOperationId, CancellationToken cancellationToken)
    {
        var proof = verified.Proof;
        ValidateOperation(deliveryOperationId);
        var registration = await RequireCurrentForProofAsync(verified, cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await LockCurrentAsync(proof.GrantId, cancellationToken);
        await VerifyOriginalAsync(verified, registration, cancellationToken);
        RequireCurrentProof(current, proof, RuntimeCredentialState.Active);
        if (current.DeliveryOperationId != deliveryOperationId ||
            current.ExpiresAt != proof.Credential.ExpiresAt ||
            await db.RuntimeGrantOperationReceipts.AsNoTracking().AnyAsync(
                row => row.OperationId == deliveryOperationId, cancellationToken))
            throw Denied("runtime_pending_delivery_invalid");
        await RequireSameCurrentAsync(registration, cancellationToken);
        RequireLive(current);
        await transaction.CommitAsync(cancellationToken);
        await RequireSameCurrentAsync(registration, cancellationToken);
        RequireProofUsable(verified);
        RequireLive(current);
        return Receipt(current);
    }

    public Task<RuntimeCredentialExchange> ExchangeBootstrapAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken = default) =>
        WithVerifiedProofAsync(proof, RuntimeCredentialPurpose.Configure,
            verified => ExchangeAsync(verified, operationId, rotate: false, cancellationToken));

    public Task<RuntimeCredentialExchange> RotateSourceAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken = default) =>
        WithVerifiedProofAsync(proof, RuntimeCredentialPurpose.Observe,
            verified => ExchangeAsync(verified, operationId, rotate: true, cancellationToken));

    public Task<RuntimeGrantReceipt> VerifySourceAsync(
        RuntimeCredentialProof proof, CancellationToken cancellationToken = default) =>
        WithVerifiedProofAsync(proof, RuntimeCredentialPurpose.Observe,
            verified => VerifySourceCoreAsync(verified, cancellationToken));

    private async Task<RuntimeGrantReceipt> VerifySourceCoreAsync(
        VerifiedRuntimeProof verified, CancellationToken cancellationToken)
    {
        var proof = verified.Proof;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await LockCurrentAsync(proof.GrantId, cancellationToken);
        var original = await VerifyStoredProofAsync(verified, cancellationToken);
        RequireCurrentProof(current, proof, RuntimeCredentialState.Active);
        RequireLive(original);
        RequireLive(current);
        RequireProofUsable(verified);
        await RequireDeliveryAsync(current, cancellationToken);
        var registration = await RequireCurrentAsync(proof.RuntimeInstanceId, cancellationToken);
        if (original.RegistrationRevision != registration.Revision ||
            original.RegistrationHash != RuntimeContractValidation.RegistrationHash(registration))
            throw Denied("runtime_registration_stale");
        RequireProofUsable(verified);
        RequireLive(original);
        RequireLive(current);
        await transaction.CommitAsync(cancellationToken);
        RequireProofUsable(verified);
        RequireLive(current);
        return Receipt(current);
    }

    public Task<RuntimeGrantReceipt> RevokeAsync(
        RuntimeCredentialProof proof, Guid operationId, CancellationToken cancellationToken = default) =>
        WithVerifiedProofAsync(proof, proof.Purpose,
            verified => RevokeCoreAsync(verified, operationId, cancellationToken));

    private async Task<RuntimeGrantReceipt> RevokeCoreAsync(
        VerifiedRuntimeProof verified, Guid operationId, CancellationToken cancellationToken)
    {
        var proof = verified.Proof;
        ValidateOperation(operationId);
        var registration = await RequireCurrentForProofAsync(verified, cancellationToken);
        var requestHash = ProofHash("revoke", proof);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await LockCurrentAsync(proof.GrantId, cancellationToken);
        await VerifyOriginalAsync(verified, registration, cancellationToken);
        var prior = await ReadOperationAsync<RuntimeGrantReceipt>(operationId, requestHash, cancellationToken);
        if (prior is not null)
        {
            await RequireSameCurrentAsync(registration, cancellationToken);
            RequireProofUsable(verified);
            RequireLive(current);
            await transaction.CommitAsync(cancellationToken);
            return prior;
        }
        RequireCurrentProof(current, proof, current.State);
        if (current.State == RuntimeCredentialState.Revoked)
            throw Denied("runtime_grant_revoked");
        var revoked = NextRevision(current, RuntimeCredentialState.Revoked);
        await AdvanceAsync(current, revoked, cancellationToken);
        await InsertNewOperationAsync(operationId, proof.GrantId, requestHash, cancellationToken);
        var receipt = Receipt(revoked);
        AddReceipt(operationId, receipt);
        await RequireSameCurrentAsync(registration, cancellationToken);
        RequireProofUsable(verified);
        RequireLive(current);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private async Task<RuntimeCredentialExchange> ExchangeAsync(
        VerifiedRuntimeProof verified, Guid operationId, bool rotate, CancellationToken cancellationToken)
    {
        var proof = verified.Proof;
        ValidateOperation(operationId);
        var registration = await RequireCurrentForProofAsync(verified, cancellationToken);
        var requestHash = ProofHash(rotate ? "rotate" : "exchange", proof);
        SecretCredential? issued = null;
        var transferred = false;
        var committed = false;
        Guid? issuedGrantId = null;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var current = await LockCurrentAsync(proof.GrantId, cancellationToken);
            await VerifyOriginalAsync(verified, registration, cancellationToken);
            var prior = await ReadOperationAsync<RuntimeGrantReceipt>(
                operationId, requestHash, cancellationToken);
            if (prior is not null)
            {
                await RequireSameCurrentAsync(registration, cancellationToken);
                RequireProofUsable(verified);
                RequireLive(current);
                await transaction.CommitAsync(cancellationToken);
                return new RuntimeCredentialExchange(prior, null, isReplay: true);
            }
            RequireCurrentProof(current, proof,
                rotate ? RuntimeCredentialState.Active : RuntimeCredentialState.Consumed);
            await RequireDeliveryAsync(current, cancellationToken);
            issued = new SecretCredential(
                OpaqueHandle.NewHandle(), Expiry(registration, policy.SourceLifetime), timeProvider);
            RuntimeGrantRevision source;
            if (rotate)
            {
                source = NextRevision(current, RuntimeCredentialState.Active);
                source.VerifierHash = OpaqueHandle.Hash(issued.GetValue());
                source.ExpiresAt = issued.ExpiresAt;
                await AdvanceAsync(current, source, cancellationToken);
            }
            else
            {
                source = AddGrant(Guid.NewGuid(), registration, RuntimeCredentialPurpose.Observe,
                    registration.Binding.ObservationEndpoint, current.ConfigurationHash,
                    issued, current.DeliveryOperationId);
                await AdvanceAsync(current, NextRevision(current, RuntimeCredentialState.Revoked),
                    cancellationToken);
            }
            await InsertNewOperationAsync(operationId, source.GrantId, requestHash, cancellationToken);
            var receipt = Receipt(source);
            issuedGrantId = source.GrantId;
            AddReceipt(operationId, receipt);
            await RequireSameCurrentAsync(registration, cancellationToken);
            RequireLive(source);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            await RequireSameCurrentAsync(registration, cancellationToken);
            RequireProofUsable(verified);
            RequireLive(source);
            issued.LimitLifetime(source.ExpiresAt);
            transferred = true;
            return new RuntimeCredentialExchange(receipt, issued, isReplay: false);
        }
        finally
        {
            if (!transferred)
            {
                issued?.Invalidate();
                db.ChangeTracker.Clear();
                if (committed && issuedGrantId is { } abandonedGrantId)
                    await RevokeFailedDeliveryAsync(abandonedGrantId);
            }
        }
    }

    private RuntimeGrantRevision AddGrant(
        Guid grantId, RuntimeRegistration registration, RuntimeCredentialPurpose purpose,
        Uri audience, string configurationHash, SecretCredential secret, Guid deliveryOperationId)
    {
        var revision = new RuntimeGrantRevision
        {
            GrantId = grantId,
            Revision = 1,
            RuntimeInstanceId = registration.RuntimeInstanceId,
            RegistrationRevision = registration.Revision,
            RegistrationJson = JsonSerializer.Serialize(registration, JsonOptions),
            RegistrationHash = RuntimeContractValidation.RegistrationHash(registration),
            VerifierHash = OpaqueHandle.Hash(secret.GetValue()),
            Issuer = policy.Issuer,
            Purpose = purpose,
            Audience = audience.AbsoluteUri,
            ConfigurationHash = configurationHash,
            State = RuntimeCredentialState.Active,
            ExpiresAt = secret.ExpiresAt,
            RecordedAt = Now(),
            DeliveryOperationId = deliveryOperationId
        };
        db.RuntimeGrantHeads.Add(new RuntimeGrantHead { GrantId = grantId, CurrentRevision = 1 });
        db.RuntimeGrantRevisions.Add(revision);
        return revision;
    }

    private async Task<RuntimeGrantRevision> LockCurrentAsync(Guid grantId, CancellationToken cancellationToken)
    {
        if (grantId == Guid.Empty)
            throw Denied("runtime_grant_invalid");
        var head = await db.RuntimeGrantHeads.FromSqlInterpolated($"""
            SELECT grant_id, current_revision FROM identity_broker.runtime_grant_heads
            WHERE grant_id = {grantId} FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            ?? throw Denied("runtime_grant_unknown");
        return await db.RuntimeGrantRevisions.AsNoTracking().SingleAsync(
            row => row.GrantId == grantId && row.Revision == head.CurrentRevision, cancellationToken);
    }

    private async Task<T> WithVerifiedProofAsync<T>(
        RuntimeCredentialProof proof, RuntimeCredentialPurpose purpose,
        Func<VerifiedRuntimeProof, Task<T>> action)
    {
        var verified = CaptureProof(proof, purpose);
        try
        {
            return await action(verified);
        }
        catch (RuntimeAuthorizationException error) when (
            verified.Original is not null && (IsAuthorityLoss(error.Code) ||
                error.Code is "runtime_grant_expired" or "runtime_credential_unavailable" or "runtime_actor_unavailable"))
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var current = await LockCurrentAsync(proof.GrantId, CancellationToken.None);
            if (current.State != RuntimeCredentialState.Revoked &&
                current.VerifierHash == verified.Original.VerifierHash &&
                current.RegistrationHash == verified.Original.RegistrationHash)
                await InvalidateCurrentAsync(current, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            throw;
        }
    }

    private VerifiedRuntimeProof CaptureProof(RuntimeCredentialProof proof, RuntimeCredentialPurpose purpose)
    {
        if (proof.Revision <= 0 || !Enum.IsDefined(purpose) || proof.Purpose != purpose ||
            !RuntimeContractValidation.IsHttpsEndpoint(proof.Audience))
            throw Denied("runtime_purpose_invalid");
        RuntimeContractValidation.ValidateHash(proof.ConfigurationHash);
        try
        {
            var value = proof.Credential.GetValue();
            RuntimeContractValidation.ValidateHash(value);
            return new VerifiedRuntimeProof(proof, purpose, OpaqueHandle.Hash(value));
        }
        catch (InvalidOperationException) when (!proof.Credential.IsUsable())
        {
            throw Denied("runtime_credential_unavailable");
        }
    }

    private async Task VerifyOriginalAsync(
        VerifiedRuntimeProof verified, RuntimeRegistration registration, CancellationToken cancellationToken)
    {
        var original = await VerifyStoredProofAsync(verified, cancellationToken);
        RequireProofUsable(verified);
        RequireLive(original);
        if (original.RegistrationRevision != registration.Revision ||
            original.RegistrationHash != RuntimeContractValidation.RegistrationHash(registration))
            throw Denied("runtime_proof_invalid");
    }

    private async Task<RuntimeGrantRevision> VerifyStoredProofAsync(
        VerifiedRuntimeProof verified, CancellationToken cancellationToken)
    {
        var proof = verified.Proof;
        var original = await db.RuntimeGrantRevisions.AsNoTracking().SingleOrDefaultAsync(
            row => row.GrantId == proof.GrantId && row.Revision == proof.Revision, cancellationToken)
            ?? throw Denied("runtime_grant_unknown");
        if (original.Purpose != verified.Purpose || original.RuntimeInstanceId != proof.RuntimeInstanceId ||
            original.Issuer != policy.Issuer || original.Audience != proof.Audience.AbsoluteUri ||
            original.ConfigurationHash != proof.ConfigurationHash ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(verified.VerifierHash), Convert.FromHexString(original.VerifierHash)))
            throw Denied("runtime_proof_invalid");
        verified.Original = original;
        return original;
    }

    private async Task<RuntimeRegistration> RequireCurrentForProofAsync(
        VerifiedRuntimeProof verified, CancellationToken cancellationToken)
    {
        var proof = verified.Proof;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await LockCurrentAsync(proof.GrantId, cancellationToken);
        var original = await VerifyStoredProofAsync(verified, cancellationToken);
        var controlsCurrent = current.VerifierHash == original.VerifierHash &&
            current.RegistrationHash == original.RegistrationHash &&
            current.State != RuntimeCredentialState.Revoked;
        if (current.ExpiresAt <= Now() || original.ExpiresAt <= Now() || !proof.Credential.IsUsable())
        {
            if (controlsCurrent)
                await InvalidateCurrentAsync(current, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw Denied("runtime_grant_expired");
        }
        RuntimeRegistration registration;
        try
        {
            registration = await RequireCurrentAsync(proof.RuntimeInstanceId, cancellationToken);
            if (original.RegistrationRevision != registration.Revision ||
                original.RegistrationHash != RuntimeContractValidation.RegistrationHash(registration))
                throw Denied("runtime_registration_stale");
            await RequireSameCurrentAsync(registration, cancellationToken);
        }
        catch (RuntimeAuthorizationException error) when (IsAuthorityLoss(error.Code))
        {
            if (controlsCurrent)
                await InvalidateCurrentAsync(current, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw;
        }
        RequireProofUsable(verified);
        RequireLive(current);
        RequireLive(original);
        await transaction.CommitAsync(cancellationToken);
        return registration;
    }

    private async Task InvalidateCurrentAsync(RuntimeGrantRevision current, CancellationToken cancellationToken)
    {
        await AdvanceAsync(current, NextRevision(current, RuntimeCredentialState.Revoked), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    private async Task<RuntimeRegistration> RequireCurrentAsync(
        Guid runtimeInstanceId, CancellationToken cancellationToken)
    {
        if (runtimeInstanceId == Guid.Empty)
            throw Denied("runtime_registration_invalid");
        if (!actor.Bearer.IsUsable())
            throw Denied("runtime_actor_unavailable");
        RuntimeRegistration registration;
        try
        {
            registration = await owner.ReadCurrentAsync(runtimeInstanceId, actor, cancellationToken);
        }
        catch (InvalidOperationException) when (!actor.Bearer.IsUsable())
        {
            throw Denied("runtime_actor_unavailable");
        }
        if (!actor.Bearer.IsUsable())
            throw Denied("runtime_actor_unavailable");
        RuntimeContractValidation.Validate(registration);
        if (registration.RuntimeInstanceId != runtimeInstanceId ||
            registration.State != RuntimeRegistrationState.Active || registration.ExpiresAt <= Now())
            throw Denied("runtime_registration_unavailable");
        return registration;
    }

    private async Task RequireSameCurrentAsync(
        RuntimeRegistration expected, CancellationToken cancellationToken)
    {
        var current = await RequireCurrentAsync(expected.RuntimeInstanceId, cancellationToken);
        if (current != expected)
            throw Denied("runtime_registration_stale");
    }

    private async Task RequireDeliveryAsync(RuntimeGrantRevision grant, CancellationToken cancellationToken)
    {
        if (!await db.RuntimeGrantOperationReceipts.AsNoTracking().AnyAsync(
                row => row.OperationId == grant.DeliveryOperationId, cancellationToken))
            throw Denied("runtime_delivery_pending");
    }

    private async Task AdvanceAsync(
        RuntimeGrantRevision current, RuntimeGrantRevision next, CancellationToken cancellationToken)
    {
        var advanced = await db.RuntimeGrantHeads
            .Where(row => row.GrantId == current.GrantId && row.CurrentRevision == current.Revision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CurrentRevision, next.Revision),
                cancellationToken);
        if (advanced != 1)
            throw Denied("runtime_grant_stale");
        db.RuntimeGrantRevisions.Add(next);
    }

    private RuntimeGrantRevision NextRevision(RuntimeGrantRevision current, RuntimeCredentialState state) => new()
    {
        GrantId = current.GrantId,
        Revision = checked(current.Revision + 1),
        RuntimeInstanceId = current.RuntimeInstanceId,
        RegistrationRevision = current.RegistrationRevision,
        RegistrationJson = current.RegistrationJson,
        RegistrationHash = current.RegistrationHash,
        VerifierHash = current.VerifierHash,
        Issuer = current.Issuer,
        Purpose = current.Purpose,
        Audience = current.Audience,
        ConfigurationHash = current.ConfigurationHash,
        State = state,
        ExpiresAt = current.ExpiresAt,
        RecordedAt = Now(),
        DeliveryOperationId = current.DeliveryOperationId
    };

    private async Task<bool> InsertOperationAsync(
        Guid operationId, Guid grantId, string requestHash, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity_broker.runtime_grant_operations(operation_id, grant_id, request_hash)
            VALUES ({operationId}, {grantId}, {requestHash})
            ON CONFLICT (operation_id) DO NOTHING
            """, cancellationToken) == 1;

    private async Task InsertNewOperationAsync(
        Guid operationId, Guid grantId, string requestHash, CancellationToken cancellationToken)
    {
        if (!await InsertOperationAsync(operationId, grantId, requestHash, cancellationToken))
            throw Denied("runtime_operation_conflict");
    }

    private async Task<T?> ReadOperationAsync<T>(
        Guid operationId, string requestHash, CancellationToken cancellationToken) where T : class
    {
        var operation = await db.RuntimeGrantOperations.AsNoTracking().SingleOrDefaultAsync(
            row => row.OperationId == operationId, cancellationToken);
        if (operation is null)
            return null;
        if (operation.RequestHash != requestHash)
            throw Denied("runtime_operation_conflict");
        var receipt = await db.RuntimeGrantOperationReceipts.AsNoTracking().SingleOrDefaultAsync(
            row => row.OperationId == operationId, cancellationToken);
        if (receipt is null)
            throw Denied("runtime_operation_pending");
        return JsonSerializer.Deserialize<T>(receipt.ReceiptJson, JsonOptions)
            ?? throw new InvalidOperationException("A committed runtime operation receipt is invalid.");
    }

    private void AddReceipt<T>(Guid operationId, T receipt) =>
        db.RuntimeGrantOperationReceipts.Add(new RuntimeGrantOperationReceipt
        {
            OperationId = operationId,
            ReceiptJson = JsonSerializer.Serialize(receipt, JsonOptions)
        });

    private async Task RevokeFailedDeliveryAsync(Guid grantId)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var exists = await db.RuntimeGrantHeads.AsNoTracking().AnyAsync(
            row => row.GrantId == grantId, CancellationToken.None);
        if (!exists)
            return;
        var current = await LockCurrentAsync(grantId, CancellationToken.None);
        if (current.State != RuntimeCredentialState.Revoked)
        {
            await AdvanceAsync(current, NextRevision(current, RuntimeCredentialState.Revoked),
                CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        await transaction.CommitAsync(CancellationToken.None);
    }

    private RuntimeGrantReceipt Receipt(RuntimeGrantRevision revision) => new(
        revision.GrantId, revision.RuntimeInstanceId, revision.RegistrationRevision, revision.Revision,
        revision.Issuer, revision.Purpose, new Uri(revision.Audience), revision.State,
        revision.ConfigurationHash, revision.ExpiresAt, revision.RecordedAt);

    private DateTimeOffset Expiry(RuntimeRegistration registration, TimeSpan lifetime)
    {
        var limit = Now().Add(lifetime);
        var expiry = registration.ExpiresAt < limit ? registration.ExpiresAt : limit;
        if (actor.Bearer.ExpiresAt < expiry)
            expiry = actor.Bearer.ExpiresAt;
        return new DateTimeOffset(expiry.UtcTicks - expiry.UtcTicks % 10, TimeSpan.Zero);
    }

    private DateTimeOffset Now()
    {
        var value = timeProvider.GetUtcNow();
        return new DateTimeOffset(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    }

    private void RequireLive(RuntimeGrantRevision grant)
    {
        if (grant.ExpiresAt <= Now())
            throw Denied("runtime_grant_expired");
    }

    private static void RequireCurrentProof(
        RuntimeGrantRevision current, RuntimeCredentialProof proof, RuntimeCredentialState state)
    {
        if (current.Revision != proof.Revision || current.State != state)
            throw Denied("runtime_grant_stale");
    }

    private static string ProofHash(string operation, RuntimeCredentialProof proof) =>
        Hash(operation, proof.GrantId, proof.RuntimeInstanceId, proof.Revision,
            proof.Purpose, proof.Audience.AbsoluteUri, proof.ConfigurationHash);

    private static string Hash(params object[] values) =>
        RuntimeContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(values, JsonOptions));

    private static void ValidateOperation(Guid operationId)
    {
        if (operationId == Guid.Empty)
            throw Denied("runtime_operation_invalid");
    }

    private static RuntimeAuthorizationException Denied(string code) => new(code);

    private static void RequireProofUsable(VerifiedRuntimeProof verified)
    {
        if (!verified.Proof.Credential.IsUsable())
            throw Denied("runtime_credential_unavailable");
    }

    private sealed class VerifiedRuntimeProof(
        RuntimeCredentialProof proof, RuntimeCredentialPurpose purpose, string verifierHash)
    {
        public RuntimeCredentialProof Proof { get; } = proof;
        public RuntimeCredentialPurpose Purpose { get; } = purpose;
        public string VerifierHash { get; } = verifierHash;
        public RuntimeGrantRevision? Original { get; set; }
    }

    private static bool IsAuthorityLoss(string code) => code is
        "runtime_owner_denied" or "runtime_registration_unknown" or "runtime_registration_unavailable" or
        "runtime_registration_stale" or "runtime_registration_mismatch";
}
