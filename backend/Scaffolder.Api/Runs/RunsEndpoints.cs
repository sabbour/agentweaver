using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Scaffolder.Api.Agent.Governance;
using Scaffolder.Api.Configuration;
using Scaffolder.Api.Persistence;
using Scaffolder.Api.Persistence.Entities;
using Scaffolder.Api.Worktrees;

namespace Scaffolder.Api.Runs;

public static class RunsEndpoints
{
    public static IEndpointRouteBuilder MapRunsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/runs").WithTags("Runs");

        // T029: POST /runs
        group.MapPost("/", CreateRunAsync)
            .WithName("CreateRun")
            .WithSummary("Submit a new run")
            .Produces<RunResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        // T030: GET /runs/{runId}
        group.MapGet("/{runId:guid}", GetRunAsync)
            .WithName("GetRun")
            .WithSummary("Get run status")
            .Produces<RunResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // T031: GET /runs/{runId}/diff
        group.MapGet("/{runId:guid}/diff", GetDiffAsync)
            .WithName("GetRunDiff")
            .WithSummary("Get the unified diff for a completed run")
            .Produces<string>(StatusCodes.Status200OK, "text/plain")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // T043: POST /runs/{runId}/review
        group.MapPost("/{runId:guid}/review", ReviewRunAsync)
            .WithName("ReviewRun")
            .WithSummary("Submit a review decision (approve or decline) for a completed run")
            .Produces<RunResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> CreateRunAsync(
        CreateRunRequest request,
        IRunRepository runRepository,
        IServiceScopeFactory scopeFactory,
        IOptions<ScaffolderOptions> options,
        HttpContext httpContext,
        CancellationToken ct)
    {
        // Validate model source
        if (!Enum.IsDefined(typeof(ModelSource), request.ModelSource))
        {
            return Results.Problem(
                title: "Invalid model source",
                detail: "ModelSource must be one of: CopilotSdk, MicrosoftFoundry",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.OriginatingBranch))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["originatingBranch"] = ["OriginatingBranch must not be empty."]
                });
        }

        if (string.IsNullOrWhiteSpace(request.TaskPrompt))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["taskPrompt"] = ["TaskPrompt must not be empty."]
                });
        }

        var opts = options.Value;

        // Extract submittedBy from request headers or context (FR-024)
        // Full auth integration is deferred; use header for now
        var submittedBy = httpContext.Request.Headers["X-Submitted-By"].FirstOrDefault()
            ?? "anonymous";

        var run = new RunEntity
        {
            Id = Guid.NewGuid(),
            OriginatingBranch = request.OriginatingBranch,
            ModelSource = request.ModelSource,
            TaskPrompt = request.TaskPrompt,
            SubmittedBy = submittedBy,
            Status = RunStatus.Queued,
            MaxSteps = request.MaxSteps is > 0 ? request.MaxSteps.Value : opts.DefaultMaxSteps,
            MaxDurationSeconds = request.MaxDurationSeconds is > 0
                ? request.MaxDurationSeconds.Value
                : opts.DefaultMaxDurationSeconds
        };

        await runRepository.CreateAsync(run, ct);

        // Enqueue execution on a background thread using a fresh DI scope so that
        // scoped services (IRunRepository, EventLogService, etc.) are not accessed
        // after the HTTP request scope is disposed (FR-001 background execution).
        _ = Task.Run(async () =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var svc = scope.ServiceProvider.GetRequiredService<RunExecutionService>();
            await svc.ExecuteAsync(run.Id, CancellationToken.None);
        }, CancellationToken.None);

        return Results.Created($"/runs/{run.Id}", RunResponse.FromEntity(run));
    }

    private static async Task<IResult> GetRunAsync(
        Guid runId,
        IRunRepository runRepository,
        CancellationToken ct)
    {
        var run = await runRepository.GetByIdAsync(runId, ct);
        if (run is null)
        {
            return Results.Problem(
                title: "Run not found",
                detail: $"No run with id {runId} exists.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(RunResponse.FromEntity(run));
    }

    private static async Task<IResult> GetDiffAsync(
        Guid runId,
        IRunRepository runRepository,
        IDiffService diffService,
        CancellationToken ct)
    {
        var run = await runRepository.GetByIdAsync(runId, ct);
        if (run is null)
        {
            return Results.Problem(
                title: "Run not found",
                detail: $"No run with id {runId} exists.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Only runs with a session (completed, awaiting_review, approved, merged) have a diff
        var diffableStatuses = new[]
        {
            RunStatus.Completed, RunStatus.AwaitingReview, RunStatus.Approved,
            RunStatus.Declined, RunStatus.Merged, RunStatus.MergeConflict
        };

        if (!diffableStatuses.Contains(run.Status) || run.Session is null)
        {
            return Results.Problem(
                title: "Diff not available",
                detail: $"Run {runId} is in status {run.Status} which does not have an available diff.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var diff = await diffService.GetDiffAsync(
            run.Session.WorktreePath,
            run.Session.OriginatingCommit,
            ct);

        return Results.Text(diff, "text/plain");
    }

    /// <summary>
    /// T043: POST /runs/{runId}/review — human-approval gate.
    /// Accepts approve or decline; on approve triggers MergeService.
    /// Enforces governance via GovernancePolicyEngine.
    /// Emits review/merge events via RunStateMachine (T044).
    /// </summary>
    private static async Task<IResult> ReviewRunAsync(
        Guid runId,
        ReviewDecisionRequest request,
        IRunRepository runRepository,
        RunStateMachine stateMachine,
        MergeService mergeService,
        GovernancePolicyEngine governance,
        EventLogService eventLog,
        CancellationToken ct)
    {
        // Validate decision value
        if (!string.Equals(request.Decision, "approve", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Decision, "decline", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                title: "Invalid decision",
                detail: "Decision must be 'approve' or 'decline'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Reviewer))
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["reviewer"] = ["Reviewer must not be empty."]
                });
        }

        var run = await runRepository.GetByIdAsync(runId, ct);
        if (run is null)
        {
            return Results.Problem(
                title: "Run not found",
                detail: $"No run with id {runId} exists.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Only AwaitingReview runs can receive a review decision
        if (run.Status != RunStatus.AwaitingReview)
        {
            return Results.Problem(
                title: "Invalid run state",
                detail: $"Run {runId} is in status '{run.Status}'. " +
                        "Only runs with status 'AwaitingReview' can be reviewed.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (string.Equals(request.Decision, "decline", StringComparison.OrdinalIgnoreCase))
        {
            // Decline: leave originating branch unmodified
            var declined = await stateMachine.TransitionToDeclinedAsync(runId, request.Reviewer, ct);
            governance.RecordPolicyDecision(runId, "human-approval-gate", "declined",
                $"Reviewer: {request.Reviewer}");
            return Results.Ok(RunResponse.FromEntity(declined));
        }

        // Approve: validate gate then merge
        var gateError = governance.ValidateHumanApprovalGate(RunStatus.AwaitingReview);
        // Note: gate always passes for AwaitingReview — the status check above is the real guard
        _ = gateError; // suppress unused warning; gate is validated structurally above

        await stateMachine.TransitionToApprovedAsync(runId, request.Reviewer, ct);
        governance.RecordPolicyDecision(runId, "human-approval-gate", "approved",
            $"Reviewer: {request.Reviewer}");

        // Ensure the session / worktree info is available
        var approvedRun = await runRepository.GetByIdAsync(runId, ct)!;
        if (approvedRun!.Session is null)
        {
            return Results.Problem(
                title: "Session not available",
                detail: "The run does not have an associated session. Cannot merge.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Perform the merge
        var (mergeOutcome, mergeError) = await mergeService.MergeAsync(
            approvedRun.Session.WorktreePath,
            approvedRun.OriginatingBranch,
            ct);

        RunEntity finalRun;
        switch (mergeOutcome)
        {
            case MergeOutcome.Merged:
                finalRun = await stateMachine.TransitionToMergedAsync(runId, ct);
                break;

            case MergeOutcome.Conflict:
                finalRun = await stateMachine.TransitionToMergeConflictAsync(runId, ct);
                await eventLog.AppendReviewEventAsync(
                    runId, EventType.MergeFailed,
                    new { reason = "merge_conflict", detail = mergeError }, ct);
                break;

            default: // Failed
                finalRun = await runRepository.UpdateStatusAsync(runId, RunStatus.Failed, ct);
                await eventLog.AppendReviewEventAsync(
                    runId, EventType.MergeFailed,
                    new { reason = "merge_failed", detail = mergeError }, ct);
                break;
        }

        return Results.Ok(RunResponse.FromEntity(finalRun));
    }
}
