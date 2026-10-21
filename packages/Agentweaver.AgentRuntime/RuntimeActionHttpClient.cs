using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Obtains an owner-issued action admission and verifies the same receipt immediately before
/// the runtime performs a guarded side effect.
/// </summary>
public sealed class RuntimeActionHttpClient(HttpClient client, Uri orchestratorAddress, RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(orchestratorAddress);

    /// <summary>Builds a versioned request bound to registration, execution fence, input hash, and event ID.</summary>
    internal async Task RequireAsync(
        RuntimeRegistration registration, string actionId, ReadOnlyMemory<byte> input,
        Func<CancellationToken, Task> requireCurrentAuthority, CancellationToken cancellationToken,
        bool isToolInvocation = false, Guid? dispatchId = null, Guid? costTurnId = null,
        Guid? eventId = null)
    {
        var request = new RuntimeActionRequest(1, registration.RuntimeInstanceId, registration.Revision,
            registration.Binding.ExecutionFence, eventId ?? Guid.NewGuid(), actionId,
            RuntimeContractValidation.Hash(input.Span))
        {
            IsToolInvocation = isToolInvocation, DispatchId = dispatchId, CostTurnId = costTurnId
        };
        await RequireAsync(request, requireCurrentAuthority, cancellationToken, ModelTurnContent(actionId, input))
            .ConfigureAwait(false);
    }

    // The owner returns runtime_owner_content_required when platform Guardrails checks apply to a model turn.
    // Only then does the client send the UTF-8 model-turn text, so runs without Guardrails never send prompt text.
    // Input that isn't strict UTF-8 or exceeds the content limit has no content form, and the owner withholds it.
    private static string? ModelTurnContent(string actionId, ReadOnlyMemory<byte> input)
    {
        if (actionId != "model.turn" || input.IsEmpty || input.Length > RuntimeActionContract.MaximumContentBytes)
            return null;
        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(input.Span);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Requires current authority before and after admission, then verifies the exact unchanged
    /// owner response. A denial, stale receipt, or changed receipt fails closed.
    /// </summary>
    internal async Task RequireAsync(
        RuntimeActionRequest request, Func<CancellationToken, Task> requireCurrentAuthority,
        CancellationToken cancellationToken, string? content = null)
    {
        RuntimeActionContract.Validate(request);
        if (content is not null)
            _ = RuntimeActionContract.ValidateContent(new RuntimeActionContentRequest(request, content));
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        RuntimeActionAdmission admitted;
        try
        {
            admitted = await SendAdmissionAsync(
                "/internal/runtime/actions/authorize", request, requireCurrentAuthority, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RuntimeAuthorizationException failure) when (failure.Code == "runtime_owner_content_required")
        {
            if (content is null)
                throw new RuntimeAuthorizationException("runtime_guardrails_content_unsupported");
            await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
            admitted = await SendAdmissionAsync(
                "/internal/runtime/actions/authorize-content", new RuntimeActionContentRequest(request, content),
                requireCurrentAuthority, cancellationToken).ConfigureAwait(false);
        }
        if (admitted.ContractVersion != 1 || admitted.Request != request ||
            admitted.PolicyReceiptId != request.EventId || admitted.GrantId != request.EventId.ToString("N") ||
            admitted.GrantRevision != "1")
            throw new RuntimeAuthorizationException("runtime_action_admission_invalid");
        if (admitted.Outcome != PolicyEvaluationOutcome.Allow ||
            admitted.ReasonCode != PolicyEvaluationReasonCode.Allowed)
            throw new RuntimeAuthorizationException("runtime_action_denied");
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        var verified = await SendAdmissionAsync(
            "/internal/runtime/actions/verify", admitted, requireCurrentAuthority, cancellationToken)
            .ConfigureAwait(false);
        if (verified != admitted)
            throw new RuntimeAuthorizationException("runtime_action_admission_changed");
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Convenience overload for a caller that already owns the stable action event ID.</summary>
    internal Task RequireAsync(
        RuntimeRegistration registration, string actionId, Guid eventId, ReadOnlyMemory<byte> input,
        Func<CancellationToken, Task> requireCurrentAuthority, CancellationToken cancellationToken,
        Guid? costTurnId = null) =>
        RequireAsync(registration, actionId, input, requireCurrentAuthority, cancellationToken,
            costTurnId: costTurnId, eventId: eventId);

    /// <summary>Retries one transport failure only after confirming the caller's authority remains current.</summary>
    private async Task<RuntimeActionAdmission> SendAdmissionAsync(
        string path, object request, Func<CancellationToken, Task> requireCurrentAuthority,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RuntimeOwnerHttpTransport.SendAsync<RuntimeActionAdmission>(
                client, _address, path, actor, request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
            return await RuntimeOwnerHttpTransport.SendAsync<RuntimeActionAdmission>(
                client, _address, path, actor, request, cancellationToken).ConfigureAwait(false);
        }
    }
}
