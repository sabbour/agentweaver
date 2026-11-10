using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Orchestrator;

public static partial class CoordinationEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowGenerationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/projects/{projectId}/workflows").RequireAuthorization();
        routes.MapPost("/generate", SubmitWorkflowGenerationAsync)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
        routes.MapGet("/generation-jobs/{jobId:guid}", ReadWorkflowGenerationJobAsync);
        routes.MapGet("/generation-jobs/{jobId:guid}/result", ReadWorkflowGenerationResultAsync);
        return endpoints;
    }

    private static Task<IResult> SubmitWorkflowGenerationAsync(
        string projectId,
        GenerateProjectWorkflowRequest request,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        CoordinationOwnerStore sessions,
        CoordinatorDecisionOwnerStore decisions,
        RuntimeRegistrationOwner registrations,
        WorkflowGenerationJobStore jobs,
        WorkflowGenerationWorkQueue queue,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Count != 0)
                throw new CoordinationException(
                    "workflow_generation_request_invalid", StatusCodes.Status400BadRequest);
            request.Validate();
            var idempotencyKey = ReadIdempotencyKey(context);
            var actor = RequireOwnerActor(context, options, projectId, request.RunId, options.Audience);
            RequireOrchestratorScope(context.User);
            if (!await jobs.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);

            var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
                context, projectId, request.RunId, cancellationToken).ConfigureAwait(false);
            RequireProjectWriteAndOrchestratorAuthority(selection, projectId, request.RunId, actor);
            var existing = await jobs.ReadByIdempotencyKeyAsync(
                selection.Authorization.TenantId,
                projectId,
                actor.Issuer,
                actor.Subject,
                idempotencyKey,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (WorkflowGenerationExecution.RequestFingerprint(request, existing) != existing.RequestFingerprint)
                    throw new CoordinationException("idempotency_key_conflict", StatusCodes.Status409Conflict);
                var acceptedSelectionHash =
                    CoordinationOwnerStore.HashSelection(selection.Selection).ToLowerInvariant();
                if (existing.ConfigurationRevision != selection.Selection.ProjectConfigurationRevision ||
                    existing.AcceptedSelectionHash != acceptedSelectionHash)
                    throw new CoordinationException(
                        "workflow_generation_binding_stale", StatusCodes.Status409Conflict);
                await RequireValidCompletedResultAsync(jobs, existing, cancellationToken).ConfigureAwait(false);
                if (WorkflowGenerationExecution.CanEnqueue(existing))
                {
                    var existingIdentity = new SessionIdentity(projectId, request.RunId, request.SessionId);
                    var existingTree = await sessions.ReadSessionTreeAsync(actor, existingIdentity, cancellationToken)
                        .ConfigureAwait(false);
                    var existingStatus = await sessions.ReadSessionStatusAsync(
                        actor, existingIdentity, cancellationToken)
                        .ConfigureAwait(false);
                    CastingGenerationExecutionContract.RequireAcceptedRoot(
                        projectId, request.RunId, request.SessionId, existingTree, existingStatus);
                    var decision = await decisions.ReadCurrentAsync(
                        actor, existingIdentity, selection, cancellationToken).ConfigureAwait(false);
                    if (existing.ExecutionPhase != "send-admitted")
                        CoordinationEndpoints.RequirePrePlanState(decision, request.ExpectedDecisionStateVersion);
                    var existingRegistration = await registrations.ReadCurrentForSessionAsync(
                        context, projectId, request.RunId, request.SessionId, cancellationToken).ConfigureAwait(false);
                    RequireRegistrationBinding(
                        existingRegistration, projectId, request.RunId, request.SessionId,
                        existingStatus.ExecutionFence, acceptedSelectionHash,
                        selection.Selection.ProjectConfigurationRevision, timeProvider);
                    if (existing.ExecutionFence != existingStatus.ExecutionFence ||
                        existing.RuntimeInstanceId != existingRegistration.RuntimeInstanceId ||
                        existing.RegistrationRevision != existingRegistration.Revision ||
                        existing.ModelSelectionReference != existingRegistration.Binding.ModelSelectionReference ||
                        existing.AcceptedSelectionHash != acceptedSelectionHash ||
                        existing.ConfigurationRevision != selection.Selection.ProjectConfigurationRevision)
                        throw new CoordinationException(
                            "workflow_generation_binding_stale", StatusCodes.Status409Conflict);
                    await QueueJobAsync(
                        context, existing, actor, queue, timeProvider).ConfigureAwait(false);
                }
                return Results.Accepted(
                    WorkflowGenerationExecution.CreateEnvelope(existing).StatusUrl,
                    WorkflowGenerationExecution.CreateEnvelope(existing));
            }

            var identity = new SessionIdentity(projectId, request.RunId, request.SessionId);
            var tree = await sessions.ReadSessionTreeAsync(actor, identity, cancellationToken).ConfigureAwait(false);
            var status = await sessions.ReadSessionStatusAsync(actor, identity, cancellationToken).ConfigureAwait(false);
            CastingGenerationExecutionContract.RequireAcceptedRoot(
                projectId, request.RunId, request.SessionId, tree, status);
            var selectionHash = CoordinationOwnerStore.HashSelection(selection.Selection).ToLowerInvariant();
            var currentDecision = await decisions.ReadCurrentAsync(
                actor, identity, selection, cancellationToken).ConfigureAwait(false);
            RequirePrePlanState(currentDecision, request.ExpectedDecisionStateVersion);
            var registration = await registrations.ReadCurrentForSessionAsync(
                context, projectId, request.RunId, request.SessionId, cancellationToken).ConfigureAwait(false);
            RequireRegistrationBinding(
                registration, projectId, request.RunId, request.SessionId,
                status.ExecutionFence, selectionHash, selection.Selection.ProjectConfigurationRevision,
                timeProvider);

            var prompt = WorkflowGenerationExecution.PurposeBoundPrompt(request.Prompt);
            var promptHash = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(prompt));
            var fingerprint = WorkflowGenerationExecution.RequestFingerprint(
                request, selection, actor, registration, status.ExecutionFence);
            var create = new WorkflowGenerationJobCreate(
                Guid.NewGuid(),
                Guid.NewGuid(),
                projectId,
                request.RunId,
                request.SessionId,
                selection.Authorization.TenantId,
                actor.Issuer,
                actor.Subject,
                idempotencyKey,
                fingerprint,
                prompt,
                promptHash,
                request.ExpectedDecisionStateVersion,
                selection.Selection.ProjectConfigurationRevision,
                selectionHash,
                status.ExecutionFence,
                registration.RuntimeInstanceId,
                registration.Revision,
                registration.Binding.ModelSelectionReference!);
            var created = await jobs.CreateOrGetAsync(create, cancellationToken).ConfigureAwait(false);
            if (created.Disposition == WorkflowGenerationCreateDisposition.Conflict)
                throw new CoordinationException("idempotency_key_conflict", StatusCodes.Status409Conflict);
            if (created.Disposition == WorkflowGenerationCreateDisposition.BindingStale)
                throw new CoordinationException(
                    "workflow_generation_binding_stale", StatusCodes.Status409Conflict);

            var job = created.Job;
            await RequireValidCompletedResultAsync(jobs, job, cancellationToken).ConfigureAwait(false);
            if (WorkflowGenerationExecution.CanEnqueue(job))
                await QueueJobAsync(context, job, actor, queue, timeProvider).ConfigureAwait(false);

            var envelope = WorkflowGenerationExecution.CreateEnvelope(job);
            context.Response.Headers.Location = envelope.StatusUrl;
            return Results.Accepted(envelope.StatusUrl, envelope);
        }, cancellationToken);

    private static async Task QueueJobAsync(
        HttpContext context,
        WorkflowGenerationJob job,
        CoordinationActor actor,
        WorkflowGenerationWorkQueue queue,
        TimeProvider timeProvider)
    {
        var runtimeActor = await CreateRuntimeActorAsync(context, actor, timeProvider).ConfigureAwait(false);
        var work = new WorkflowGenerationWork(
            job.ProjectId, job.RunId, job.SessionId, actor, context.User, runtimeActor, job.JobId);
        if (!queue.TryEnqueue(work))
        {
            runtimeActor.Bearer.Invalidate();
            throw new CoordinationException(
                "workflow_generation_queue_full", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Task<IResult> ReadWorkflowGenerationJobAsync(
        string projectId,
        Guid jobId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        WorkflowGenerationJobStore jobs,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await jobs.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
            var job = await jobs.ReadAsync(projectId, jobId, cancellationToken).ConfigureAwait(false)
                ?? throw WorkflowGenerationExecution.NotFound();
            await RequireJobReadAuthorityAsync(context, options, projects, job, cancellationToken)
                .ConfigureAwait(false);
            await RequireValidCompletedResultAsync(jobs, job, cancellationToken).ConfigureAwait(false);
            return Results.Ok(WorkflowGenerationExecution.CreateEnvelope(job));
        }, cancellationToken);

    private static Task<IResult> ReadWorkflowGenerationResultAsync(
        string projectId,
        Guid jobId,
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        WorkflowGenerationJobStore jobs,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await jobs.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
                throw new CoordinationException("database_update_pending", StatusCodes.Status503ServiceUnavailable);
            var job = await jobs.ReadAsync(projectId, jobId, cancellationToken).ConfigureAwait(false)
                ?? throw WorkflowGenerationExecution.NotFound();
            await RequireJobReadAuthorityAsync(context, options, projects, job, cancellationToken)
                .ConfigureAwait(false);
            if (job.Status != "completed")
                throw new CoordinationException(
                    "workflow_generation_not_complete", StatusCodes.Status409Conflict);
            await RequireValidCompletedResultAsync(jobs, job, cancellationToken).ConfigureAwait(false);
            return Results.Ok(job.Result);
        }, cancellationToken);

    private static async Task RequireValidCompletedResultAsync(
        WorkflowGenerationJobStore jobs,
        WorkflowGenerationJob job,
        CancellationToken cancellationToken)
    {
        if (job.Status == "completed" &&
            (!WorkflowGenerationJobStore.HasValidResult(job, job.Result) ||
             !await jobs.HasPersistedCompletionAsync(job, job.Result, cancellationToken).ConfigureAwait(false)))
            throw new CoordinationException(
                "workflow_generation_result_unavailable", StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task RequireJobReadAuthorityAsync(
        HttpContext context,
        OrchestratorOptions options,
        ProjectsRunSelectionClient projects,
        WorkflowGenerationJob job,
        CancellationToken cancellationToken)
    {
        try
        {
            var actor = RequireOwnerActor(context, options, job.ProjectId, job.RunId, options.Audience);
            if (actor.Issuer != job.ActorIssuer || actor.Subject != job.ActorId)
                throw WorkflowGenerationExecution.NotFound();
            var selection = await projects.ReadSelectionForReadWithAuthorityAsync(
                context, job.ProjectId, job.RunId, cancellationToken).ConfigureAwait(false);
            if (selection.Authorization.TenantId != job.TenantId ||
                selection.Authorization.Issuer != job.ActorIssuer ||
                selection.Authorization.ActorId != job.ActorId ||
                !CoordinationOwnerStore.HashSelection(selection.Selection)
                    .Equals(job.AcceptedSelectionHash, StringComparison.OrdinalIgnoreCase))
                throw WorkflowGenerationExecution.NotFound();
        }
        catch (CoordinationException exception) when (
            exception.StatusCode is StatusCodes.Status403Forbidden or StatusCodes.Status404NotFound)
        {
            throw WorkflowGenerationExecution.NotFound();
        }
    }

    private static string ReadIdempotencyKey(HttpContext context)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) ||
            values[0]!.Length > 256 ||
            values[0]!.Any(character => char.IsControl(character) || character > 0x7e))
            throw new CoordinationException(
                "workflow_generation_idempotency_key_required", StatusCodes.Status400BadRequest);
        return values[0]!;
    }

    internal static void RequireOrchestratorScope(ClaimsPrincipal caller)
    {
        var scopes = caller.FindAll("scope").Concat(caller.FindAll("scp"))
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!scopes.Contains("projects.orchestrator", StringComparer.Ordinal))
            throw new CoordinationException(
                "project_orchestrator_role_required_acceptRunSelection", StatusCodes.Status403Forbidden);
    }

    internal static void RequireProjectWriteAndOrchestratorAuthority(
        AuthorizedRunSelection selection,
        string projectId,
        string runId,
        CoordinationActor actor)
    {
        var authority = selection.Authorization;
        if (authority.ContractVersion != 1 || authority.Issuer != actor.Issuer ||
            authority.ActorId != actor.Subject || authority.BoundProjectId != projectId ||
            authority.BoundRunId != runId || string.IsNullOrWhiteSpace(authority.TenantId) ||
            !authority.EffectiveAuthority.Any(resource =>
                resource.ResourceType == "project" && resource.ResourceId == projectId &&
                !resource.Permissions.IsDefault &&
                resource.Permissions.Any(permission =>
                    permission.RoleRevision > 0 && permission.Permission == "acceptRunSelection")) ||
            !authority.EffectiveAuthority.Any(resource =>
                resource.ResourceType == "project" && resource.ResourceId == projectId &&
                !resource.Permissions.IsDefault &&
                resource.Permissions.Any(permission =>
                    permission.RoleRevision > 0 && permission.Permission == "writeProjects")))
            throw new CoordinationException(
                "project_orchestrator_role_required_acceptRunSelection", StatusCodes.Status403Forbidden);
    }

    internal static void RequirePrePlanState(
        CoordinatorDecisionCurrentState decision,
        long expectedVersion)
    {
        if (decision.StateVersion != expectedVersion)
            throw new CoordinationException(
                "workflow_generation_decision_stale", StatusCodes.Status409Conflict);
        if (decision.State.SelectedWorkflow is not null ||
            decision.State.CandidateWorkPlan is not null ||
            decision.State.ConfirmedWorkPlan is not null)
            throw new CoordinationException(
                "workflow_generation_not_available_after_plan", StatusCodes.Status409Conflict);
    }

    internal static void RequireRegistrationBinding(
        RuntimeRegistration registration,
        string projectId,
        string runId,
        string sessionId,
        long executionFence,
        string acceptedSelectionHash,
        long configurationRevision,
        TimeProvider timeProvider)
    {
        CastingGenerationExecutionContract.RequireCurrentRegistration(
            registration, projectId, runId, sessionId, executionFence, acceptedSelectionHash, timeProvider);
        if (registration.Binding.ProjectConfigurationRevision != configurationRevision ||
            string.IsNullOrWhiteSpace(registration.Binding.ModelSelectionReference))
            throw new CoordinationException(
                "workflow_generation_registration_stale", StatusCodes.Status409Conflict);
    }
}

internal sealed record WorkflowGenerationWork(
    string ProjectId,
    string RunId,
    string SessionId,
    CoordinationActor Caller,
    ClaimsPrincipal Principal,
    RuntimeActorAuthorization Actor,
    Guid JobId);

internal sealed class WorkflowGenerationWorkQueue
{
    private readonly Channel<WorkflowGenerationWork> _channel = Channel.CreateBounded<WorkflowGenerationWork>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    internal bool TryEnqueue(WorkflowGenerationWork work) => _channel.Writer.TryWrite(work);

    internal IAsyncEnumerable<WorkflowGenerationWork> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

internal sealed class WorkflowGenerationBackgroundService(
    WorkflowGenerationWorkQueue queue,
    IServiceScopeFactory scopes,
    ILogger<WorkflowGenerationBackgroundService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Parallel.ForEachAsync(
            queue.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = stoppingToken },
            async (work, token) =>
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<WorkflowGenerationExecutor>()
                        .ExecuteAsync(work, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning("Workflow generation worker failed with {FailureCode}.",
                        RuntimeRunStartContinuation.FailureCode(exception));
                }
                finally
                {
                    work.Actor.Bearer.Invalidate();
                }
            });
}

internal sealed class WorkflowGenerationExecutor(
    WorkflowGenerationJobStore jobs,
    ProjectsRunSelectionClient projects,
    CoordinationOwnerStore sessions,
    CoordinatorDecisionOwnerStore decisions,
    RuntimeRegistrationOwner registrations,
    ExecutableActionGrantOwnerStore actionGrants,
    EventsAddressedMessageClient events,
    IHttpClientFactory clients,
    TimeProvider timeProvider,
    IServiceProvider services,
    ILogger<WorkflowGenerationExecutor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    internal Task ExecuteAsync(WorkflowGenerationWork work, CancellationToken cancellationToken) =>
        jobs.WithExecutionLockAsync(
            work.ProjectId,
            work.JobId,
            token => ExecuteOwnedAsync(work, token),
            cancellationToken);

    private async Task ExecuteOwnedAsync(WorkflowGenerationWork work, CancellationToken cancellationToken)
    {
        if (!work.Actor.Bearer.IsUsable())
            return;
        var job = await jobs.TryBeginExecutionAsync(
            work.ProjectId, work.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
            return;

        var completionVerified = false;
        var sendAdmitted = job.ExecutionPhase == "send-admitted";
        try
        {
            var context = CreateContext(work, services);
            var current = await ReadCurrentAsync(context, work, job, cancellationToken).ConfigureAwait(false);
            if (job.ExecutionPhase != "send-admitted")
                CoordinationEndpoints.RequirePrePlanState(current.Decision, job.ExpectedDecisionStateVersion);
            else if (!string.Equals(job.AcceptedSelectionHash, current.SelectionHash, StringComparison.OrdinalIgnoreCase))
                throw new CoordinationException(
                    "workflow_generation_binding_stale", StatusCodes.Status409Conflict);

            var stored = await actionGrants.ReadRootTurnCompletionAsync(
                context, current.Registration, job.NativeRequestId, job.PromptSha256, cancellationToken)
                .ConfigureAwait(false);
            RuntimeRootTurnCompletionEvidence evidence;
            string output;
            if (stored is not null)
            {
                if (job.ExecutionPhase != "send-admitted" ||
                    stored.Completion.RequestId != job.NativeRequestId ||
                    stored.Completion.Registration != current.Registration ||
                    stored.Completion.PromptHash != job.PromptSha256)
                    throw new CoordinationException(
                        "workflow_generation_completion_receipt_invalid",
                        StatusCodes.Status503ServiceUnavailable);
                (evidence, output) = await ReadCompletionMaterialsAsync(
                    context, work, job, current.Registration, stored, cancellationToken).ConfigureAwait(false);
                completionVerified = true;
            }
            else
            {
                if (job.ExecutionPhase == "send-admitted" &&
                    (job.Status != "failed" ||
                     job.FailureCode != "workflow_generation_outcome_indeterminate"))
                    throw new CoordinationException(
                        "workflow_generation_outcome_indeterminate",
                        StatusCodes.Status503ServiceUnavailable);

                await actionGrants.AdmitCoordinatorRootModelTurnAsync(
                    context,
                    current.Registration,
                    job.NativeRequestId,
                    job.PromptSha256,
                    job.ExpectedDecisionStateVersion,
                    cancellationToken).ConfigureAwait(false);

                var readinessActor = work.Actor;
                var readiness = await CoordinationEndpoints.SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                    clients.CreateClient("casting-generation-host"),
                    CoordinationEndpoints.RuntimeHostEndpoint(
                        current.Registration.Binding.ConfigureEndpoint, "/runtime/v1/readiness"),
                    readinessActor,
                    null,
                    allowNotReady: true,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new CoordinationException(
                        "workflow_generation_host_not_ready", StatusCodes.Status503ServiceUnavailable);
                CoordinationEndpoints.ValidateHostReadiness(readiness, current.Registration, timeProvider);
                var priorRegistration = current.Registration;
                current = await ReadCurrentAsync(context, work, job, cancellationToken).ConfigureAwait(false);
                CoordinationEndpoints.RequirePrePlanState(current.Decision, job.ExpectedDecisionStateVersion);
                if (current.Registration != priorRegistration ||
                    current.SelectionHash != job.AcceptedSelectionHash)
                    throw new CoordinationException(
                        "workflow_generation_binding_stale", StatusCodes.Status409Conflict);
                if (!await jobs.MarkSendAdmittedAsync(
                        job.ProjectId, job.JobId, cancellationToken).ConfigureAwait(false))
                    return;
                sendAdmitted = true;

                var hostRequest = CreateHostRequest(job, current.Registration, readiness);
                var hostResponse = await CoordinationEndpoints.SendRuntimeHostMessageAsync(
                    clients.CreateClient("casting-generation-host"),
                    current.Registration,
                    readiness,
                    work.Actor,
                    hostRequest,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
                output = CastingGenerationExecutionContract.ReadOutput(
                    hostResponse, RuntimeContractValidation.NativeSessionId(current.Registration.Binding));
                var completion = hostResponse.RootCompletion
                    ?? throw new CoordinationException(
                        "workflow_generation_completion_receipt_unavailable",
                        StatusCodes.Status503ServiceUnavailable);
                completion.Validate();
                if (hostResponse.RootReplay is not null ||
                    completion.RequestId != job.NativeRequestId ||
                    completion.Registration != current.Registration ||
                    completion.PromptHash != job.PromptSha256 ||
                    completion.NativeTurn.OutputHash != RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(output)))
                    throw new CoordinationException(
                        "workflow_generation_completion_receipt_invalid",
                        StatusCodes.Status503ServiceUnavailable);
                var recorded = await RecordCompletionAsync(
                    context, work, job, current.Registration, readiness, completion, output, cancellationToken)
                    .ConfigureAwait(false);
                evidence = recorded.Evidence;
                completionVerified = true;
            }

            var definition = ParseAndValidateDefinition(output, current.Selection.Selection);
            var definitionHash = WorkflowGenerationJobStore.Hash(JsonSerializer.Serialize(definition, JsonOptions));
            var artifactHash = WorkflowGenerationJobStore.Hash(
                $"{job.JobId:D}\0{definition.Id}\0{definition.Revision}\0{definitionHash}");
            var result = new WorkflowGenerationJobResult(
                job.JobId,
                Guid.ParseExact(artifactHash[..32], "N"),
                definition,
                definitionHash,
                evidence,
                job.ConfigurationRevision,
                job.AcceptedSelectionHash);
            if (!await jobs.CompleteAsync(
                    job.ProjectId, job.JobId, result, cancellationToken).ConfigureAwait(false))
            {
                var latest = await jobs.ReadAsync(job.ProjectId, job.JobId, cancellationToken).ConfigureAwait(false);
                if (latest?.Status is "cancelled")
                    await jobs.MarkQuiescedAsync(job.ProjectId, job.JobId, cancellationToken).ConfigureAwait(false);
                else
                    throw new CoordinationException(
                        "workflow_generation_outcome_indeterminate", StatusCodes.Status503ServiceUnavailable);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failureCode = MapFailure(exception, sendAdmitted);
            var quiesced = !sendAdmitted || completionVerified &&
                failureCode is ("workflow_generation_artifact_invalid" or "workflow_generation_role_unresolved");
            await jobs.FailAsync(
                job.ProjectId, job.JobId, failureCode, FailureMessage(failureCode),
                quiesced, cancellationToken).ConfigureAwait(false);
            if (quiesced)
                await jobs.MarkQuiescedAsync(job.ProjectId, job.JobId, cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Workflow generation job {JobId} failed with {FailureCode}.",
                job.JobId, failureCode);
        }
    }

    private async Task<WorkflowGenerationCurrent> ReadCurrentAsync(
        HttpContext context,
        WorkflowGenerationWork work,
        WorkflowGenerationJob job,
        CancellationToken cancellationToken)
    {
        if (!work.Actor.Bearer.IsUsable())
            throw new CoordinationException("workflow_generation_actor_expired", StatusCodes.Status403Forbidden);
        var selection = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, work.ProjectId, work.RunId, cancellationToken).ConfigureAwait(false);
        CoordinationEndpoints.RequireOrchestratorScope(context.User);
        CoordinationEndpoints.RequireProjectWriteAndOrchestratorAuthority(
            selection, work.ProjectId, work.RunId, work.Caller);
        await CoordinationEndpoints.RequireUnchangedAuthorizedSelectionAsync(
            context, work.ProjectId, work.RunId, selection, projects, cancellationToken)
            .ConfigureAwait(false);
        if (selection.Authorization.TenantId != job.TenantId ||
            selection.Authorization.Issuer != job.ActorIssuer ||
            selection.Authorization.ActorId != job.ActorId)
            throw new CoordinationException("workflow_generation_binding_stale", StatusCodes.Status409Conflict);

        var identity = new SessionIdentity(work.ProjectId, work.RunId, work.SessionId);
        var tree = await sessions.ReadSessionTreeAsync(work.Caller, identity, cancellationToken).ConfigureAwait(false);
        var status = await sessions.ReadSessionStatusAsync(work.Caller, identity, cancellationToken).ConfigureAwait(false);
        CastingGenerationExecutionContract.RequireAcceptedRoot(
            work.ProjectId, work.RunId, work.SessionId, tree, status);
        var decision = await decisions.ReadCurrentAsync(
            work.Caller, identity, selection, cancellationToken).ConfigureAwait(false);
        var registration = await registrations.ReadCurrentForSessionAsync(
            context, work.ProjectId, work.RunId, work.SessionId, cancellationToken).ConfigureAwait(false);
        var selectionHash = CoordinationOwnerStore.HashSelection(selection.Selection).ToLowerInvariant();
        CoordinationEndpoints.RequireRegistrationBinding(
            registration, work.ProjectId, work.RunId, work.SessionId, status.ExecutionFence,
            selectionHash, selection.Selection.ProjectConfigurationRevision, timeProvider);
        if (job.RuntimeInstanceId != registration.RuntimeInstanceId ||
            job.RegistrationRevision != registration.Revision ||
            job.ModelSelectionReference != registration.Binding.ModelSelectionReference ||
            job.ExecutionFence != status.ExecutionFence ||
            job.ConfigurationRevision != selection.Selection.ProjectConfigurationRevision ||
            job.AcceptedSelectionHash != selectionHash)
            throw new CoordinationException(
                "workflow_generation_binding_stale", StatusCodes.Status409Conflict);
        return new(selection, decision, registration, selectionHash);
    }

    private async Task<(RuntimeRootTurnCompletionEvidence Evidence, string Output)> RecordCompletionAsync(
        HttpContext context,
        WorkflowGenerationWork work,
        WorkflowGenerationJob job,
        RuntimeRegistration registration,
        RuntimeHostReadinessReceipt readiness,
        RuntimeRootTurnCompletionReceipt completion,
        string output,
        CancellationToken cancellationToken)
    {
        await VerifyMaterialsAsync(
            context, work, job, registration, readiness, completion, output, cancellationToken).ConfigureAwait(false);
        var stored = await actionGrants.RecordRootTurnCompletionAsync(
            context, registration, completion, cancellationToken).ConfigureAwait(false);
        return (stored.Completion.ToEvidence(), output);
    }

    private async Task<(RuntimeRootTurnCompletionEvidence Evidence, string Output)> ReadCompletionMaterialsAsync(
        HttpContext context,
        WorkflowGenerationWork work,
        WorkflowGenerationJob job,
        RuntimeRegistration registration,
        RuntimeRootTurnCompletionLedgerReceipt stored,
        CancellationToken cancellationToken)
    {
        var completion = stored.Completion.Validate();
        if (completion.RequestId != job.NativeRequestId ||
            completion.Registration != registration ||
            completion.PromptHash != job.PromptSha256)
            throw new CoordinationException(
                "workflow_generation_completion_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
        var assistant = await events.ReadSessionMaterialAsync(
            context, new SessionIdentity(job.ProjectId, job.RunId, job.SessionId),
            completion.AssistantMaterial.EventId, SessionMaterialKind.TurnContent,
            work.Actor, cancellationToken).ConfigureAwait(false);
        var user = await events.ReadSessionMaterialAsync(
            context, new SessionIdentity(job.ProjectId, job.RunId, job.SessionId),
            completion.UserMaterial.EventId, SessionMaterialKind.TurnContent,
            work.Actor, cancellationToken).ConfigureAwait(false);
        var cache = await events.ReadSessionMaterialAsync(
            context, new SessionIdentity(job.ProjectId, job.RunId, job.SessionId),
            completion.CacheMaterial.EventId, SessionMaterialKind.SdkCache,
            work.Actor, cancellationToken).ConfigureAwait(false);
        if (user.Material != completion.UserMaterial ||
            assistant.Material != completion.AssistantMaterial ||
            cache.Material != completion.CacheMaterial ||
            !user.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(job.Prompt)) ||
            RuntimeContractValidation.Hash(assistant.Bytes) != completion.NativeTurn.OutputHash)
            throw new CoordinationException(
                "workflow_generation_material_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
        return (completion.ToEvidence(), Encoding.UTF8.GetString(assistant.Bytes));
    }

    private async Task VerifyMaterialsAsync(
        HttpContext context,
        WorkflowGenerationWork work,
        WorkflowGenerationJob job,
        RuntimeRegistration registration,
        RuntimeHostReadinessReceipt readiness,
        RuntimeRootTurnCompletionReceipt completion,
        string output,
        CancellationToken cancellationToken)
    {
        var identity = new SessionIdentity(job.ProjectId, job.RunId, job.SessionId);
        var user = await events.ReadSessionMaterialAsync(
            context, identity, completion.UserMaterial.EventId, SessionMaterialKind.TurnContent,
            work.Actor, cancellationToken).ConfigureAwait(false);
        var assistant = await events.ReadSessionMaterialAsync(
            context, identity, completion.AssistantMaterial.EventId, SessionMaterialKind.TurnContent,
            work.Actor, cancellationToken).ConfigureAwait(false);
        var cache = await events.ReadSessionMaterialAsync(
            context, identity, completion.CacheMaterial.EventId, SessionMaterialKind.SdkCache,
            work.Actor, cancellationToken).ConfigureAwait(false);
        if (user.Material != completion.UserMaterial ||
            assistant.Material != completion.AssistantMaterial ||
            cache.Material != completion.CacheMaterial ||
            !user.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(job.Prompt)) ||
            !assistant.Bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(output)) ||
            completion.ObserveGrant != readiness.SourceGrant ||
            completion.Registration != registration)
            throw new CoordinationException(
                "workflow_generation_material_receipt_invalid", StatusCodes.Status503ServiceUnavailable);
    }

    private static RuntimeA2ASendRequest CreateHostRequest(
        WorkflowGenerationJob job,
        RuntimeRegistration registration,
        RuntimeHostReadinessReceipt readiness)
    {
        var proof = new RuntimeHostSessionProof(
            1,
            registration,
            readiness.SourceGrant.GrantId,
            readiness.SourceGrant.Revision,
            RuntimeCredentialPurpose.Observe);
        return new RuntimeA2ASendRequest(new RuntimeA2AMessage(
            "message",
            job.NativeRequestId,
            RuntimeContractValidation.NativeSessionId(registration.Binding),
            "user",
            [new("text", job.Prompt)],
            new(
                proof,
                AddressedMessageDeliveryMode.Immediate,
                ContinueAfterCallerDisconnect: true,
                ReadOnlyToolsOnly: true)));
    }

    private static WorkflowDefinition ParseAndValidateDefinition(
        string output,
        EffectiveRunSelection selection)
    {
        WorkflowDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<WorkflowDefinition>(output, JsonOptions);
        }
        catch (JsonException)
        {
            throw new CoordinationException(
                "workflow_generation_artifact_invalid", StatusCodes.Status422UnprocessableEntity);
        }
        var validation = WorkflowDefinitionValidator.ValidateAndSnapshot(definition);
        if (!validation.IsValid || validation.Value is null ||
            validation.Value.Definition.Origin != WorkflowDefinitionOrigin.Generated)
            throw new CoordinationException(
                "workflow_generation_artifact_invalid", StatusCodes.Status422UnprocessableEntity);

        try
        {
            _ = CoordinatorWorkflowCatalog.ForSelection(selection, validation.Value.Definition);
        }
        catch (CoordinationException)
        {
            throw new CoordinationException(
                "workflow_generation_artifact_invalid", StatusCodes.Status422UnprocessableEntity);
        }
        var acceptedRoles = CoordinatorWorkflowCatalog.CreateRunSelectionContext(selection.Snapshot).Roles
            .Select(role => role.RoleId)
            .Where(role => !IsReservedRole(role))
            .ToHashSet(StringComparer.Ordinal);
        var roles = validation.Value.Definition.Steps
            .SelectMany(step => step.AllowedRoles.Concat(
                step.FixedWork is null ? Array.Empty<string>() : [step.FixedWork.RoleId]));
        if (roles.Any(role => IsReservedRole(role) || !acceptedRoles.Contains(role)))
            throw new CoordinationException(
                "workflow_generation_role_unresolved", StatusCodes.Status422UnprocessableEntity);
        return validation.Value.Definition;
    }

    private static bool IsReservedRole(string roleId)
    {
        var normalized = roleId.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        return normalized is "scribe" or "work-monitor" or "ralph" or "rai" or "rai-reviewer" or "coordinator";
    }

    private static HttpContext CreateContext(WorkflowGenerationWork work, IServiceProvider services)
    {
        var context = new DefaultHttpContext { User = work.Principal, RequestServices = services };
        context.Request.Headers.Authorization = $"Bearer {work.Actor.Bearer.GetValue()}";
        if (work.Actor.TenantSelector is not null)
            context.Request.Headers["X-Agentweaver-Tenant"] = work.Actor.TenantSelector;
        return context;
    }

    private static string MapFailure(Exception exception, bool sendAdmitted)
    {
        if (exception is CoordinationException coordination)
        {
            if (coordination.Code is "workflow_generation_artifact_invalid" or "workflow_generation_role_unresolved")
                return coordination.Code;
            if (coordination.Code is "workflow_generation_binding_stale" or
                "workflow_generation_decision_stale" or "workflow_generation_not_available_after_plan")
                return coordination.Code;
        }
        return sendAdmitted
            ? "workflow_generation_outcome_indeterminate"
            : "workflow_generation_failed";
    }

    private static string FailureMessage(string failureCode) => failureCode switch
    {
        "workflow_generation_artifact_invalid" =>
            "The model output was not a valid workflow definition.",
        "workflow_generation_role_unresolved" =>
            "The workflow uses a role that is not available in this run.",
        "workflow_generation_binding_stale" =>
            "The run selection or runtime binding changed before generation.",
        "workflow_generation_decision_stale" =>
            "The coordinator decision changed before generation.",
        "workflow_generation_not_available_after_plan" =>
            "Workflow generation must finish before workflow or plan selection.",
        "workflow_generation_outcome_indeterminate" =>
            "The native turn outcome is not known. Submit the same request to reconcile this job.",
        _ => "Workflow generation could not be completed."
    };

    private sealed record WorkflowGenerationCurrent(
        AuthorizedRunSelection Selection,
        CoordinatorDecisionCurrentState Decision,
        RuntimeRegistration Registration,
        string SelectionHash);
}

internal static class WorkflowGenerationExecution
{
    private const string PromptPrefix =
        "Create a workflow definition for the user's request. Return only one JSON object that matches the typed " +
        "Agentweaver WorkflowDefinition contract. Set origin to generated. Use only roles in the accepted run team. " +
        "Do not include markdown or text outside the JSON object. User request:\n";

    internal static string PurposeBoundPrompt(string prompt) => PromptPrefix + prompt;

    internal static bool CanEnqueue(WorkflowGenerationJob job) =>
        job.Status is "queued" or "running" &&
        job.ExecutionPhase is "queued" or "pre-send" or "send-admitted" ||
        job.Status == "failed" && job.ExecutionPhase == "send-admitted" &&
        job.FailureCode == "workflow_generation_outcome_indeterminate";

    internal static string RequestFingerprint(
        GenerateProjectWorkflowRequest request,
        AuthorizedRunSelection selection,
        CoordinationActor actor,
        RuntimeRegistration registration,
        long executionFence)
    {
        var identity = new
        {
            request.RunId,
            request.SessionId,
            request.ExpectedDecisionStateVersion,
            request.Prompt,
            selection.Authorization.TenantId,
            actor.Issuer,
            actor.Subject,
            selection.Selection.ProjectConfigurationRevision,
            SelectionHash = CoordinationOwnerStore.HashSelection(selection.Selection).ToLowerInvariant(),
            ExecutionFence = executionFence,
            registration.RuntimeInstanceId,
            RegistrationRevision = registration.Revision,
            ModelSelectionReference = registration.Binding.ModelSelectionReference
        };
        return WorkflowGenerationJobStore.Hash(JsonSerializer.Serialize(identity));
    }

    internal static string RequestFingerprint(
        GenerateProjectWorkflowRequest request,
        WorkflowGenerationJob job)
    {
        var identity = new
        {
            request.RunId,
            request.SessionId,
            request.ExpectedDecisionStateVersion,
            request.Prompt,
            TenantId = job.TenantId,
            Issuer = job.ActorIssuer,
            Subject = job.ActorId,
            ProjectConfigurationRevision = job.ConfigurationRevision,
            SelectionHash = job.AcceptedSelectionHash,
            ExecutionFence = job.ExecutionFence,
            RuntimeInstanceId = job.RuntimeInstanceId,
            RegistrationRevision = job.RegistrationRevision,
            ModelSelectionReference = job.ModelSelectionReference
        };
        return WorkflowGenerationJobStore.Hash(JsonSerializer.Serialize(identity));
    }

    internal static WorkflowGenerationJobEnvelope CreateEnvelope(WorkflowGenerationJob job)
    {
        var statusUrl = $"/api/projects/{Uri.EscapeDataString(job.ProjectId)}/workflows/generation-jobs/{job.JobId:D}";
        var resultUrl = statusUrl + "/result";
        var artifact = job.Status == "completed" && HasValidResult(job)
            ? new WorkflowGenerationArtifactSummary(
                job.Result!.ArtifactId,
                job.Result.WorkflowDefinition.Id,
                job.Result.WorkflowDefinition.Revision)
            : null;
        var failure = job.FailureCode is null
            ? null
            : new WorkflowGenerationFailure(job.FailureCode, job.FailureMessage ?? "Workflow generation failed.");
        return new WorkflowGenerationJobEnvelope(
            job.JobId, job.Status, job.ProjectId, job.RunId, job.SessionId,
            job.CreatedAt, job.UpdatedAt, statusUrl, resultUrl, artifact, failure);
    }

    internal static CoordinationException NotFound() =>
        new("workflow_generation_job_not_found", StatusCodes.Status404NotFound);

    private static bool HasValidResult(WorkflowGenerationJob job) =>
        WorkflowGenerationJobStore.HasValidResult(job, job.Result);
}
