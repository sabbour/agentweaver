using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Npgsql;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Runs the platform Guardrails checks on model-turn user input before the Orchestrator admits the turn.
/// The checks run after the grant is issued and before the policy receipt is written, so a withheld turn
/// has no receipt and the runtime can't verify or perform it.
/// </summary>
internal static class RuntimeGuardrailsGate
{
    /// <summary>Prefix for every public Guardrails failure code returned by runtime action admission.</summary>
    internal const string CodePrefix = "runtime_guardrails_";

    /// <summary>
    /// Returns the accepted checks when Guardrails checks apply to a model turn, or null when they don't.
    /// Call this before issuing the grant, so a request without content has no side effects.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">
    /// Thrown with <c>runtime_guardrails_content_required</c> when checks apply and the request has no content,
    /// or with <c>runtime_guardrails_unavailable</c> when checks apply and no classifier is registered.
    /// </exception>
    internal static GuardrailsPreparation? Prepare(
        IServiceProvider services, RuntimeActionRequest request, AuthorizedRunSelection accepted, byte[]? content)
    {
        if (request.ActionId != "model.turn")
            return null;
        var acceptedChecks = ReadAcceptedChecks(accepted.Selection.Snapshot);
        var resolver = services.GetService<GuardrailsStagePlanResolver>();
        if (resolver is null)
        {
            var catalog = services.GetService<ProviderCatalog>();
            if (acceptedChecks is not null ||
                catalog is not null && catalog.TryGetOrdered(ProviderSeam.Guardrails, out var ids) && !ids.IsDefaultOrEmpty)
                throw new RuntimeAuthorizationException(CodePrefix + "unavailable");
            return null;
        }
        if (!resolver.IsActive(acceptedChecks))
            return null;
        if (content is null)
            throw new RuntimeAuthorizationException(ContentRequiredCode);
        return new(resolver, acceptedChecks, content);
    }

    /// <summary>Public code returned when Guardrails checks apply and the request has no content.</summary>
    internal const string ContentRequiredCode = CodePrefix + "content_required";

    /// <summary>
    /// Requires the ordered Guardrails checks to allow the model-turn content. Call this after the grant is
    /// issued and before the policy receipt is written.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the checks withhold the content or can't run.</exception>
    internal static async Task RequireAllowedAsync(
        IServiceProvider services,
        AgtPolicyProvider policy,
        GuardrailsPreparation preparation,
        Func<CancellationToken, Task<GuardrailsAuthorityStatus>> checkCurrentAuthority,
        RuntimeRegistration registration,
        AuthorizedRunSelection accepted,
        RuntimeActionRequest request,
        ExecutableActionGrantReference grant,
        PinnedProviderBinding policyBinding,
        AgtPolicyProviderOptions policyOptions,
        CancellationToken cancellationToken)
    {
        var content = preparation.Content;
        var binding = registration.Binding;
        var plan = preparation.Resolver.Resolve(binding.RunId, GuardrailsContentStage.UserInput,
            GuardrailsContentClass.Text, preparation.AcceptedChecks);
        if (!plan.IsSuccess)
            throw new RuntimeAuthorizationException(Code(plan.ReasonCode));

        var authorization = accepted.Authorization;
        var acceptedRun = new GuardrailsAcceptedRunBinding(
            authorization.Issuer,
            authorization.ActorId,
            authorization.TenantId,
            binding.ProjectId,
            binding.RunId,
            binding.SessionId,
            request.ActionId,
            RuntimeActionContract.Purpose,
            SelectionRevision(accepted.Selection),
            SelectionHash(accepted.Selection),
            grant.GrantId,
            grant.Revision,
            binding.ExecutionFence,
            policyBinding,
            policyOptions,
            [],
            [plan.Plan!]);
        var contentRequest = new GuardrailsContentRequest(
            request.EventId,
            "model-turn-" + request.EventId.ToString("N"),
            request.InputHash,
            GuardrailsContentStage.UserInput,
            GuardrailsContentClass.Text,
            null,
            [.. content]);
        var gate = new GuardrailsContentGate(policy, new CurrentAuthority(checkCurrentAuthority),
            services.GetService<IGuardrailsEvidenceJournal>());
        var result = await gate.EvaluateAsync(acceptedRun, contentRequest, cancellationToken).ConfigureAwait(false);
        if (result.Decision != GuardrailsDecision.Allow)
            throw new RuntimeAuthorizationException(Code(result.ReasonCode));
    }

    /// <summary>
    /// Reads the ordered Guardrails selection that Projects recorded in the accepted run snapshot.
    /// Returns null when the snapshot has no Guardrails selection.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the recorded selection is malformed.</exception>
    internal static IReadOnlyList<GuardrailsAcceptedCheck>? ReadAcceptedChecks(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object ||
            !snapshot.TryGetProperty("providers", out var providers) ||
            providers.ValueKind != JsonValueKind.Array)
            return null;
        ImmutableArray<EffectiveProviderSelection> selections;
        try
        {
            selections = JsonSerializer.Deserialize<ImmutableArray<EffectiveProviderSelection>>(
                providers.GetRawText(), SourceControlEndpoints.ProviderSelectionJsonOptions);
        }
        catch (JsonException)
        {
            throw new RuntimeAuthorizationException(Code(GuardrailsReasonCode.InvalidConfiguration));
        }
        if (selections.IsDefault)
            return null;
        var guardrails = selections.Where(item => item?.Seam == ProviderSeam.Guardrails).ToArray();
        if (guardrails.Length == 0)
            return null;
        if (guardrails.Length != 1 ||
            guardrails[0].Cardinality != ProviderCardinality.OrderedComposite ||
            guardrails[0].Candidates.IsDefault ||
            guardrails[0].Candidates.Any(item => item is null || item.Seam != ProviderSeam.Guardrails))
            throw new RuntimeAuthorizationException(Code(GuardrailsReasonCode.InvalidConfiguration));
        return guardrails[0].Candidates
            .Select(item => new GuardrailsAcceptedCheck(
                item.ProviderId, item.AdapterVersion, item.OptionsSchemaVersion, item.OptionsRevision))
            .ToArray();
    }

    /// <summary>Returns the revision identifier of the accepted selection.</summary>
    internal static string SelectionRevision(EffectiveRunSelection selection) =>
        $"{selection.ProjectRevision}.{selection.ProjectConfigurationRevision}.{selection.PlatformRuntimeRevision}";

    /// <summary>Returns the SHA-256 digest of the accepted snapshot and its context revision.</summary>
    internal static string SelectionHash(EffectiveRunSelection selection) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            selection.Snapshot.GetRawText() + "\n" + selection.ContextRevision)));

    /// <summary>Maps a Guardrails reason to its public, snake-case failure code.</summary>
    internal static string Code(GuardrailsReasonCode reason)
    {
        var name = reason.ToString();
        var builder = new StringBuilder(CodePrefix, CodePrefix.Length + name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index]))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[index]));
        }
        return builder.ToString();
    }

    /// <summary>Returns true for Guardrails failures that a retry can resolve.</summary>
    internal static bool IsTransient(string code) =>
        code.StartsWith(CodePrefix, StringComparison.Ordinal) &&
        (code.EndsWith("_unavailable", StringComparison.Ordinal) ||
         code == Code(GuardrailsReasonCode.ClassifierTimedOut));

    /// <summary>
    /// Builds the authority check that the gate runs around each classifier call. A changed registration or
    /// accepted selection is stale, a revoked registration is revoked, and an owner or database failure is unavailable.
    /// </summary>
    internal static Func<CancellationToken, Task<GuardrailsAuthorityStatus>> AuthorityCheck(
        Func<CancellationToken, Task<RuntimeRegistration>> readRegistration,
        Func<CancellationToken, Task<AuthorizedRunSelection>> readSelection,
        RuntimeRegistration expected,
        string expectedSelectionHash) =>
        async cancellationToken =>
        {
            try
            {
                if (await readRegistration(cancellationToken).ConfigureAwait(false) != expected)
                    return GuardrailsAuthorityStatus.Stale;
                var selection = await readSelection(cancellationToken).ConfigureAwait(false);
                return SelectionHash(selection.Selection) == expectedSelectionHash
                    ? GuardrailsAuthorityStatus.Current
                    : GuardrailsAuthorityStatus.Stale;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RuntimeAuthorizationException)
            {
                return GuardrailsAuthorityStatus.Revoked;
            }
            catch (CoordinationException failure) when (failure.StatusCode < StatusCodes.Status500InternalServerError)
            {
                return GuardrailsAuthorityStatus.Revoked;
            }
            catch (Exception failure) when (failure is CoordinationException or HttpRequestException or
                NpgsqlException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                return GuardrailsAuthorityStatus.Unavailable;
            }
        };

    private sealed class CurrentAuthority(Func<CancellationToken, Task<GuardrailsAuthorityStatus>> check)
        : IGuardrailsCurrentAuthority
    {
        public Task<GuardrailsAuthorityStatus> CheckCurrentAsync(
            GuardrailsAuthorityCheckRequest request, CancellationToken cancellationToken) =>
            check(cancellationToken);
    }
}

/// <summary>Resolver, accepted checks, and UTF-8 content for one model turn that Guardrails checks apply to.</summary>
internal sealed record GuardrailsPreparation(
    GuardrailsStagePlanResolver Resolver,
    IReadOnlyList<GuardrailsAcceptedCheck>? AcceptedChecks,
    byte[] Content);
