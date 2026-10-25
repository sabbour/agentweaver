using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Knowledge;

/// <summary>Maps authenticated Knowledge routes to application operations and stable HTTP error responses.</summary>
public static class KnowledgeEndpoints
{
    /// <summary>Registers project/run/agent record routes and internal accepted-effect receipt routes.</summary>
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{projectId}/knowledge/records", SearchProjectAsync)
            .RequireAuthorization();

        // Projects & Config forwards the caller after checking the current project agent.
        app.MapGet(
                "/internal/projects/{projectId}/agents/{agentId}/history",
                ReadProjectAgentHistoryAsync)
            .RequireAuthorization();

        var agents = app.MapGroup("/api/projects/{projectId}/runs/{runId}/agents/{agentId}")
            .RequireAuthorization();
        agents.MapPost("/records", CreateAsync);
        agents.MapGet("/records", SearchAsync);
        agents.MapGet("/records/{recordId:guid}", ReadAsync);
        agents.MapPut("/records/{recordId:guid}", UpdateAsync);
        agents.MapGet("/records/{recordId:guid}/revisions", ReadRevisionsAsync);
        agents.MapPost("/records/{recordId:guid}/restore", RestoreAsync);
        agents.MapPost("/records/{recordId:guid}/approve", ApproveDecisionAsync);
        agents.MapGet("/records/export", ExportAsync);
        agents.MapPost("/records/import", ImportAsync)
            .WithMetadata(new RequestSizeLimitAttribute(KnowledgeRecordTransferContract.MaximumBytes));
        agents.MapPost("/proposals/{proposalId:guid}/promote", PromoteAsync);
        agents.MapPost("/proposals/{proposalId:guid}/reject", RejectAsync);
        agents.MapGet("/context", CompileContextAsync);

        app.MapGet("/internal/accepted-effects/{receiptId:guid}", ReadAcceptedEffectReceiptAsync)
            .RequireAuthorization();
        app.MapGet(
                "/internal/projects/{projectId}/runs/{runId}/accepted-effects/{receiptId:guid}",
                ReadScopedAcceptedEffectReceiptAsync)
            .RequireAuthorization();
    }

    /// <summary>Searches private Knowledge across currently authorized project-run provider bindings.</summary>
    private static Task<IResult> SearchProjectAsync(
        string projectId,
        KnowledgeRecordKind? kind,
        string? state,
        string? q,
        bool? includeInactive,
        int? page,
        int? pageSize,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return ExecuteAsync(async () => Results.Ok(await service.SearchProjectAsync(
            projectId,
            kind,
            state,
            q,
            includeInactive ?? false,
            page ?? 1,
            pageSize ?? 50,
            cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Reads a caller-authorized agent history without returning provider or run details.</summary>
    private static Task<IResult> ReadProjectAgentHistoryAsync(
        string projectId,
        string agentId,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        // Knowledge rechecks the caller's current private-content grant before searching any provider.
        return ExecuteAsync(async () => Results.Ok(await service.ReadProjectAgentHistoryAsync(
            projectId, agentId, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Creates a record and returns 201, or 200 when the idempotency receipt is reused.</summary>
    private static Task<IResult> CreateAsync(
        string projectId,
        string runId,
        string agentId,
        CreateKnowledgeRecordRequest request,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var result = await service.CreateAsync(
                projectId,
                runId,
                agentId,
                request,
                IdempotencyKey(httpContext),
                cancellationToken).ConfigureAwait(false);
            return Results.Json(
                result,
                statusCode: result.IsDuplicate ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        });

    /// <summary>Searches records using defaults for omitted page and filter values.</summary>
    private static Task<IResult> SearchAsync(
        string projectId,
        string runId,
        string agentId,
        KnowledgeRecordKind? kind,
        string? q,
        bool? includeInactive,
        int? page,
        int? pageSize,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var result = await service.SearchAsync(
                projectId,
                runId,
                agentId,
                kind,
                q,
                includeInactive ?? false,
                page ?? 1,
                pageSize ?? 50,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });

    /// <summary>Reads one record in the route's project and agent scope.</summary>
    private static Task<IResult> ReadAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.ReadAsync(
            projectId, runId, agentId, recordId, cancellationToken).ConfigureAwait(false)));

    /// <summary>Applies an idempotent, revision-checked record update.</summary>
    private static Task<IResult> UpdateAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        UpdateKnowledgeRecordRequest request,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => ToWriteResult(await service.UpdateAsync(
            projectId,
            runId,
            agentId,
            recordId,
            request,
            IdempotencyKey(httpContext),
            cancellationToken).ConfigureAwait(false)));

    /// <summary>Returns a page of revisions after the application service verifies record ownership.</summary>
    private static Task<IResult> ReadRevisionsAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        int? page,
        int? pageSize,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.ReadRevisionsAsync(
            projectId, runId, agentId, recordId, page ?? 1, pageSize ?? 50, cancellationToken)
            .ConfigureAwait(false)));

    /// <summary>Restores one historical revision through the application service.</summary>
    private static Task<IResult> RestoreAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        RestoreKnowledgeRecordRequest request,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => ToWriteResult(await service.RestoreAsync(
            projectId,
            runId,
            agentId,
            recordId,
            request,
            IdempotencyKey(httpContext),
            cancellationToken).ConfigureAwait(false)));

    /// <summary>Approves a Decision and maps provider write outcomes to HTTP status codes.</summary>
    private static Task<IResult> ApproveDecisionAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        ApproveKnowledgeDecisionRequest request,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => ToWriteResult(await service.ApproveDecisionAsync(
            projectId,
            runId,
            agentId,
            recordId,
            request,
            IdempotencyKey(httpContext),
            cancellationToken).ConfigureAwait(false)));

    /// <summary>Exports the agent's Knowledge bundle and disables response caching.</summary>
    private static Task<IResult> ExportAsync(
        string projectId,
        string runId,
        string agentId,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return ExecuteAsync(async () => Results.Ok(await service.ExportAsync(
            projectId, runId, agentId, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Imports a validated bundle and returns 200 for duplicate idempotent submissions.</summary>
    private static Task<IResult> ImportAsync(
        string projectId,
        string runId,
        string agentId,
        KnowledgeRecordTransferBundle request,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var result = await service.ImportAsync(
                projectId,
                runId,
                agentId,
                request,
                IdempotencyKey(httpContext),
                cancellationToken).ConfigureAwait(false);
            return Results.Json(result, statusCode: result.IsDuplicate
                ? StatusCodes.Status200OK
                : StatusCodes.Status201Created);
        });

    /// <summary>Promotes a Proposal and maps stale, invalid-state, and not-found results to problem responses.</summary>
    private static Task<IResult> PromoteAsync(
        string projectId,
        string runId,
        string agentId,
        Guid proposalId,
        PromoteKnowledgeProposalRequest request,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var result = await service.PromoteAsync(
                projectId,
                runId,
                agentId,
                proposalId,
                request,
                IdempotencyKey(httpContext),
                cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                KnowledgeWriteStatus.Updated => Results.Json(
                    result, statusCode: result.IsDuplicate ? StatusCodes.Status200OK : StatusCodes.Status201Created),
                KnowledgeWriteStatus.NotFound => NotFound(),
                KnowledgeWriteStatus.Stale => Conflict("stale_revision", result.CurrentRevision),
                KnowledgeWriteStatus.InvalidState => Conflict("proposal_not_promotable", result.CurrentRevision),
                _ => throw new InvalidOperationException("Unexpected proposal promotion status.")
            };
        });

    /// <summary>Rejects a Proposal through the current run's provider and idempotency receipt.</summary>
    private static Task<IResult> RejectAsync(
        string projectId,
        string runId,
        string agentId,
        Guid proposalId,
        RejectKnowledgeProposalRequest request,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => ToWriteResult(await service.RejectAsync(
            projectId,
            runId,
            agentId,
            proposalId,
            request,
            IdempotencyKey(httpContext),
            cancellationToken).ConfigureAwait(false)));

    /// <summary>Returns bounded structured context and its source-revision references.</summary>
    private static Task<IResult> CompileContextAsync(
        string projectId,
        string runId,
        string agentId,
        string? q,
        int? maxItems,
        int? maxTokens,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.CompileContextAsync(
            projectId, runId, agentId, q, maxItems, maxTokens, cancellationToken).ConfigureAwait(false)));

    /// <summary>Reads a receipt by ID and disables caching of the authorization-sensitive response.</summary>
    private static Task<IResult> ReadAcceptedEffectReceiptAsync(
        Guid receiptId,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return ExecuteAsync(async () => Results.Ok(
            await service.ReadAcceptedEffectReceiptAsync(receiptId, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Reads a receipt within a project/run route and disables caching of the response.</summary>
    private static Task<IResult> ReadScopedAcceptedEffectReceiptAsync(
        string projectId,
        string runId,
        Guid receiptId,
        HttpContext httpContext,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return ExecuteAsync(async () => Results.Ok(
            await service.ReadAcceptedEffectReceiptAsync(
                projectId, runId, receiptId, cancellationToken).ConfigureAwait(false)));
    }

    /// <summary>Converts known application and provider failures to stable RFC problem responses.</summary>
    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (KnowledgeApiException exception)
        {
            return Results.Problem(
                title: exception.Code,
                detail: exception.Message,
                statusCode: exception.StatusCode,
                extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
        }
        catch (KnowledgeStorageUnavailableException)
        {
            return Results.Problem(
                title: "knowledge_storage_unavailable",
                detail: "Knowledge storage is unavailable; the operation was not completed.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["code"] = "knowledge_storage_unavailable" });
        }
        catch (KnowledgeProviderUnavailableException exception)
        {
            return Results.Problem(
                title: "memory_provider_unavailable",
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["code"] = "memory_provider_unavailable" });
        }
        catch (KnowledgeContextCandidateLimitException exception)
        {
            return Results.Problem(
                title: "context_candidate_limit_exceeded",
                detail: exception.Message,
                statusCode: StatusCodes.Status413PayloadTooLarge,
                extensions: new Dictionary<string, object?> { ["code"] = "context_candidate_limit_exceeded" });
        }
        catch (MandatoryContextBudgetExceededException exception)
        {
            return Results.Problem(
                title: "mandatory_context_budget_exceeded",
                detail: exception.Message,
                statusCode: StatusCodes.Status413PayloadTooLarge,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "mandatory_context_budget_exceeded",
                    ["budgetCharacters"] = exception.BudgetCharacters,
                    ["requiredCharacters"] = exception.RequiredCharacters
                });
        }
    }

    /// <summary>Maps record-write statuses to success, not-found, or conflict HTTP results.</summary>
    private static IResult ToWriteResult(KnowledgeRecordWriteResult result) =>
        result.Status switch
        {
            KnowledgeWriteStatus.Created or KnowledgeWriteStatus.Updated =>
                Results.Json(result, statusCode: result.IsDuplicate
                    ? StatusCodes.Status200OK
                    : result.Status == KnowledgeWriteStatus.Created
                        ? StatusCodes.Status201Created
                        : StatusCodes.Status200OK),
            KnowledgeWriteStatus.NotFound => NotFound(),
            KnowledgeWriteStatus.Stale => Conflict("stale_revision", result.CurrentRevision),
            KnowledgeWriteStatus.IdempotencyConflict => Conflict("idempotency_conflict", result.CurrentRevision),
            KnowledgeWriteStatus.InvalidState => Conflict("invalid_record_state", result.CurrentRevision),
            KnowledgeWriteStatus.InvalidReplacement => Conflict("invalid_replacement", result.CurrentRevision),
            KnowledgeWriteStatus.ReplacementCycle => Conflict("replacement_cycle", result.CurrentRevision),
            _ => throw new InvalidOperationException("Unexpected Knowledge write status.")
        };

    /// <summary>Builds the scoped not-found response without revealing whether another agent owns the record.</summary>
    private static IResult NotFound() =>
        Results.Problem(
            title: "record_not_found",
            detail: "The requested Knowledge record was not found for this project and agent.",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?> { ["code"] = "record_not_found" });

    /// <summary>Builds a 409 response and includes the current revision only when the provider returned one.</summary>
    private static IResult Conflict(string code, int? currentRevision) =>
        Results.Problem(
            title: code,
            detail: currentRevision is null
                ? "The Knowledge write conflicts with the current record state."
                : $"The current Knowledge revision is {currentRevision}.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["currentRevision"] = currentRevision
            });

    /// <summary>Requires exactly one non-empty idempotency key for every write endpoint.</summary>
    private static string IdempotencyKey(HttpContext context)
    {
        var values = context.Request.Headers["Idempotency-Key"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            throw new KnowledgeApiException(
                "idempotency_key_required",
                "A single valid Idempotency-Key header is required for Knowledge writes.",
                StatusCodes.Status400BadRequest);
        return values[0]!;
    }
}
