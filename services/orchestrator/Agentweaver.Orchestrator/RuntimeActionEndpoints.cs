using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Authorizes runtime actions and verifies their grants and policy receipts before the host performs native effects.
/// </summary>
internal sealed class RuntimeActionOwner(
    RuntimeRegistrationOwner registrations,
    ProjectsRunSelectionClient projects,
    ExecutableActionGrantOwnerStore grants,
    ExecutableActionGuard guard,
    IExecutableActionPolicyEvaluationJournal journal,
    ProviderCatalog catalog,
    ProviderResolver resolver,
    AgtPolicyProvider policy,
    IServiceProvider services,
    OrchestratorOptions options)
{
    /// <summary>
    /// Evaluates and admits a runtime action against the current registration, selection, grant, and policy.
    /// </summary>
    /// <param name="context">The authenticated runtime request context.</param>
    /// <param name="request">The action ID, event ID, and registration binding to authorize.</param>
    /// <param name="cancellationToken">Cancels owner, database, and policy-journal work.</param>
    /// <returns>An admission containing the allow decision, grant reference, and policy receipt ID.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when registration, cost budget, selection, grant, or policy does not allow the action.</exception>
    /// <remarks>For tool invocations, the guarded callback writes the durable reservation before the SDK effect is performed by AgentHost.</remarks>
    internal async Task<RuntimeActionAdmission> AuthorizeAsync(
        HttpContext context, RuntimeActionRequest request, CancellationToken cancellationToken,
        byte[]? content = null)
    {
        RuntimeActionContract.Validate(request);
        var registration = await registrations.ReadCurrentAsync(
            context, request.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        RequireRequest(registration, request);
        var binding = registration.Binding;
        var actionStepId = RuntimeActionContract.RequireActionStepId(binding);
        await RequireCostTurnEnabledAsync(context, registration, request,
            RuntimeFixedCreditGuardStage.ActionAuthorize, cancellationToken).ConfigureAwait(false);
        var accepted = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        var policyOptions = services.GetService<AgtPolicyProviderOptions>()
            ?? throw new RuntimeAuthorizationException("runtime_action_policy_unavailable");
        var policyBinding = await SourceControlEndpoints.ResolvePolicyBindingAsync(
            accepted.Selection.Snapshot, binding.RunId, catalog, resolver, policy,
            policyOptions, cancellationToken).ConfigureAwait(false);
        // Decide before the grant is issued whether Guardrails checks apply. If content is required but missing,
        // this throws now, so a rejected request leaves no grant behind.
        var guardrails = RuntimeGuardrailsGate.Prepare(services, request, accepted, content);
        var grant = await IssueOrRecordNumericLimitDenialAsync(
            context, registration, request, cancellationToken).ConfigureAwait(false);
        await RequireCurrentAsync(context, registration, cancellationToken).ConfigureAwait(false);
        // Guardrails checks run before the guarded callback, so withheld content never gets a policy receipt.
        // The authority check re-reads the registration and the accepted selection from their owners, and
        // compares them with the values used for this admission.
        if (guardrails is not null)
            await RuntimeGuardrailsGate.RequireAllowedAsync(services, policy, guardrails,
                RuntimeGuardrailsGate.AuthorityCheck(
                    token => registrations.ReadCurrentAsync(context, registration.RuntimeInstanceId, token),
                    token => projects.ReadAcceptedSelectionWithAuthorityAsync(
                        context, binding.ProjectId, binding.RunId, token),
                    registration, RuntimeGuardrailsGate.SelectionHash(accepted.Selection)),
                registration, accepted, request, grant, policyBinding, policyOptions,
                cancellationToken).ConfigureAwait(false);
        var invocation = new ExecutableActionInvocation(context.User, binding.SessionId, actionStepId,
            request.ActionId, RuntimeActionContract.Purpose, grant, binding.ExecutionFence, policyBinding, request.EventId);
        // The guarded callback admits dispatch; the native SDK effect remains in AgentHost.
        var evaluated = await guard.ExecuteAsync(invocation,
            async token =>
            {
                await RequireCurrentAsync(context, registration, token).ConfigureAwait(false);
                if (request.IsToolInvocation)
                    await IssueOrRecordNumericLimitDenialAsync(
                        context, registration, request, token, reserveToolInvocation: true).ConfigureAwait(false);
                await RequireCurrentAsync(context, registration, token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        await RequireCurrentAsync(context, registration, cancellationToken).ConfigureAwait(false);
        return new(1, request, grant.GrantId, grant.Revision, request.EventId,
            evaluated.Outcome, evaluated.ReasonCode);
    }

    /// <summary>
    /// Revalidates an admission against current registration, grant, receipt, selection, and policy state.
    /// </summary>
    /// <param name="context">The authenticated runtime request context.</param>
    /// <param name="admission">The previously returned runtime action admission.</param>
    /// <param name="cancellationToken">Cancels owner and journal reads.</param>
    /// <returns>The same admission after current authority is confirmed.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the admission or any current grant, receipt, selection, or policy check is invalid.</exception>
    internal async Task<RuntimeActionAdmission> VerifyAsync(
        HttpContext context, RuntimeActionAdmission admission, CancellationToken cancellationToken)
    {
        RuntimeActionContract.Validate(admission.Request);
        if (admission.ContractVersion != 1 || admission.PolicyReceiptId != admission.Request.EventId ||
            admission.GrantId != admission.Request.EventId.ToString("N") || admission.GrantRevision != "1" ||
            admission.Outcome != PolicyEvaluationOutcome.Allow || admission.ReasonCode != PolicyEvaluationReasonCode.Allowed)
            throw new RuntimeAuthorizationException("runtime_action_admission_invalid");
        var registration = await registrations.ReadCurrentAsync(
            context, admission.Request.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        RequireRequest(registration, admission.Request);
        await RequireCostTurnEnabledAsync(context, registration, admission.Request,
            RuntimeFixedCreditGuardStage.ActionVerify, cancellationToken).ConfigureAwait(false);
        var reference = await IssueOrRecordNumericLimitDenialAsync(
            context, registration, admission.Request, cancellationToken).ConfigureAwait(false);
        if (reference.GrantId != admission.GrantId || reference.Revision != admission.GrantRevision)
            throw new RuntimeAuthorizationException("runtime_action_admission_invalid");
        if (admission.Request.IsToolInvocation)
            await grants.RequireToolInvocationReservationAsync(
                registration, admission.Request, cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        var actionStepId = RuntimeActionContract.RequireActionStepId(binding);
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        var receipt = await grants.ValidateReceiptAdmissionAsync(actor, binding.ProjectId, binding.RunId,
            admission.PolicyReceiptId, cancellationToken).ConfigureAwait(false);
        if (receipt.Identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
            receipt.Evidence.GrantId != admission.GrantId || receipt.Evidence.GrantRevision != admission.GrantRevision ||
            receipt.Evidence.StepId != actionStepId || receipt.Evidence.ActionId != admission.Request.ActionId ||
            receipt.Evidence.Purpose != RuntimeActionContract.Purpose ||
            receipt.Evidence.Fence != binding.ExecutionFence || receipt.Evidence.Outcome != PolicyEvaluationOutcome.Allow)
            throw new RuntimeAuthorizationException("runtime_action_admission_invalid");
        var current = await grants.GetCurrentAsync(reference, cancellationToken).ConfigureAwait(false);
        if (current.Status != ExecutableActionGrantLookupStatus.Current || current.Grant is not { } grant)
            throw new RuntimeAuthorizationException("runtime_action_grant_unavailable");
        var policyOptions = services.GetService<AgtPolicyProviderOptions>()
            ?? throw new RuntimeAuthorizationException("runtime_action_policy_unavailable");
        var accepted = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        _ = await SourceControlEndpoints.ResolvePolicyBindingAsync(
            accepted.Selection.Snapshot, binding.RunId, catalog, resolver, policy,
            policyOptions, cancellationToken).ConfigureAwait(false);
        var decision = policy.Evaluate(policyOptions, new AgtPolicyEvaluationRequest(
            grant.ActorId, admission.Request.ActionId, new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tenant_id"] = grant.TenantId, ["project_id"] = grant.ProjectId,
                ["run_id"] = grant.RunId, ["session_id"] = grant.SessionId, ["step_id"] = grant.StepId,
                ["grant_id"] = grant.Reference.GrantId, ["grant_revision"] = grant.Reference.Revision,
                ["purpose"] = grant.Purpose, ["fence"] = grant.Fence
            }));
        if (decision.Outcome != PolicyEvaluationOutcome.Allow)
            throw new RuntimeAuthorizationException("runtime_action_denied");
        var recorded = await journal.AppendReceiptAsync(
            receipt.Identity, admission.PolicyReceiptId, cancellationToken).ConfigureAwait(false);
        if (!recorded.IsDuplicate)
            throw new RuntimeAuthorizationException("runtime_action_journal_ack_missing");
        await RequireCurrentAsync(context, registration, cancellationToken).ConfigureAwait(false);
        return admission;
    }

    /// <summary>
    /// Rejects the action when its runtime registration changed between authorization steps.
    /// </summary>
    private async Task RequireCurrentAsync(
        HttpContext context, RuntimeRegistration expected, CancellationToken cancellationToken)
    {
        if (await registrations.ReadCurrentAsync(
                context, expected.RuntimeInstanceId, cancellationToken).ConfigureAwait(false) != expected)
            throw new RuntimeAuthorizationException("runtime_action_registration_changed");
    }

    private async Task<ExecutableActionGrantReference> IssueOrRecordNumericLimitDenialAsync(
        HttpContext context,
        RuntimeRegistration registration,
        RuntimeActionRequest request,
        CancellationToken cancellationToken,
        bool reserveToolInvocation = false)
    {
        try
        {
            return await grants.IssueRuntimeActionGrantAsync(
                context, registration, request, cancellationToken, reserveToolInvocation).ConfigureAwait(false);
        }
        catch (RuntimeNumericLimitDeniedException denial)
        {
            if (denial.SendStateEmpty)
                await grants.RecordNumericLimitDenialAsync(
                    context, registration, request, denial, cancellationToken).ConfigureAwait(false);
            throw new RuntimeAuthorizationException(denial.Code);
        }
    }

    /// <summary>
    /// Requires the request revision and execution fence to match the current registration.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the request uses a stale registration or fence.</exception>
    private static void RequireRequest(RuntimeRegistration registration, RuntimeActionRequest request)
    {
        if (registration.Revision != request.RegistrationRevision ||
            registration.Binding.ExecutionFence != request.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_action_binding_invalid");
    }

    /// <summary>
    /// Reads the current fixed-credit denial receipt for a verified registration.
    /// </summary>
    /// <exception cref="CoordinationException">Thrown when the requested guard record is unknown.</exception>
    internal async Task<RuntimeFixedCreditGuardReadResult> ReadFixedCreditGuardAsync(
        HttpContext context, RuntimeFixedCreditGuardReadRequest request, CancellationToken cancellationToken)
    {
        RuntimeFixedCreditGuardContract.ValidateReadRequest(request);
        if (request.Authorization is not null)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_request_invalid");
        var store = RequireFixedCreditGuardStore(services);
        var registration = await registrations.ReadCurrentAsync(
            context, request.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        if (registration.Revision != request.RegistrationRevision ||
            registration.Binding.ExecutionFence != request.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_conflict");
        return await store.ExecuteLockedAsync(registration.RuntimeInstanceId,
            (connection, transaction, token) => registrations.ExecuteCurrentAsync(
                context, registration.RuntimeInstanceId, async (current, currentToken) =>
                {
                    if (current != registration)
                        throw new RuntimeAuthorizationException("runtime_registration_changed");
                    var result = await store.ReadFixedCreditGuardResultWithinTransactionAsync(
                        connection, transaction, current, request, includeEvidence: false,
                        grant: null, sourceReceipt: null, currentToken).ConfigureAwait(false)
                        ?? throw new CoordinationException(
                            "runtime_fixed_credit_guard_unknown", StatusCodes.Status404NotFound);
                    RuntimeFixedCreditGuardContract.ValidateReadResult(result, current, request);
                    await transaction.CommitAsync(currentToken).ConfigureAwait(false);
                    return result;
                }, token), cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RuntimeNumericLimitDenialReadResult> ReadNumericLimitDenialAsync(
        HttpContext context,
        RuntimeNumericLimitDenialReadRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeNumericLimitDenialReadContract.ValidateRequest(request);
        var registration = await registrations.ReadCurrentAsync(
            context, request.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        if (registration.Revision != request.RegistrationRevision ||
            binding.ExecutionFence != request.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        var accepted = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        if (actor.Issuer != binding.ActorIssuer || actor.Subject != binding.ActorId ||
            accepted.Authorization.ActorId != binding.ActorId ||
            accepted.Authorization.Issuer != binding.ActorIssuer ||
            accepted.Authorization.TenantId != binding.TenantId ||
            accepted.Selection.ProjectId != binding.ProjectId ||
            accepted.Selection.RunId != binding.RunId ||
            accepted.Selection.ProjectRevision != binding.ProjectRevision ||
            accepted.Selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            accepted.Selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            accepted.Selection.ContextRevision != binding.ContextRevision ||
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
                accepted.Selection.Snapshot.GetRawText())) != binding.AcceptedSelectionHash)
            throw new RuntimeAuthorizationException("runtime_numeric_limit_denial_conflict");
        await RequireCurrentAsync(context, registration, cancellationToken).ConfigureAwait(false);
        return await grants.ReadNumericLimitDenialAsync(
            registration, request, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<RuntimeReplacementComparisonBlocker> CompareReplacementAsync(
        HttpContext context,
        RuntimeReplacementComparisonRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeReplacementComparisonContract.ValidateRequest(request);
        var registration = await registrations.ReadCurrentAsync(
            context, request.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        if (registration.Revision != request.RegistrationRevision ||
            binding.ExecutionFence != request.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_replacement_comparison_conflict");
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        var accepted = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        if (actor.Issuer != binding.ActorIssuer || actor.Subject != binding.ActorId ||
            accepted.Authorization.ActorId != binding.ActorId ||
            accepted.Authorization.Issuer != binding.ActorIssuer ||
            accepted.Authorization.TenantId != binding.TenantId ||
            accepted.Selection.ProjectId != binding.ProjectId ||
            accepted.Selection.RunId != binding.RunId ||
            accepted.Selection.ProjectRevision != binding.ProjectRevision ||
            accepted.Selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            accepted.Selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            accepted.Selection.ContextRevision != binding.ContextRevision ||
            RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
                accepted.Selection.Snapshot.GetRawText())) != binding.AcceptedSelectionHash)
            throw new RuntimeAuthorizationException("runtime_replacement_comparison_conflict");
        await RequireCurrentAsync(context, registration, cancellationToken).ConfigureAwait(false);
        return await grants.ReadReplacementComparisonBlockerAsync(
            registration, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Prevents hosted model turns when the hard-credit limit is zero and records the denial when a store is available.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the action is not admitted or the configured cost budget is exhausted.</exception>
    internal async Task RequireCostTurnEnabledAsync(
        HttpContext context, RuntimeRegistration registration, RuntimeActionRequest request,
        RuntimeFixedCreditGuardStage stage, CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        RuntimeActionContract.RequireNativeDispatchAdmission(binding, request);
        if (request.ActionId != "model.turn" ||
            binding.ModelSourceMode != ModelSourceMode.HostedCopilot ||
            binding.CopilotHardCreditLimit is not 0m)
            return;

        var store = services.GetService<RuntimeUsageSourceStore>();
        if (store is not null)
        {
            var receipt = RuntimeFixedCreditGuardContract.Seal(new RuntimeFixedCreditGuardReceipt(
                1, stage, RuntimeFixedCreditGuardCode.HardLimitZero, registration,
                RuntimeContractValidation.RegistrationHash(registration), request.EventId,
                RuntimeActionContract.Hash(request), null, null, null, DateTimeOffset.UtcNow, string.Empty));
            await store.ExecuteLockedAsync(registration.RuntimeInstanceId,
                (connection, transaction, token) => registrations.ExecuteCurrentAsync(
                    context, registration.RuntimeInstanceId, async (current, currentToken) =>
                    {
                        if (current != registration)
                            throw new RuntimeAuthorizationException("runtime_registration_changed");
                        _ = await store.RecordFixedCreditGuardWithinTransactionAsync(
                            connection, transaction, current, receipt, grant: null,
                            sourceReceipt: null, currentToken).ConfigureAwait(false);
                        await transaction.CommitAsync(currentToken).ConfigureAwait(false);
                        return true;
                    }, token), cancellationToken).ConfigureAwait(false);
        }
        throw new RuntimeAuthorizationException("runtime_cost_budget_exhausted");
    }

    /// <summary>
    /// Returns the usage store required to persist fixed-credit guard evidence.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when usage support is not configured.</exception>
    internal static RuntimeUsageSourceStore RequireFixedCreditGuardStore(IServiceProvider services) =>
        services.GetService<RuntimeUsageSourceStore>()
        ?? throw new RuntimeAuthorizationException("runtime_fixed_credit_guard_unavailable");
}

/// <summary>
/// Maps authenticated endpoints for runtime action admission, verification, and denial reads.
/// </summary>
public static class RuntimeActionEndpoints
{
    // JSON escaping can expand the 256 KiB content limit up to six times, plus the request envelope.
    private const long ContentRequestBodyLimit = 2 * 1024 * 1024;

    /// <summary>
    /// Adds the protected runtime action routes to the endpoint builder.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    public static void MapRuntimeActionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/runtime/actions/authorize",
            (HttpContext context, RuntimeActionRequest request, [FromServices] RuntimeActionOwner owner,
                CancellationToken token) => ExecuteAsync(context, () => owner.AuthorizeAsync(context, request, token)))
            .RequireAuthorization();
        // Model-turn admission with the UTF-8 content that the platform Guardrails checks inspect.
        endpoints.MapPost("/internal/runtime/actions/authorize-content",
            (HttpContext context, RuntimeActionContentRequest request, [FromServices] RuntimeActionOwner owner,
                CancellationToken token) => ExecuteAsync(context, () =>
                {
                    // Encode the content as strict UTF-8, check its size, and confirm its digest matches the
                    // request's input hash. Then run the normal admission with the content attached.
                    var content = RuntimeActionContract.ValidateContent(request);
                    return owner.AuthorizeAsync(context, request.Request, token, content);
                }))
            .WithMetadata(new RequestSizeLimitAttribute(ContentRequestBodyLimit))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/actions/verify",
            (HttpContext context, RuntimeActionAdmission admission, [FromServices] RuntimeActionOwner owner,
                CancellationToken token) => ExecuteAsync(context, () => owner.VerifyAsync(context, admission, token)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/actions/credit-denials/read",
            (HttpContext context, RuntimeFixedCreditGuardReadRequest request,
                [FromServices] RuntimeActionOwner owner, CancellationToken token) =>
                ExecuteAsync(context, () => owner.ReadFixedCreditGuardAsync(context, request, token)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/actions/numeric-denials/read",
            (HttpContext context, RuntimeNumericLimitDenialReadRequest request,
                [FromServices] RuntimeActionOwner owner, CancellationToken token) =>
                ExecuteAsync(context, () => owner.ReadNumericLimitDenialAsync(context, request, token)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/execution/replacements/compare",
            (HttpContext context, RuntimeReplacementComparisonRequest request,
                [FromServices] RuntimeActionOwner owner, CancellationToken token) =>
                ExecuteAsync(context, () => owner.CompareReplacementAsync(context, request, token)))
            .RequireAuthorization();
    }

    /// <summary>
    /// Executes an action-owner call and maps expected authorization, coordination, and transport failures to no-store responses.
    /// </summary>
    private static async Task<IResult> ExecuteAsync<T>(HttpContext context, Func<Task<T>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Ok(await action().ConfigureAwait(false));
        }
        catch (CoordinationException failure)
        {
            return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode);
        }
        catch (RuntimeAuthorizationException failure)
        {
            // 428 Precondition Required: checks apply but the request had no content. The runtime must resend the
            // turn to the content endpoint; this is not a denial.
            // 503 Service Unavailable: a dependency (cost guard, classifier, journal, or owner) could not answer,
            // or the classifier timed out. A retry may succeed.
            // 403 Forbidden: every other failure is a final denial, including content that the checks blocked.
            var status = failure.Code == RuntimeGuardrailsGate.ContentRequiredCode
                ? StatusCodes.Status428PreconditionRequired
                : failure.Code == "runtime_fixed_credit_guard_unavailable" ||
                RuntimeGuardrailsGate.IsTransient(failure.Code)
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status403Forbidden;
            return Results.Json(new { error = failure.Code }, statusCode: status);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "runtime_action_invalid" });
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "runtime_action_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { error = "runtime_action_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
