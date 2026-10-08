using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.EventsAndSessions;

public static class SessionsEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static void MapEventsAndSessionsEndpoints(this IEndpointRouteBuilder app)
    {
        var endpoints = app.MapGroup("/internal/sessions")
            .RequireAuthorization();

        endpoints.MapPost("/{sessionId}", CreateAsync);
        endpoints.MapPost("/{sessionId}/events", AppendAsync);
        endpoints.MapPost("/{sessionId}/fork", ForkFromExplicitEventAsync);
        endpoints.MapPost("/{sessionId}/policy-evaluations", AppendPolicyEvaluationAsync);
        endpoints.MapGet("/{sessionId}/events", ReplayAsync);
        endpoints.MapGet("/{sessionId}/events/live", SubscribeAsync);

        app.MapGet("/internal/projects/{projectId}/runs/{runId}/events", ReplayRunAsync)
            .RequireAuthorization();
        app.MapGet("/internal/projects/{projectId}/runs/{runId}/events/live", SubscribeRunAsync)
            .RequireAuthorization();
        app.MapGet("/internal/projects/{projectId}/runs/{runId}/sessions-provider-binding", ReadRunBindingAsync)
            .RequireAuthorization();
    }

    private static Task<IResult> ReadRunBindingAsync(
        HttpContext context, string projectId, string runId, ISessionsJournal journal,
        ISessionsProviderBinder bindings, [FromServices] IProjectsAuthorizationContextClient projects,
        CancellationToken cancellationToken) => ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var first = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
            NativeUsageApplicationService.RequireReadAuthority(first);
            if (first.BoundProjectId != projectId || first.BoundRunId != runId)
                throw new RuntimeAuthorizationException("runtime_material_scope_invalid");
            var pin = await journal.GetRunProviderBindingAsync(
                context.User, projectId, runId, cancellationToken).ConfigureAwait(false);
            await bindings.VerifyPinnedAsync(context.User, pin, cancellationToken).ConfigureAwait(false);
            var current = await projects.GetCurrentAsync(context, cancellationToken).ConfigureAwait(false);
            NativeUsageApplicationService.RequireReadAuthority(current);
            if (current.TenantId != first.TenantId ||
                current.BoundProjectId != projectId || current.BoundRunId != runId)
                throw new RuntimeAuthorizationException("runtime_material_scope_invalid");
            return Results.Json(pin, JsonOptions);
        }, cancellationToken);

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        string sessionId,
        ISessionsJournal journal,
        ISessionsProviderBinder bindings,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            var binding = await bindings.ResolveAndPinAsync(context.User, cancellationToken);
            var session = await journal.CreateSessionAsync(context.User, sessionId, binding, cancellationToken);
            return Results.Json(session, JsonOptions, statusCode: StatusCodes.Status201Created);
        }, cancellationToken);

    private static async Task<IResult> AppendAsync(
        HttpContext context,
        string sessionId,
        AppendSessionEvent input,
        ISessionsJournal journal,
        ISessionsProviderBinder bindings,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            var binding = await journal.GetProviderBindingAsync(context.User, sessionId, cancellationToken);
            await bindings.VerifyPinnedAsync(context.User, binding, cancellationToken);
            var result = await journal.AppendAsync(context.User, sessionId, input, cancellationToken);
            return Results.Json(result, JsonOptions, statusCode: result.IsDuplicate
                ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        }, cancellationToken);

    private static async Task<IResult> ForkFromExplicitEventAsync(
        HttpContext context,
        string sessionId,
        SessionForkRequest input,
        ISessionsJournal journal,
        ISessionsProviderBinder bindings,
        [FromServices] ICoordinationOwnerClient owner,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!SessionIdentityClaims.TryGetScope(context.User, out var scope) || scope is null)
                throw new SessionAuthenticationException();
            var source = scope.Value.ForSession(sessionId);
            var admission = await owner.ValidateSessionForkAdmissionAsync(
                context, source, input, cancellationToken).ConfigureAwait(false);
            var binding = await journal.GetProviderBindingAsync(context.User, sessionId, cancellationToken);
            await bindings.VerifyPinnedAsync(context.User, binding, cancellationToken);
            var result = await journal.ForkFromExplicitEventAsync(
                context.User,
                sessionId,
                input,
                async token =>
                {
                    var currentAdmission = await owner.ValidateSessionForkAdmissionAsync(
                        context, source, input, token).ConfigureAwait(false);
                    if (currentAdmission != admission)
                        throw new CoordinationOwnerClientException(
                            "coordination_owner_admission_changed", StatusCodes.Status409Conflict);
                },
                cancellationToken);
            return Results.Json(result, JsonOptions, statusCode: result.IsDuplicate
                ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        }, cancellationToken);

    private static async Task<IResult> AppendPolicyEvaluationAsync(
        HttpContext context,
        string sessionId,
        PolicyEvaluationReceiptReferenceRequest request,
        [FromServices] PostgresSessionsJournal journal,
        ISessionsProviderBinder bindings,
        [FromServices] ICoordinationOwnerClient owner,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            if (request.ReceiptId == Guid.Empty)
                throw new ArgumentException("A policy receipt identity is required.", nameof(request));
            var binding = await journal.GetProviderBindingAsync(context.User, sessionId, cancellationToken);
            await bindings.VerifyPinnedAsync(context.User, binding, cancellationToken);
            if (!binding.NegotiatedCapabilities.Contains(SessionsCapabilities.PolicyEvaluations))
                throw new SessionProviderBindingConflictException(
                    "The pinned Sessions provider did not negotiate policy evaluation receipts.");
            var identity = new SessionIdentity(binding.ProjectId, binding.RunId, sessionId);
            var receipt = await owner.ReadPolicyEvaluationReceiptAsync(
                context, identity, request.ReceiptId, cancellationToken);
            await owner.ValidatePolicyEvaluationReceiptAdmissionAsync(
                context, identity, request.ReceiptId, cancellationToken);
            var result = await journal.AppendPolicyEvaluationAsync(
                context.User,
                sessionId,
                receipt,
                token => owner.ValidatePolicyEvaluationReceiptAdmissionAsync(
                    context, identity, request.ReceiptId, token),
                cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            var acknowledgment = new PolicyEvaluationAppendAcknowledgment(
                receipt.ReceiptId, identity, result.Event.Position, result.IsDuplicate);
            return Results.Json(acknowledgment, JsonOptions, statusCode: result.IsDuplicate
                ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        }, cancellationToken);

    private static async Task<IResult> ReplayAsync(
        HttpContext context,
        string sessionId,
        string? cursor,
        int? limit,
        ISessionsJournal journal,
        ISessionsProviderBinder bindings,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var binding = await journal.GetProviderBindingAsync(context.User, sessionId, cancellationToken);
            await bindings.VerifyPinnedAsync(context.User, binding, cancellationToken);
            var page = await journal.ReplayAsync(
                context.User, new SessionEventPageRequest(sessionId, cursor, limit ?? 100), cancellationToken);
            return Results.Json(page, JsonOptions);
        }, cancellationToken);

    private static async Task<IResult> ReplayRunAsync(
        HttpContext context,
        string projectId,
        string runId,
        string? cursor,
        int? limit,
        ISessionsJournal journal,
        ISessionsProviderBinder bindings,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(async () =>
        {
            var binding = await journal.GetRunProviderBindingAsync(
                context.User, projectId, runId, cancellationToken);
            await bindings.VerifyPinnedAsync(context.User, binding, cancellationToken);
            var page = await journal.ReplayRunAsync(context.User,
                new SessionRunEventPageRequest(projectId, runId, cursor, limit ?? 100), cancellationToken);
            return Results.Json(page, JsonOptions);
        }, cancellationToken);

    private static async Task SubscribeAsync(
        HttpContext context,
        string sessionId,
        string? cursor,
        int? maximumEvents,
        int? maximumDurationSeconds,
        ISessionsJournal journal,
        ISessionsProviderBinder bindings,
        CancellationToken cancellationToken)
    {
        try
        {
            var binding = await journal.GetProviderBindingAsync(context.User, sessionId, cancellationToken);
            await bindings.VerifyPinnedAsync(context.User, binding, cancellationToken);
            context.Response.ContentType = "application/x-ndjson";
            await foreach (var item in journal.SubscribeAsync(context.User,
                new SessionSubscriptionRequest(
                    sessionId, cursor, maximumEvents ?? 1000, maximumDurationSeconds ?? 300),
                cancellationToken))
            {
                await JsonSerializer.SerializeAsync(context.Response.Body, item, JsonOptions, cancellationToken);
                await context.Response.WriteAsync("\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !context.Response.HasStarted)
        {
            await ErrorResult(exception).ExecuteAsync(context);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            context.Abort();
        }
    }

    private static async Task SubscribeRunAsync(
        HttpContext context,
        string projectId,
        string runId,
        string? cursor,
        int? maximumEvents,
        int? maximumDurationSeconds,
        ISessionsJournal journal,
        ISessionsProviderBinder bindings,
        CancellationToken cancellationToken)
    {
        try
        {
            var binding = await journal.GetRunProviderBindingAsync(
                context.User, projectId, runId, cancellationToken);
            await bindings.VerifyPinnedAsync(context.User, binding, cancellationToken);
            context.Response.ContentType = "application/x-ndjson";
            await foreach (var item in journal.SubscribeRunAsync(context.User,
                new SessionRunSubscriptionRequest(
                    projectId, runId, cursor, maximumEvents ?? 1000, maximumDurationSeconds ?? 300),
                cancellationToken))
            {
                await JsonSerializer.SerializeAsync(context.Response.Body, item, JsonOptions, cancellationToken);
                await context.Response.WriteAsync("\n", cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !context.Response.HasStarted)
        {
            await ErrorResult(exception).ExecuteAsync(context);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            context.Abort();
        }
    }

    private static async Task<IResult> ExecuteAsync(
        Func<Task<IResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoordinationOwnerClientException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: exception.StatusCode);
        }
        catch (RuntimeAuthorizationException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ProjectsAuthorizationContextException exception)
        {
            return Results.Json(new { error = exception.Code }, statusCode: (int)exception.StatusCode);
        }
        catch (SessionAuthenticationException)
        {
            return Results.Json(new { error = "invalid_run_claims" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        catch (SessionAccessDeniedException)
        {
            return Results.Json(new { error = "session_access_denied" }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (SessionNotFoundException)
        {
            return Results.Json(new { error = "session_not_found" }, statusCode: StatusCodes.Status404NotFound);
        }
        catch (SessionEventConflictException)
        {
            return Results.Json(new { error = "event_identity_conflict" }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (SessionForkUnsupportedException)
        {
            return Results.Json(new { error = "session_fork_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (SessionProviderBindingConflictException)
        {
            return Results.Json(new { error = "sessions_provider_binding_conflict" },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (SessionContractVersionException)
        {
            return Results.Json(new { error = "unsupported_contract_version" }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (ArgumentException)
        {
            return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (SessionsProviderUnavailableException)
        {
            return Results.Json(new { error = "sessions_provider_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (SessionPinnedProviderUnavailableException)
        {
            return Results.Json(new { error = "pinned_sessions_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Persistence and transport details can contain connection configuration; return
            // only a stable error code and never log request or exception contents here.
            return Results.Json(new { error = "journal_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static IResult ErrorResult(Exception exception) => exception switch
    {
        CoordinationOwnerClientException ownerException => Results.Json(
            new { error = ownerException.Code }, statusCode: ownerException.StatusCode),
        SessionAuthenticationException => Results.Json(
            new { error = "invalid_run_claims" }, statusCode: StatusCodes.Status401Unauthorized),
        SessionAccessDeniedException => Results.Json(
            new { error = "session_access_denied" }, statusCode: StatusCodes.Status403Forbidden),
        SessionNotFoundException => Results.Json(
            new { error = "session_not_found" }, statusCode: StatusCodes.Status404NotFound),
        SessionEventConflictException => Results.Json(
            new { error = "event_identity_conflict" }, statusCode: StatusCodes.Status409Conflict),
        SessionForkUnsupportedException => Results.Json(
            new { error = "session_fork_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
        SessionProviderBindingConflictException => Results.Json(
            new { error = "sessions_provider_binding_conflict" }, statusCode: StatusCodes.Status409Conflict),
        SessionContractVersionException => Results.Json(
            new { error = "unsupported_contract_version" }, statusCode: StatusCodes.Status422UnprocessableEntity),
        ArgumentException => Results.Json(
            new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest),
        SessionsProviderUnavailableException => Results.Json(
            new { error = "sessions_provider_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
        SessionPinnedProviderUnavailableException => Results.Json(
            new { error = "pinned_sessions_provider_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(
            new { error = "journal_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable)
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase));
        return options;
    }
}
