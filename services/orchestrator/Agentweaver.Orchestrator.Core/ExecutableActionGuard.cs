using System.Collections.Immutable;
using System.Security.Claims;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator.Core;

public sealed record ExecutableActionGrantReference(string GrantId, string Revision);

public enum ExecutableActionGrantState
{
    Active,
    Revoked,
    Expired,
    Superseded
}

public sealed record ValidatedExecutableActionGrant(
    ExecutableActionGrantReference Reference,
    ExecutableActionGrantState State,
    string Issuer,
    string ActorId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    string StepId,
    ImmutableHashSet<string> ActionIds,
    string Purpose,
    long Fence,
    DateTimeOffset ExpiresAt);

public enum ExecutableActionGrantLookupStatus
{
    Current,
    Unknown,
    Revoked,
    Expired,
    Stale,
    Error
}

public sealed record ExecutableActionGrantLookupResult(
    ExecutableActionGrantLookupStatus Status,
    ValidatedExecutableActionGrant? Grant = null)
{
    public static ExecutableActionGrantLookupResult Current(ValidatedExecutableActionGrant grant) =>
        new(ExecutableActionGrantLookupStatus.Current, grant);
}

public interface IExecutableActionGrantOwnerLookup
{
    Task<ExecutableActionGrantLookupResult> GetCurrentAsync(
        ExecutableActionGrantReference reference,
        CancellationToken cancellationToken = default);
}

public sealed record ExecutableActionInvocation(
    ClaimsPrincipal Caller,
    string SessionId,
    string StepId,
    string ActionId,
    string Purpose,
    ExecutableActionGrantReference GrantReference,
    long Fence,
    PinnedProviderBinding? PolicyBinding,
    Guid EventId);

public sealed record ExecutableActionGuardResult<T>(
    PolicyEvaluationOutcome Outcome,
    PolicyEvaluationReasonCode ReasonCode,
    bool EffectInvoked,
    T? EffectResult);

public sealed class ExecutableActionGuard(
    AgtPolicyProvider? policyProvider,
    AgtPolicyProviderOptions? policyOptions,
    ISessionsJournal? sessionsJournal,
    IExecutableActionGrantOwnerLookup? grantOwnerLookup = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ExecutableActionGuardResult<T>> ExecuteAsync<T>(
        ExecutableActionInvocation invocation,
        Func<CancellationToken, Task<T>> protectedEffect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(protectedEffect);

        if (policyProvider is null || policyOptions is null || sessionsJournal is null)
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
        if (grantOwnerLookup is null)
            return Deny<T>(PolicyEvaluationReasonCode.NoEffectiveGrant);

        if (!TryGetCallerBindings(
                invocation.Caller,
                out var issuer,
                out var actorId,
                out var projectId,
                out var runId) ||
            !IsValidInvocation(invocation))
            return Deny<T>(PolicyEvaluationReasonCode.NoEffectiveGrant);

        var lookup = await grantOwnerLookup.GetCurrentAsync(
            invocation.GrantReference, cancellationToken).ConfigureAwait(false);
        if (lookup is null)
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
        switch (lookup.Status)
        {
            case ExecutableActionGrantLookupStatus.Unknown:
            case ExecutableActionGrantLookupStatus.Revoked:
            case ExecutableActionGrantLookupStatus.Expired:
                return Deny<T>(PolicyEvaluationReasonCode.NoEffectiveGrant);
            case ExecutableActionGrantLookupStatus.Stale:
                return Deny<T>(PolicyEvaluationReasonCode.StaleFence);
            case ExecutableActionGrantLookupStatus.Error:
                return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
            case ExecutableActionGrantLookupStatus.Current when lookup.Grant is not null:
                break;
            default:
                return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
        }

        var grant = lookup.Grant!;
        if (grant is null || !IsValidGrant(grant))
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
        if (grant.State != ExecutableActionGrantState.Active ||
            grant.ExpiresAt <= _timeProvider.GetUtcNow())
            return Deny<T>(PolicyEvaluationReasonCode.NoEffectiveGrant);
        if (grant.Fence != invocation.Fence)
            return Deny<T>(PolicyEvaluationReasonCode.StaleFence);
        if (grant.Reference != invocation.GrantReference)
            return Deny<T>(PolicyEvaluationReasonCode.NoEffectiveGrant);
        if (!TryNormalizeIssuer(grant.Issuer, out var grantIssuer) ||
            !string.Equals(grantIssuer, issuer, StringComparison.Ordinal) ||
            !string.Equals(grant.ActorId, actorId, StringComparison.Ordinal) ||
            !string.Equals(grant.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(grant.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(grant.SessionId, invocation.SessionId, StringComparison.Ordinal) ||
            !string.Equals(grant.StepId, invocation.StepId, StringComparison.Ordinal) ||
            !string.Equals(grant.Purpose, invocation.Purpose, StringComparison.Ordinal) ||
            !grant.ActionIds.Any(actionId =>
                string.Equals(actionId, invocation.ActionId, StringComparison.Ordinal)))
            return Deny<T>(PolicyEvaluationReasonCode.NoEffectiveGrant);
        if (!IsMatchingPolicyBinding(invocation.PolicyBinding, grant, policyOptions))
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);

        var decision = policyProvider.Evaluate(
            policyOptions,
            new AgtPolicyEvaluationRequest(
                actorId,
                invocation.ActionId,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["tenant_id"] = grant.TenantId,
                    ["project_id"] = grant.ProjectId,
                    ["run_id"] = grant.RunId,
                    ["session_id"] = grant.SessionId,
                    ["step_id"] = grant.StepId,
                    ["grant_id"] = grant.Reference.GrantId,
                    ["grant_revision"] = grant.Reference.Revision,
                    ["purpose"] = grant.Purpose,
                    ["fence"] = grant.Fence
                }));
        if (decision.Outcome != PolicyEvaluationOutcome.Allow)
            return new ExecutableActionGuardResult<T>(
                decision.Outcome, decision.ReasonCode, EffectInvoked: false, default);

        var evidence = new PolicyEvaluationSessionPayload(
            grant.ActorId,
            grant.TenantId,
            grant.StepId,
            grant.Reference.GrantId,
            grant.Reference.Revision,
            grant.Purpose,
            invocation.ActionId,
            PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed,
            grant.Fence,
            decision.ProviderId,
            decision.AdapterVersion.ToString(),
            decision.OptionsSchemaVersion,
            decision.OptionsRevision);
        var append = new AppendSessionEvent(
            invocation.EventId,
            SessionsContractVersions.CurrentSchemaVersion,
            SessionsContractVersions.PolicyEvaluationEventVersion,
            evidence);

        SessionAppendResult appended;
        try
        {
            appended = await sessionsJournal.AppendAsync(
                invocation.Caller, invocation.SessionId, append, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionAccessDeniedException)
        {
            return Error<T>(PolicyEvaluationReasonCode.EvaluationFailed);
        }
        catch (SessionEventConflictException)
        {
            return Error<T>(PolicyEvaluationReasonCode.EvaluationFailed);
        }
        catch (SessionNotFoundException)
        {
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
        }
        catch (SessionPinnedProviderUnavailableException)
        {
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
        }

        if (appended.IsDuplicate)
            return Error<T>(PolicyEvaluationReasonCode.EvaluationFailed);

        var effectResult = await protectedEffect(cancellationToken).ConfigureAwait(false);
        return new ExecutableActionGuardResult<T>(
            PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed,
            EffectInvoked: true,
            effectResult);
    }

    private static bool IsValidInvocation(ExecutableActionInvocation invocation) =>
        AgtPolicyProvider.IsIdentifier(invocation.SessionId) &&
        AgtPolicyProvider.IsIdentifier(invocation.StepId) &&
        AgtPolicyProvider.IsIdentifier(invocation.ActionId) &&
        AgtPolicyProvider.IsIdentifier(invocation.Purpose) &&
        invocation.GrantReference is { } reference &&
        AgtPolicyProvider.IsIdentifier(reference.GrantId) &&
        AgtPolicyProvider.IsIdentifier(reference.Revision) &&
        invocation.Fence > 0 &&
        invocation.EventId != Guid.Empty;

    private static bool IsValidGrant(ValidatedExecutableActionGrant grant) =>
        grant.Reference is { } reference &&
        AgtPolicyProvider.IsIdentifier(reference.GrantId) &&
        AgtPolicyProvider.IsIdentifier(reference.Revision) &&
        Enum.IsDefined(grant.State) &&
        TryNormalizeIssuer(grant.Issuer, out _) &&
        AgtPolicyProvider.IsIdentifier(grant.ActorId) &&
        AgtPolicyProvider.IsIdentifier(grant.TenantId) &&
        AgtPolicyProvider.IsIdentifier(grant.ProjectId) &&
        AgtPolicyProvider.IsIdentifier(grant.RunId) &&
        AgtPolicyProvider.IsIdentifier(grant.SessionId) &&
        AgtPolicyProvider.IsIdentifier(grant.StepId) &&
        grant.ActionIds is not null &&
        !grant.ActionIds.IsEmpty &&
        grant.ActionIds.All(AgtPolicyProvider.IsIdentifier) &&
        AgtPolicyProvider.IsIdentifier(grant.Purpose) &&
        grant.Fence > 0 &&
        grant.ExpiresAt != default;

    private static bool IsMatchingPolicyBinding(
        PinnedProviderBinding? binding,
        ValidatedExecutableActionGrant grant,
        AgtPolicyProviderOptions options) =>
        binding is not null &&
        binding.RunId == grant.RunId &&
        binding.Seam == ProviderSeam.Policy &&
        binding.ProviderId == AgtPolicyProvider.ProviderId &&
        binding.AdapterVersion == AgtPolicyProvider.AdapterVersion &&
        binding.OptionsSchemaVersion == options.OptionsSchemaVersion &&
        binding.OptionsRevision == options.OptionsRevision &&
        binding.Hosting == ProviderHostingPattern.InProcess &&
        binding.Resource is { } resource &&
        resource.Seam == ProviderSeam.Policy &&
        resource.ProviderId == AgtPolicyProvider.ProviderId &&
        resource.ResourceId == options.ResourceId &&
        resource.Generation == options.ResourceGeneration &&
        binding.NegotiatedCapabilities.SetEquals(PolicyProviderCapabilities.All);

    private static bool TryGetCallerBindings(
        ClaimsPrincipal? principal,
        out string issuer,
        out string actorId,
        out string projectId,
        out string runId)
    {
        issuer = actorId = projectId = runId = string.Empty;
        if (principal is null)
            return false;

        var identities = principal.Identities.Where(identity => identity.IsAuthenticated).Take(2).ToArray();
        if (identities.Length != 1)
            return false;

        var claims = identities[0].Claims.ToArray();
        var subjects = claims.Where(claim => claim.Type == "sub").Select(claim => claim.Value).ToArray();
        var projects = claims.Where(claim => claim.Type == "project_id").ToArray();
        var runs = claims.Where(claim => claim.Type == "run_id").ToArray();
        if (subjects.Length != 1 || projects.Length != 1 || runs.Length != 1 ||
            !Guid.TryParse(subjects[0], out _) ||
            !AgtPolicyProvider.IsIdentifier(projects[0].Value) ||
            !AgtPolicyProvider.IsIdentifier(runs[0].Value) ||
            !TryNormalizeIssuer(
                claims.Single(claim => claim.Type == "sub").Issuer,
                out issuer) ||
            !TryNormalizeIssuer(projects[0].Issuer, out var projectIssuer) ||
            !TryNormalizeIssuer(runs[0].Issuer, out var runIssuer) ||
            !string.Equals(issuer, projectIssuer, StringComparison.Ordinal) ||
            !string.Equals(issuer, runIssuer, StringComparison.Ordinal))
            return false;

        actorId = subjects[0];
        projectId = projects[0].Value;
        runId = runs[0].Value;
        return true;
    }

    private static bool TryNormalizeIssuer(string? value, out string issuer)
    {
        issuer = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;

        issuer = uri.AbsoluteUri;
        return true;
    }

    private static ExecutableActionGuardResult<T> Deny<T>(PolicyEvaluationReasonCode reason) =>
        new(PolicyEvaluationOutcome.Deny, reason, EffectInvoked: false, default);

    private static ExecutableActionGuardResult<T> Error<T>(PolicyEvaluationReasonCode reason) =>
        new(PolicyEvaluationOutcome.Error, reason, EffectInvoked: false, default);
}
