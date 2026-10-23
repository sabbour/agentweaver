using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Projects.Config;

/// <summary>Run-bound generation request metadata returned to trusted service callers.</summary>
public sealed record ProjectCastingGenerationRequest(
    Guid RequestId,
    Guid ProposalId,
    string ProjectId,
    long BaseConfigurationRevision,
    string RunId,
    ProjectCastingGenerationMode Mode,
    ProjectCastingGenerationRequestState State,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Safe generation state attached to a casting proposal returned for review.</summary>
public sealed record ProjectCastingGenerationStatus(
    Guid RequestId,
    ProjectCastingGenerationMode Mode,
    ProjectCastingGenerationRequestState State,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed partial class ProjectsConfigService
{
    /// <summary>Creates an empty review proposal and binds it to one run before generation begins.</summary>
    public Task<ProjectCastingGenerationRequest> StartCastingGenerationAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        StartProjectCastingGenerationRequest request,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateCastingGenerationStart(request);

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            RequireActiveCastingProject(project);

            var existing = await context.CastingGenerationRequests.SingleOrDefaultAsync(
                item => item.ProjectId == project.ProjectId &&
                    item.ActorIssuer == caller.Issuer &&
                    item.ActorId == caller.ActorId &&
                    item.IdempotencyKey == request.IdempotencyKey,
                token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.BaseConfigurationRevision != request.ExpectedConfigurationRevision ||
                    existing.Mode != request.Mode ||
                    !string.Equals(existing.RequestDigest, request.RequestDigest, StringComparison.Ordinal))
                    throw CastingGenerationIdempotencyConflict();

                // A replay reuses the first run binding; changed live configuration fences unfinished work as stale.
                if (existing.State == ProjectCastingGenerationRequestState.Pending &&
                    existing.BaseConfigurationRevision != project.ConfigurationRevision)
                {
                    existing.State = ProjectCastingGenerationRequestState.Stale;
                    existing.UpdatedAt = timeProvider.GetUtcNow();
                    await context.SaveChangesAsync(token).ConfigureAwait(false);
                }
                return ToCastingGenerationRequest(existing);
            }

            var runBinding = await context.CastingGenerationRequests.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == project.ProjectId && item.RunId == request.RunId,
                    token)
                .ConfigureAwait(false);
            if (runBinding is not null)
                throw CastingGenerationConflict("The run is already bound to another casting generation request.");

            if (project.ConfigurationRevision != request.ExpectedConfigurationRevision)
                throw CastingConfigurationConflict();

            var currentRevision = await context.ProjectConfigurationRevisions.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == project.ProjectId &&
                        item.Revision == project.ConfigurationRevision,
                    token)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var current = Deserialize<ProjectConfiguration>(currentRevision.ConfigurationJson);
            var proposal = await AddCastingProposalAsync(
                context, caller, project, current, transferProvenance: null, token).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var generation = new ProjectCastingGenerationRequestRecord
            {
                RequestId = Guid.NewGuid(),
                ProposalId = proposal.ProposalId,
                ProjectId = project.ProjectId,
                BaseConfigurationRevision = project.ConfigurationRevision,
                RunId = request.RunId,
                Mode = request.Mode,
                State = ProjectCastingGenerationRequestState.Pending,
                ActorIssuer = caller.Issuer,
                ActorId = caller.ActorId,
                IdempotencyKey = request.IdempotencyKey,
                RequestDigest = request.RequestDigest,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.CastingGenerationRequests.Add(generation);
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            return ToCastingGenerationRequest(generation);
        }, cancellationToken);
    }

    /// <summary>Stores generated roster data as a proposal only after matching its run and base revision.</summary>
    public Task<ProjectCastingGenerationRequest> CompleteCastingGenerationAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid requestId,
        CompleteProjectCastingGenerationRequest request,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        ValidateCastingGenerationRequestId(requestId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateCastingGenerationRunId(request.RunId);
        if (request.AgentCharters.IsDefault || request.Casting.IsDefault)
            throw ProjectConfigException.Invalid(
                "invalid_casting_generation_result",
                "Generated charters and casting must be provided.");
        var outputDigest = HashFingerprint(new
        {
            agentCharters = request.AgentCharters,
            casting = request.Casting,
        });

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            RequireActiveCastingProject(project);
            var generation = await LockCastingGenerationRequestAsync(
                context, project.ProjectId, requestId, token).ConfigureAwait(false);
            RequireCastingGenerationRun(generation, request.RunId);
            var proposal = await LockCastingProposalAsync(
                context, project.ProjectId, generation.ProposalId, token).ConfigureAwait(false);

            if (generation.State == ProjectCastingGenerationRequestState.Completed)
            {
                if (!string.Equals(generation.OutputDigest, outputDigest, StringComparison.Ordinal))
                    throw CastingGenerationConflict("A completed generation cannot be replaced by different output.");
                return ToCastingGenerationRequest(generation);
            }
            // Cancellation and staleness are terminal; late model output must not revive a proposal.
            if (generation.State is ProjectCastingGenerationRequestState.Cancelled or
                ProjectCastingGenerationRequestState.Stale)
                return ToCastingGenerationRequest(generation);
            if (generation.State != ProjectCastingGenerationRequestState.Pending)
                throw CastingGenerationConflict("The casting generation request is no longer pending.");
            if (proposal.State != ProjectCastingProposalState.Pending)
            {
                generation.State = ProjectCastingGenerationRequestState.Cancelled;
                generation.UpdatedAt = timeProvider.GetUtcNow();
                await context.SaveChangesAsync(token).ConfigureAwait(false);
                return ToCastingGenerationRequest(generation);
            }
            if (project.ConfigurationRevision != generation.BaseConfigurationRevision ||
                proposal.BaseConfigurationRevision != generation.BaseConfigurationRevision)
            {
                generation.State = ProjectCastingGenerationRequestState.Stale;
                generation.UpdatedAt = timeProvider.GetUtcNow();
                await context.SaveChangesAsync(token).ConfigureAwait(false);
                return ToCastingGenerationRequest(generation);
            }

            var currentRevision = await context.ProjectConfigurationRevisions.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == project.ProjectId &&
                        item.Revision == project.ConfigurationRevision,
                    token)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var current = Deserialize<ProjectConfiguration>(currentRevision.ConfigurationJson);
            var draft = ProjectConfigurationValidator.ValidateTransition(
                current,
                current with
                {
                    AgentCharters = request.AgentCharters,
                    Casting = request.Casting,
                });
            await EnsureCastingProjectNarrowingAsync(context, draft, token).ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            proposal.DraftJson = Serialize(draft);
            proposal.DraftRevision = checked(proposal.DraftRevision + 1);
            proposal.UpdatedByActorId = caller.ActorId;
            proposal.UpdatedAt = now;
            generation.State = ProjectCastingGenerationRequestState.Completed;
            generation.OutputDigest = outputDigest;
            generation.UpdatedAt = now;
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            return ToCastingGenerationRequest(generation);
        }, cancellationToken);
    }

    /// <summary>Records a bounded failure code without retaining model output, credentials, or exception text.</summary>
    public Task<ProjectCastingGenerationRequest> FailCastingGenerationAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid requestId,
        FailProjectCastingGenerationRequest request,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        ValidateCastingGenerationRequestId(requestId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateCastingGenerationRunId(request.RunId);
        if (!IsSafeCastingGenerationFailureCode(request.FailureCode))
            throw ProjectConfigException.Invalid(
                "invalid_casting_generation_failure",
                "The generation failure code is invalid.");

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            var generation = await LockCastingGenerationRequestAsync(
                context, project.ProjectId, requestId, token).ConfigureAwait(false);
            RequireCastingGenerationRun(generation, request.RunId);
            if (generation.State == ProjectCastingGenerationRequestState.Failed &&
                string.Equals(generation.FailureCode, request.FailureCode, StringComparison.Ordinal))
                return ToCastingGenerationRequest(generation);
            if (generation.State != ProjectCastingGenerationRequestState.Pending)
                throw CastingGenerationConflict("The casting generation request is no longer pending.");

            generation.State = ProjectCastingGenerationRequestState.Failed;
            generation.FailureCode = request.FailureCode;
            generation.UpdatedAt = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            return ToCastingGenerationRequest(generation);
        }, cancellationToken);
    }

    /// <summary>Reads one generation request after checking current project read authority.</summary>
    public Task<ProjectCastingGenerationRequest> GetCastingGenerationRequestAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        RequireCastingRead(caller, projectId);
        ValidateCastingGenerationRequestId(requestId);
        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: false, token).ConfigureAwait(false);
            var generation = await context.CastingGenerationRequests.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == project.ProjectId && item.RequestId == requestId,
                    token)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            return ToCastingGenerationRequest(generation);
        }, cancellationToken);
    }

    private static async Task<ProjectCastingGenerationRequestRecord> LockCastingGenerationRequestAsync(
        ProjectsConfigDbContext context,
        string projectId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        await using var requestLock = new NpgsqlCommand(
            """
            SELECT request_id
            FROM projects_config.casting_generation_requests
            WHERE request_id = @request_id
              AND project_id = @project_id
            FOR UPDATE
            """,
            connection,
            transaction);
        requestLock.Parameters.AddWithValue("request_id", requestId);
        requestLock.Parameters.AddWithValue("project_id", projectId);
        if (await requestLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw ProjectConfigException.NotFound();

        return await context.CastingGenerationRequests.SingleAsync(
            item => item.RequestId == requestId && item.ProjectId == projectId,
            cancellationToken).ConfigureAwait(false);
    }

    private static ProjectCastingGenerationRequest ToCastingGenerationRequest(
        ProjectCastingGenerationRequestRecord request) =>
        new(
            request.RequestId,
            request.ProposalId,
            request.ProjectId,
            request.BaseConfigurationRevision,
            request.RunId,
            request.Mode,
            request.State,
            request.FailureCode,
            request.CreatedAt,
            request.UpdatedAt);

    private static ProjectCastingGenerationStatus ToCastingGenerationStatus(
        ProjectCastingGenerationRequestRecord request) =>
        new(
            request.RequestId,
            request.Mode,
            request.State,
            request.FailureCode,
            request.CreatedAt,
            request.UpdatedAt);

    private static ProjectCastingProposal ToCastingProposal(
        ProjectCastingProposalRecord proposal,
        ProjectCastingGenerationRequestRecord? generation) =>
        ToCastingProposal(proposal) with
        {
            Generation = generation is null ? null : ToCastingGenerationStatus(generation),
        };

    private static void ValidateCastingGenerationStart(StartProjectCastingGenerationRequest request)
    {
        if (request.ExpectedConfigurationRevision <= 0 ||
            !Enum.IsDefined(request.Mode) ||
            !IsValidCastingGenerationRunId(request.RunId) ||
            !IsValidCastingGenerationIdempotencyKey(request.IdempotencyKey) ||
            !IsLowercaseCastingGenerationDigest(request.RequestDigest))
            throw ProjectConfigException.Invalid(
                "invalid_casting_generation_request",
                "The generation mode, run ID, idempotency key, revision, or request digest is invalid.");
    }

    private static void ValidateCastingGenerationRequestId(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw ProjectConfigException.NotFound();
    }

    private static void ValidateCastingGenerationRunId(string runId)
    {
        if (!IsValidCastingGenerationRunId(runId))
            throw ProjectConfigException.Invalid(
                "invalid_casting_generation_run",
                "The generation run ID is invalid.");
    }

    private static bool IsValidCastingGenerationRunId(string? value) =>
        value is { Length: > 0 and <= 256 } &&
        !value.Any(char.IsControl) &&
        !string.IsNullOrWhiteSpace(value);

    private static bool IsValidCastingGenerationIdempotencyKey(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool IsLowercaseCastingGenerationDigest(string? value) =>
        value is { Length: 64 } &&
        value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static bool IsSafeCastingGenerationFailureCode(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool IsCastingGenerationIdempotencyRace(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "ux_casting_generation_requests_idempotency" or
                        "ux_casting_generation_requests_project_run",
                })
                return true;
        }
        return false;
    }

    private static void RequireCastingGenerationRun(
        ProjectCastingGenerationRequestRecord request,
        string runId)
    {
        if (!string.Equals(request.RunId, runId, StringComparison.Ordinal))
            throw CastingGenerationConflict("The generation result does not match the request's run.");
    }

    private static ProjectConfigException CastingGenerationIdempotencyConflict() =>
        ProjectConfigException.Conflict(
            "casting_generation_idempotency_conflict",
            "The idempotency key already names a different casting generation request.");

    private static ProjectConfigException CastingGenerationConflict(string message) =>
        ProjectConfigException.Conflict("casting_generation_conflict", message);
}
