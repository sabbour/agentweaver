using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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

        return app;
    }

    private static async Task<IResult> CreateRunAsync(
        CreateRunRequest request,
        IRunRepository runRepository,
        RunExecutionService executionService,
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

        // Enqueue execution on a background thread (fire and forget with structured cancellation)
        _ = Task.Run(
            () => executionService.ExecuteAsync(run.Id, CancellationToken.None),
            CancellationToken.None);

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
}
