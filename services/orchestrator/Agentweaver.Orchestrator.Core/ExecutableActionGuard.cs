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

public enum ExecutableActionSourceReceiptWriteStatus
{
    Unavailable,
    Stored,
    Duplicate,
    Rejected
}

public sealed record ExecutableActionSourceReceiptWriteResult(
    ExecutableActionSourceReceiptWriteStatus Status);

public sealed class ExecutableActionSourceReceipt
{
    internal ExecutableActionSourceReceipt(
        Guid receiptId,
        ValidatedExecutableActionGrant grant,
        PinnedProviderBinding policyBinding,
        string sessionId,
        string stepId,
        string actionId,
        string purpose,
        long fence,
        DateTimeOffset createdAt) =>
        (ReceiptId, Grant, PolicyBinding, SessionId, StepId, ActionId, Purpose, Fence, CreatedAt) =
        (receiptId, grant, policyBinding, sessionId, stepId, actionId, purpose, fence, createdAt);

    public Guid ReceiptId { get; }
    public ValidatedExecutableActionGrant Grant { get; }
    public PinnedProviderBinding PolicyBinding { get; }
    public string SessionId { get; }
    public string StepId { get; }
    public string ActionId { get; }
    public string Purpose { get; }
    public long Fence { get; }
    public DateTimeOffset CreatedAt { get; }
}

/// <summary>
/// Owner-local immutable source-receipt store. It is not an Events &amp; Sessions writer, and a
/// <see cref="ExecutableActionSourceReceiptWriteStatus.Stored"/> result never replaces the existing
/// Sessions append guard or the guard's fresh grant and fence checks before an effect.
/// </summary>
public interface IExecutableActionSourceReceiptWriter
{
    Task<ExecutableActionSourceReceiptWriteResult> StoreAsync(
        ExecutableActionSourceReceipt receipt,
        CancellationToken cancellationToken = default);
}

public sealed record ExecutableActionPolicyEvaluationReceipt(
    Guid ReceiptId,
    string SessionId,
    string StepId,
    ExecutableActionGrantReference GrantReference,
    string Purpose,
    string ActionId,
    PolicyEvaluationOutcome Outcome,
    PolicyEvaluationReasonCode ReasonCode,
    long Fence,
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision);

public interface IExecutableActionPolicyEvaluationReceiptWriter
{
    Task<ExecutableActionSourceReceiptWriteResult> StoreAsync(
        ExecutableActionPolicyEvaluationReceipt receipt,
        CancellationToken cancellationToken = default);
}

public sealed record ExecutableActionPolicyEvaluationAppendResult(bool IsDuplicate);

public interface IExecutableActionPolicyEvaluationJournal
{
    Task<ExecutableActionPolicyEvaluationAppendResult> AppendReceiptAsync(
        SessionIdentity identity,
        Guid receiptId,
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
    IExecutableActionPolicyEvaluationJournal? policyJournal,
    IExecutableActionGrantOwnerLookup? grantOwnerLookup = null,
    TimeProvider? timeProvider = null,
    IExecutableActionSourceReceiptWriter? sourceReceiptWriter = null,
    IExecutableActionPolicyEvaluationReceiptWriter? evaluationReceiptWriter = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ExecutableActionGuardResult<T>> ExecuteAsync<T>(
        ExecutableActionInvocation invocation,
        Func<CancellationToken, Task<T>> protectedEffect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(protectedEffect);

        if (policyProvider is null || policyOptions is null || policyJournal is null)
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

        ExecutableActionGrantLookupResult? lookup;
        try
        {
            lookup = await grantOwnerLookup.GetCurrentAsync(
                invocation.GrantReference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
        }
        if (lookup is null)
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
        switch (lookup.Status)
        {
            case ExecutableActionGrantLookupStatus.Unknown:
            case ExecutableActionGrantLookupStatus.Revoked:
            case ExecutableActionGrantLookupStatus.Expired:
                return await RecordNonAllowAsync<T>(
                    invocation,
                    projectId,
                    runId,
                    PolicyEvaluationOutcome.Deny,
                    PolicyEvaluationReasonCode.NoEffectiveGrant,
                    policyOptions,
                    cancellationToken).ConfigureAwait(false);
            case ExecutableActionGrantLookupStatus.Stale:
                return await RecordNonAllowAsync<T>(
                    invocation,
                    projectId,
                    runId,
                    PolicyEvaluationOutcome.Deny,
                    PolicyEvaluationReasonCode.StaleFence,
                    policyOptions,
                    cancellationToken).ConfigureAwait(false);
            case ExecutableActionGrantLookupStatus.Error:
                return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
            case ExecutableActionGrantLookupStatus.Current when lookup.Grant is not null:
                break;
            default:
                return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);
        }

        var grant = lookup.Grant!;
        var grantError = ValidateGrant(
            invocation, grant, issuer, actorId, projectId, runId,
            expectedGrant: null, options: policyOptions);
        if (grantError is not null)
            return AuthorizationResult<T>(grantError.Value);

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
            return await RecordNonAllowAsync<T>(
                invocation,
                projectId,
                runId,
                decision.Outcome,
                decision.ReasonCode,
                decision.ProviderId,
                decision.AdapterVersion.ToString(),
                decision.OptionsSchemaVersion,
                decision.OptionsRevision,
                cancellationToken).ConfigureAwait(false);

        if (sourceReceiptWriter is null)
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);

        ExecutableActionSourceReceiptWriteResult? receiptWrite;
        try
        {
            receiptWrite = await sourceReceiptWriter.StoreAsync(
                new ExecutableActionSourceReceipt(
                    invocation.EventId,
                    grant,
                    invocation.PolicyBinding!,
                    invocation.SessionId,
                    invocation.StepId,
                    invocation.ActionId,
                    invocation.Purpose,
                    invocation.Fence,
                    _timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Error<T>(PolicyEvaluationReasonCode.EvaluationFailed);
        }

        if (receiptWrite is null)
            return Error<T>(PolicyEvaluationReasonCode.EvaluationFailed);
        if (receiptWrite.Status != ExecutableActionSourceReceiptWriteStatus.Stored)
            return Error<T>(receiptWrite.Status == ExecutableActionSourceReceiptWriteStatus.Unavailable
                ? PolicyEvaluationReasonCode.ProviderUnavailable
                : PolicyEvaluationReasonCode.EvaluationFailed);

        var authorityError = await RevalidateCurrentGrantAsync(
            invocation, issuer, actorId, projectId, runId, grant, policyOptions, cancellationToken)
            .ConfigureAwait(false);
        if (authorityError is not null)
            return AuthorizationResult<T>(authorityError.Value);

        ExecutableActionPolicyEvaluationAppendResult appended;
        try
        {
            appended = await policyJournal.AppendReceiptAsync(
                new SessionIdentity(projectId, runId, invocation.SessionId),
                invocation.EventId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Error<T>(PolicyEvaluationReasonCode.EvaluationFailed);
        }

        if (appended.IsDuplicate)
            return Error<T>(PolicyEvaluationReasonCode.EvaluationFailed);

        authorityError = await RevalidateCurrentGrantAsync(
            invocation, issuer, actorId, projectId, runId, grant, policyOptions, cancellationToken)
            .ConfigureAwait(false);
        if (authorityError is not null)
            return AuthorizationResult<T>(authorityError.Value);

        var effectResult = await protectedEffect(cancellationToken).ConfigureAwait(false);
        return new ExecutableActionGuardResult<T>(
            PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed,
            EffectInvoked: true,
            effectResult);
    }

    private async Task<ExecutableActionGuardResult<T>> RecordNonAllowAsync<T>(
        ExecutableActionInvocation invocation,
        string projectId,
        string runId,
        PolicyEvaluationOutcome outcome,
        PolicyEvaluationReasonCode reasonCode,
        AgtPolicyProviderOptions options,
        CancellationToken cancellationToken) =>
        await RecordNonAllowAsync<T>(
            invocation,
            projectId,
            runId,
            outcome,
            reasonCode,
            AgtPolicyProvider.ProviderId,
            AgtPolicyProvider.AdapterVersion.ToString(),
            options.OptionsSchemaVersion,
            options.OptionsRevision,
            cancellationToken).ConfigureAwait(false);

    private async Task<ExecutableActionGuardResult<T>> RecordNonAllowAsync<T>(
        ExecutableActionInvocation invocation,
        string projectId,
        string runId,
        PolicyEvaluationOutcome outcome,
        PolicyEvaluationReasonCode reasonCode,
        string providerId,
        string adapterVersion,
        int optionsSchemaVersion,
        string optionsRevision,
        CancellationToken cancellationToken)
    {
        if (outcome is not (PolicyEvaluationOutcome.Deny or PolicyEvaluationOutcome.Error) ||
            evaluationReceiptWriter is null ||
            policyJournal is null ||
            policyOptions is null ||
            !IsMatchingPolicyBinding(invocation.PolicyBinding, runId, policyOptions))
            return Error<T>(PolicyEvaluationReasonCode.ProviderUnavailable);

        ExecutableActionSourceReceiptWriteResult? stored;
        try
        {
            stored = await evaluationReceiptWriter.StoreAsync(
                new ExecutableActionPolicyEvaluationReceipt(
                    invocation.EventId,
                    invocation.SessionId,
                    invocation.StepId,
                    invocation.GrantReference,
                    invocation.Purpose,
                    invocation.ActionId,
                    outcome,
                    reasonCode,
                    invocation.Fence,
                    providerId,
                    adapterVersion,
                    optionsSchemaVersion,
                    optionsRevision),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Error<T>(PolicyEvaluationReasonCode.EvaluationFailed);
        }

        if (stored?.Status != ExecutableActionSourceReceiptWriteStatus.Stored)
            return Error<T>(stored?.Status == ExecutableActionSourceReceiptWriteStatus.Unavailable
                ? PolicyEvaluationReasonCode.ProviderUnavailable
                : PolicyEvaluationReasonCode.EvaluationFailed);

        ExecutableActionPolicyEvaluationAppendResult appended;
        try
        {
            appended = await policyJournal.AppendReceiptAsync(
                new SessionIdentity(projectId, runId, invocation.SessionId),
                invocation.EventId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Error<T>(PolicyEvaluationReasonCode.EvaluationFailed);
        }

        return appended.IsDuplicate
            ? Error<T>(PolicyEvaluationReasonCode.EvaluationFailed)
            : new ExecutableActionGuardResult<T>(outcome, reasonCode, EffectInvoked: false, default);
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

    private async Task<PolicyEvaluationReasonCode?> RevalidateCurrentGrantAsync(
        ExecutableActionInvocation invocation,
        string issuer,
        string actorId,
        string projectId,
        string runId,
        ValidatedExecutableActionGrant expectedGrant,
        AgtPolicyProviderOptions options,
        CancellationToken cancellationToken)
    {
        if (!TryGetCallerBindings(
                invocation.Caller, out var currentIssuer, out var currentActorId,
                out var currentProjectId, out var currentRunId) ||
            !string.Equals(currentIssuer, issuer, StringComparison.Ordinal) ||
            !string.Equals(currentActorId, actorId, StringComparison.Ordinal) ||
            !string.Equals(currentProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(currentRunId, runId, StringComparison.Ordinal))
            return PolicyEvaluationReasonCode.NoEffectiveGrant;

        ExecutableActionGrantLookupResult? lookup;
        try
        {
            lookup = await grantOwnerLookup!.GetCurrentAsync(
                invocation.GrantReference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return PolicyEvaluationReasonCode.ProviderUnavailable;
        }

        if (lookup is null)
            return PolicyEvaluationReasonCode.ProviderUnavailable;

        switch (lookup.Status)
        {
            case ExecutableActionGrantLookupStatus.Unknown:
            case ExecutableActionGrantLookupStatus.Revoked:
            case ExecutableActionGrantLookupStatus.Expired:
                return PolicyEvaluationReasonCode.NoEffectiveGrant;
            case ExecutableActionGrantLookupStatus.Stale:
                return PolicyEvaluationReasonCode.StaleFence;
            case ExecutableActionGrantLookupStatus.Error:
                return PolicyEvaluationReasonCode.ProviderUnavailable;
            case ExecutableActionGrantLookupStatus.Current when lookup.Grant is not null:
                return ValidateGrant(
                    invocation, lookup.Grant, issuer, actorId, projectId, runId, expectedGrant, options);
            default:
                return PolicyEvaluationReasonCode.ProviderUnavailable;
        }
    }

    private PolicyEvaluationReasonCode? ValidateGrant(
        ExecutableActionInvocation invocation,
        ValidatedExecutableActionGrant? grant,
        string issuer,
        string actorId,
        string projectId,
        string runId,
        ValidatedExecutableActionGrant? expectedGrant,
        AgtPolicyProviderOptions? options = null)
    {
        if (grant is null || !IsValidGrant(grant))
            return PolicyEvaluationReasonCode.ProviderUnavailable;
        if (grant.State != ExecutableActionGrantState.Active ||
            grant.ExpiresAt <= _timeProvider.GetUtcNow())
            return PolicyEvaluationReasonCode.NoEffectiveGrant;
        if (grant.Fence != invocation.Fence)
            return PolicyEvaluationReasonCode.StaleFence;

        if (!TryNormalizeIssuer(grant.Issuer, out var grantIssuer) ||
            !string.Equals(grantIssuer, issuer, StringComparison.Ordinal) ||
            !string.Equals(grant.ActorId, actorId, StringComparison.Ordinal) ||
            !string.Equals(grant.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(grant.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(grant.SessionId, invocation.SessionId, StringComparison.Ordinal) ||
            !string.Equals(grant.StepId, invocation.StepId, StringComparison.Ordinal) ||
            !string.Equals(grant.Purpose, invocation.Purpose, StringComparison.Ordinal) ||
            grant.Reference != invocation.GrantReference ||
            !grant.ActionIds.Contains(invocation.ActionId))
            return PolicyEvaluationReasonCode.NoEffectiveGrant;

        if (options is not null &&
            !IsMatchingPolicyBinding(invocation.PolicyBinding, runId, options))
            return PolicyEvaluationReasonCode.ProviderUnavailable;

        if (expectedGrant is not null && !SameGrant(grant, expectedGrant))
            return PolicyEvaluationReasonCode.NoEffectiveGrant;

        return null;
    }

    private static bool SameGrant(
        ValidatedExecutableActionGrant current,
        ValidatedExecutableActionGrant expected) =>
        current.Reference == expected.Reference &&
        current.State == expected.State &&
        string.Equals(current.Issuer, expected.Issuer, StringComparison.Ordinal) &&
        string.Equals(current.ActorId, expected.ActorId, StringComparison.Ordinal) &&
        string.Equals(current.TenantId, expected.TenantId, StringComparison.Ordinal) &&
        string.Equals(current.ProjectId, expected.ProjectId, StringComparison.Ordinal) &&
        string.Equals(current.RunId, expected.RunId, StringComparison.Ordinal) &&
        string.Equals(current.SessionId, expected.SessionId, StringComparison.Ordinal) &&
        string.Equals(current.StepId, expected.StepId, StringComparison.Ordinal) &&
        current.ActionIds.SetEquals(expected.ActionIds) &&
        string.Equals(current.Purpose, expected.Purpose, StringComparison.Ordinal) &&
        current.Fence == expected.Fence &&
        current.ExpiresAt == expected.ExpiresAt;

    private static ExecutableActionGuardResult<T> AuthorizationResult<T>(
        PolicyEvaluationReasonCode reason) =>
        reason is PolicyEvaluationReasonCode.NoEffectiveGrant or PolicyEvaluationReasonCode.StaleFence
            ? Deny<T>(reason)
            : Error<T>(reason);

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
        string runId,
        AgtPolicyProviderOptions options) =>
        binding is not null &&
        binding.RunId == runId &&
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
