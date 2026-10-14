using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Environment.Tests;

internal static class SandboxLeaseProviderAttemptTestSupport
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static JsonElement SerializeOutcome<TReceipt>(TReceipt receipt) =>
        JsonSerializer.SerializeToElement(receipt, JsonOptions);

    public static async Task<SandboxLeaseSnapshot> CompleteProvisionAsync(
        ISandboxLeaseStore store,
        Guid operationId,
        EnvironmentGenerationFence fence,
        SandboxProvisionedResource? provisionedResource,
        bool effectMayHaveApplied,
        CancellationToken cancellationToken,
        Guid? providerAttemptId = null,
        string? providerErrorCode = null)
    {
        if (providerAttemptId is null &&
            provisionedResource is null &&
            !effectMayHaveApplied)
            return await store.CompleteProvisionAsync(
                operationId,
                fence,
                provisionedResource,
                effectMayHaveApplied,
                cancellationToken,
                providerErrorCode: providerErrorCode).ConfigureAwait(false);

        var attempts = await store.GetProviderAttemptsAsync(
            fence, operationId, cancellationToken).ConfigureAwait(false);
        var lease = attempts.IsEmpty
            ? await store.GetCurrentAsync(fence, cancellationToken).ConfigureAwait(false)
            : await store.GetAsync(
                fence, attempts[0].Request.ResourceGeneration, cancellationToken).ConfigureAwait(false);
        if (lease?.OperationId != operationId)
            throw new InvalidOperationException("The Sandbox provision lease is missing.");
        if (providerAttemptId is null)
        {
            var previous = attempts.LastOrDefault(attempt =>
                attempt.Request.EffectKind == SandboxProviderEffectKind.Provision);
            if (previous is { State: SandboxProviderAttemptState.InProgress or
                    SandboxProviderAttemptState.ResultRecorded or
                    SandboxProviderAttemptState.Unknown or
                    SandboxProviderAttemptState.Completed })
            {
                providerAttemptId = previous.AttemptId;
            }
            else
            {
                if (lease is null)
                    throw new InvalidOperationException("The Sandbox provision lease is missing.");
                if (lease.ProviderRequestFingerprint is null)
                    lease = await store.SaveProviderRequestAsync(
                        operationId,
                        fence,
                        lease.ProvisionIntent.ProviderRequest,
                        cancellationToken).ConfigureAwait(false);
                var reservation = await store.BeginProviderAttemptAsync(
                    SandboxProviderAttemptRequest.ForProvision(lease),
                    cancellationToken).ConfigureAwait(false);
                providerAttemptId = reservation.Attempt.AttemptId;
            }
        }

        return await store.CompleteProvisionAsync(
            operationId,
            fence,
            provisionedResource,
            effectMayHaveApplied,
            cancellationToken,
            providerAttemptId,
            providerErrorCode).ConfigureAwait(false);
    }

    public static async Task<SandboxLeaseSnapshot> CompleteReleaseAsync(
        ISandboxLeaseStore store,
        SandboxLeaseSnapshot lease,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null,
        Guid? providerAttemptId = null)
    {
        var attempt = await GetOrBeginAttemptAsync(
            store,
            lease,
            SandboxProviderEffectKind.Release,
            SandboxProviderAttemptRequest.ForRelease(
                lease,
                lease.ProvisionedResource ?? throw new InvalidOperationException(
                    "The Sandbox release lease has no provider resource.")),
            cancellationToken,
            providerAttemptId).ConfigureAwait(false);
        await RecordReceiptAsync(store, attempt, receipt, lease.Fence, cancellationToken).ConfigureAwait(false);
        return await store.CompleteReleaseAsync(
            lease.OperationId,
            lease.Fence,
            lease.ProviderFencingGeneration,
            receipt,
            cancellationToken,
            beforeCommitCheck,
            attempt.AttemptId).ConfigureAwait(false);
    }

    public static async Task<SandboxLeaseSnapshot> CompletePartialReleaseAsync(
        ISandboxLeaseStore store,
        SandboxLeaseSnapshot lease,
        SandboxPartialReleaseReceipt receipt,
        CancellationToken cancellationToken)
    {
        var attempt = await GetOrBeginAttemptAsync(
            store,
            lease,
            SandboxProviderEffectKind.PartialRelease,
            SandboxProviderAttemptRequest.ForPartialRelease(lease),
            cancellationToken).ConfigureAwait(false);
        await RecordReceiptAsync(store, attempt, receipt, lease.Fence, cancellationToken).ConfigureAwait(false);
        return await store.CompletePartialReleaseAsync(
            new SandboxPartialReleaseCompletionRequest(lease.Fence, receipt)
            {
                ProviderAttemptId = attempt.AttemptId
            },
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task CompleteLateResourceCleanupAsync(
        ISandboxLeaseStore store,
        EnvironmentGenerationFence currentFence,
        SandboxLateResourceCleanupClaim claim,
        SandboxReleaseReceipt receipt,
        CancellationToken cancellationToken)
    {
        var request = SandboxProviderAttemptRequest.ForLateResourceRelease(claim);
        var attempt = await GetOrBeginAttemptAsync(
            store,
            claim.Lease,
            SandboxProviderEffectKind.LateResourceRelease,
            request,
            cancellationToken,
            claim.ReleaseAttemptId).ConfigureAwait(false);
        await RecordReceiptAsync(store, attempt, receipt, claim.Lease.Fence, cancellationToken)
            .ConfigureAwait(false);
        await store.CompleteLateResourceCleanupAsync(
            currentFence,
            claim,
            receipt,
            cancellationToken,
            attempt.AttemptId).ConfigureAwait(false);
    }

    public static async Task<SandboxProviderAttemptSnapshot> BeginProvisionAttemptAsync(
        ISandboxLeaseStore store,
        SandboxLeaseSnapshot lease,
        CancellationToken cancellationToken)
    {
        if (lease.ProviderRequestFingerprint is null)
            lease = await store.SaveProviderRequestAsync(
                lease.OperationId,
                lease.Fence,
                lease.ProvisionIntent.ProviderRequest,
                cancellationToken).ConfigureAwait(false);
        return (await store.BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest.ForProvision(lease),
            cancellationToken).ConfigureAwait(false)).Attempt;
    }

    private static async Task<SandboxProviderAttemptSnapshot> GetOrBeginAttemptAsync(
        ISandboxLeaseStore store,
        SandboxLeaseSnapshot lease,
        SandboxProviderEffectKind effectKind,
        SandboxProviderAttemptRequest request,
        CancellationToken cancellationToken,
        Guid? providerAttemptId = null)
    {
        if (providerAttemptId is { } exactId)
        {
            var attempts = await store.GetProviderAttemptsAsync(
                lease.Fence, lease.OperationId, cancellationToken).ConfigureAwait(false);
            return attempts.Single(attempt => attempt.AttemptId == exactId);
        }

        var existingAttempts = await store.GetProviderAttemptsAsync(
            lease.Fence, lease.OperationId, cancellationToken).ConfigureAwait(false);
        var previous = existingAttempts.LastOrDefault(attempt =>
            attempt.Request.EffectKind == effectKind &&
            attempt.Request.RequestFingerprint == request.RequestFingerprint);
        if (previous is { State: SandboxProviderAttemptState.InProgress or
                SandboxProviderAttemptState.ResultRecorded or
                SandboxProviderAttemptState.Unknown or
                SandboxProviderAttemptState.Completed })
            return previous;
        if (previous is { State: SandboxProviderAttemptState.LegacyUnknown })
            throw new InvalidOperationException("The legacy provider attempt is unresolved.");

        return (await store.BeginProviderAttemptAsync(request, cancellationToken).ConfigureAwait(false)).Attempt;
    }

    private static Task<SandboxProviderAttemptSnapshot> RecordReceiptAsync<TReceipt>(
        ISandboxLeaseStore store,
        SandboxProviderAttemptSnapshot attempt,
        TReceipt receipt,
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken) =>
        attempt.State == SandboxProviderAttemptState.InProgress
            ? store.RecordProviderAttemptOutcomeAsync(
                attempt.AttemptId,
                fence,
                JsonSerializer.SerializeToElement(receipt, JsonOptions),
                effectMayHaveApplied: true,
                failureCode: null,
                cancellationToken)
            : Task.FromResult(attempt);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
