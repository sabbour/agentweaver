using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;

namespace Agentweaver.Knowledge;

public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var agents = app.MapGroup("/api/projects/{projectId}/runs/{runId}/agents/{agentId}")
            .RequireAuthorization();
        agents.MapPost("/records", CreateAsync);
        agents.MapGet("/records", SearchAsync);
        agents.MapGet("/records/{recordId:guid}", ReadAsync);
        agents.MapPut("/records/{recordId:guid}", UpdateAsync);
        agents.MapGet("/records/{recordId:guid}/revisions", ReadRevisionsAsync);
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

    private static Task<IResult> ReadAsync(
        string projectId,
        string runId,
        string agentId,
        Guid recordId,
        KnowledgeApplicationService service,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Results.Ok(await service.ReadAsync(
            projectId, runId, agentId, recordId, cancellationToken).ConfigureAwait(false)));

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
            _ => throw new InvalidOperationException("Unexpected Knowledge write status.")
        };

    private static IResult NotFound() =>
        Results.Problem(
            title: "record_not_found",
            detail: "The requested Knowledge record was not found for this project and agent.",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?> { ["code"] = "record_not_found" });

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
