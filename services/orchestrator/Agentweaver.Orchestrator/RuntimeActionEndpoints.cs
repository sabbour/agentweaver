using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Orchestrator;

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
    internal async Task<RuntimeActionAdmission> AuthorizeAsync(
        HttpContext context, RuntimeActionRequest request, CancellationToken cancellationToken)
    {
        RuntimeActionContract.Validate(request);
        var registration = await registrations.ReadCurrentAsync(
            context, request.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        RequireRequest(registration, request);
        var binding = registration.Binding;
        RequireCostTurnEnabled(binding, request);
        var accepted = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        var policyOptions = services.GetService<AgtPolicyProviderOptions>()
            ?? throw new RuntimeAuthorizationException("runtime_action_policy_unavailable");
        var policyBinding = await SourceControlEndpoints.ResolvePolicyBindingAsync(
            accepted.Selection.Snapshot, binding.RunId, catalog, resolver, policy,
            policyOptions, cancellationToken).ConfigureAwait(false);
        var grant = await grants.IssueRuntimeActionGrantAsync(
            context, registration, request, cancellationToken).ConfigureAwait(false);
        await RequireCurrentAsync(context, registration, cancellationToken).ConfigureAwait(false);
        var invocation = new ExecutableActionInvocation(context.User, binding.SessionId, binding.WorkflowStepId!,
            request.ActionId, RuntimeActionContract.Purpose, grant, binding.ExecutionFence, policyBinding, request.EventId);
        // The guarded callback admits dispatch; the native SDK effect remains in AgentHost.
        var evaluated = await guard.ExecuteAsync(invocation,
            async token =>
            {
                await RequireCurrentAsync(context, registration, token).ConfigureAwait(false);
                if (request.IsToolInvocation)
                    await grants.IssueRuntimeActionGrantAsync(
                        context, registration, request, token, reserveToolInvocation: true).ConfigureAwait(false);
                await RequireCurrentAsync(context, registration, token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        await RequireCurrentAsync(context, registration, cancellationToken).ConfigureAwait(false);
        return new(1, request, grant.GrantId, grant.Revision, request.EventId,
            evaluated.Outcome, evaluated.ReasonCode);
    }

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
        RequireCostTurnEnabled(registration.Binding, admission.Request);
        var reference = await grants.IssueRuntimeActionGrantAsync(
            context, registration, admission.Request, cancellationToken).ConfigureAwait(false);
        if (reference.GrantId != admission.GrantId || reference.Revision != admission.GrantRevision)
            throw new RuntimeAuthorizationException("runtime_action_admission_invalid");
        if (admission.Request.IsToolInvocation)
            await grants.RequireToolInvocationReservationAsync(
                registration, admission.Request, cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        var receipt = await grants.ValidateReceiptAdmissionAsync(actor, binding.ProjectId, binding.RunId,
            admission.PolicyReceiptId, cancellationToken).ConfigureAwait(false);
        if (receipt.Identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
            receipt.Evidence.GrantId != admission.GrantId || receipt.Evidence.GrantRevision != admission.GrantRevision ||
            receipt.Evidence.StepId != binding.WorkflowStepId || receipt.Evidence.ActionId != admission.Request.ActionId ||
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

    private async Task RequireCurrentAsync(
        HttpContext context, RuntimeRegistration expected, CancellationToken cancellationToken)
    {
        if (await registrations.ReadCurrentAsync(
                context, expected.RuntimeInstanceId, cancellationToken).ConfigureAwait(false) != expected)
            throw new RuntimeAuthorizationException("runtime_action_registration_changed");
    }

    private static void RequireRequest(RuntimeRegistration registration, RuntimeActionRequest request)
    {
        if (registration.Revision != request.RegistrationRevision ||
            registration.Binding.ExecutionFence != request.ExecutionFence)
            throw new RuntimeAuthorizationException("runtime_action_binding_invalid");
    }

    private static void RequireCostTurnEnabled(RuntimeBinding binding, RuntimeActionRequest request)
    {
        if (request.ActionId == "model.turn" && binding.MaxModelTurns is not null && request.DispatchId is null)
            throw new RuntimeAuthorizationException("runtime_model_turn_admission_required");
        if (request.ActionId == "model.turn" && binding.ModelSourceMode == ModelSourceMode.HostedCopilot &&
            binding.CopilotHardCreditLimit is 0)
            throw new RuntimeAuthorizationException("runtime_cost_budget_exhausted");
    }
}

public static class RuntimeActionEndpoints
{
    public static void MapRuntimeActionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/runtime/actions/authorize",
            (HttpContext context, RuntimeActionRequest request, [FromServices] RuntimeActionOwner owner,
                CancellationToken token) => ExecuteAsync(context, () => owner.AuthorizeAsync(context, request, token)))
            .RequireAuthorization();
        endpoints.MapPost("/internal/runtime/actions/verify",
            (HttpContext context, RuntimeActionAdmission admission, [FromServices] RuntimeActionOwner owner,
                CancellationToken token) => ExecuteAsync(context, () => owner.VerifyAsync(context, admission, token)))
            .RequireAuthorization();
    }

    private static async Task<IResult> ExecuteAsync(HttpContext context, Func<Task<RuntimeActionAdmission>> action)
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
            return Results.Json(new { error = failure.Code }, statusCode: StatusCodes.Status403Forbidden);
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
