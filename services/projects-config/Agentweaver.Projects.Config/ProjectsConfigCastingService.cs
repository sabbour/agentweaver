using System.Collections.Immutable;
using System.Data;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Agentweaver.Projects.Config;

public sealed record ProjectCastingProposal(
    Guid ProposalId,
    string ProjectId,
    long BaseConfigurationRevision,
    long DraftRevision,
    ProjectCastingProposalState State,
    ProjectConfiguration Draft,
    long? ConfirmedConfigurationRevision,
    ProjectConfiguration? Result,
    ProjectCastingTransferProvenance? TransferProvenance,
    string CreatedByActorId,
    DateTimeOffset CreatedAt,
    string UpdatedByActorId,
    DateTimeOffset UpdatedAt);

public sealed partial class ProjectsConfigService
{
    private const int CastingProposalTransactionAttempts = 3;

    public Task<ProjectCastingProposal> CreateCastingProposalAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedConfigurationRevision,
        ProjectConfiguration draft,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        var normalized = ProjectConfigurationValidator.Validate(draft);
        return CreateCastingProposalAsync(
            caller,
            projectId,
            expectedConfigurationRevision,
            normalized,
            transferProvenance: null,
            cancellationToken);
    }

    public Task<ProjectCastingProposal> CreateScenarioCastingProposalAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedConfigurationRevision,
        string templateId,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        return CreateCastingProposalFromCurrentConfigurationAsync(
            caller,
            projectId,
            expectedConfigurationRevision,
            current => ProjectCastingCatalog.CreateScenarioDraft(current, templateId),
            cancellationToken);
    }

    public Task<ProjectCastingProposal> CreateManualCastingProposalAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedConfigurationRevision,
        ImmutableArray<string> roleIds,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        return CreateCastingProposalFromCurrentConfigurationAsync(
            caller,
            projectId,
            expectedConfigurationRevision,
            current => ProjectCastingCatalog.CreateManualDraft(current, roleIds),
            cancellationToken);
    }

    private Task<ProjectCastingProposal> CreateCastingProposalAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedConfigurationRevision,
        ProjectConfiguration normalized,
        ProjectCastingTransferProvenance? transferProvenance,
        CancellationToken cancellationToken)
        => CreateCastingProposalFromCurrentConfigurationAsync(
            caller,
            projectId,
            expectedConfigurationRevision,
            _ => normalized,
            cancellationToken,
            transferProvenance);

    private Task<ProjectCastingProposal> CreateCastingProposalFromCurrentConfigurationAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long expectedConfigurationRevision,
        Func<ProjectConfiguration, ProjectConfiguration> createDraft,
        CancellationToken cancellationToken,
        ProjectCastingTransferProvenance? transferProvenance = null)
    {
        if (expectedConfigurationRevision <= 0)
            throw ProjectConfigException.Invalid(
                "invalid_configuration_revision",
                "The expected project configuration revision must be positive.");

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            RequireActiveCastingProject(project);
            if (project.ConfigurationRevision != expectedConfigurationRevision)
                throw CastingConfigurationConflict();
            var currentConfiguration = await context.ProjectConfigurationRevisions.AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.ProjectId == project.ProjectId && record.Revision == project.ConfigurationRevision,
                    token)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var normalized = ProjectConfigurationValidator.Validate(
                createDraft(Deserialize<ProjectConfiguration>(currentConfiguration.ConfigurationJson)));
            await EnsureCastingProjectNarrowingAsync(context, normalized, token).ConfigureAwait(false);
            return await AddCastingProposalAsync(
                context, caller, project, normalized, transferProvenance, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<ProjectCastingProposal>> ListCastingProposalsAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        int limit,
        CancellationToken cancellationToken)
    {
        RequireCastingRead(caller, projectId);
        if (limit is < 1 or > 100)
            throw ProjectConfigException.Invalid("invalid_limit", "The proposal limit must be between 1 and 100.");

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: false, token).ConfigureAwait(false);
            var proposals = await context.CastingProposals.AsNoTracking()
                .Where(proposal => proposal.ProjectId == project.ProjectId)
                .OrderByDescending(proposal => proposal.CreatedAt)
                .ThenByDescending(proposal => proposal.ProposalId)
                .Take(limit)
                .ToArrayAsync(token)
                .ConfigureAwait(false);
            return (IReadOnlyList<ProjectCastingProposal>)proposals.Select(ToCastingProposal).ToArray();
        }, cancellationToken);
    }

    public Task<ProjectCastingProposal> GetCastingProposalAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        RequireCastingRead(caller, projectId);
        ValidateCastingProposalId(proposalId);

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: false, token).ConfigureAwait(false);
            var proposal = await LockCastingProposalAsync(context, project.ProjectId, proposalId, token)
                .ConfigureAwait(false);
            return ToCastingProposal(proposal);
        }, cancellationToken);
    }

    public Task<ProjectCastingProposal> UpdateCastingProposalAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid proposalId,
        long expectedDraftRevision,
        ProjectConfiguration draft,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        ValidateCastingProposalId(proposalId);
        ValidateExpectedDraftRevision(expectedDraftRevision);
        var normalized = ProjectConfigurationValidator.Validate(draft);

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            RequireActiveCastingProject(project);
            var proposal = await LockCastingProposalAsync(context, project.ProjectId, proposalId, token)
                .ConfigureAwait(false);
            RequirePendingCastingProposal(proposal, expectedDraftRevision);
            if (proposal.BaseConfigurationRevision != project.ConfigurationRevision)
                throw CastingConfigurationConflict();
            await EnsureCastingProjectNarrowingAsync(context, normalized, token).ConfigureAwait(false);

            proposal.DraftJson = Serialize(normalized);
            proposal.DraftRevision = checked(proposal.DraftRevision + 1);
            proposal.UpdatedByActorId = caller.ActorId;
            proposal.UpdatedAt = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            return ToCastingProposal(proposal);
        }, cancellationToken);
    }

    public Task<ProjectCastingProposal> ConfirmCastingProposalAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid proposalId,
        long expectedDraftRevision,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        ValidateCastingProposalId(proposalId);
        ValidateExpectedDraftRevision(expectedDraftRevision);

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            var proposal = await LockCastingProposalAsync(context, project.ProjectId, proposalId, token)
                .ConfigureAwait(false);

            if (proposal.State != ProjectCastingProposalState.Pending)
            {
                if (proposal.State == ProjectCastingProposalState.Confirmed &&
                    proposal.DraftRevision == expectedDraftRevision)
                    return ToCastingProposal(proposal);
                throw CastingProposalConflict(
                    proposal.State == ProjectCastingProposalState.Rejected
                        ? "A rejected casting proposal cannot be confirmed."
                        : "The casting proposal is no longer pending.");
            }

            RequirePendingCastingProposal(proposal, expectedDraftRevision);
            RequireActiveCastingProject(project);
            if (proposal.BaseConfigurationRevision != project.ConfigurationRevision)
                throw CastingConfigurationConflict();

            var configuration = ProjectConfigurationValidator.Validate(
                Deserialize<ProjectConfiguration>(proposal.DraftJson));
            await EnsureCastingProjectNarrowingAsync(context, configuration, token).ConfigureAwait(false);
            var now = timeProvider.GetUtcNow();
            var nextRevision = checked(project.ConfigurationRevision + 1);
            context.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
            {
                ProjectId = project.ProjectId,
                Revision = nextRevision,
                ConfigurationJson = Serialize(configuration),
                UpdatedByActorId = caller.ActorId,
                CreatedAt = now,
            });
            project.ConfigurationRevision = nextRevision;
            project.UpdatedAt = now;
            proposal.State = ProjectCastingProposalState.Confirmed;
            proposal.ConfirmedConfigurationRevision = nextRevision;
            proposal.ResultJson = Serialize(configuration);
            proposal.UpdatedByActorId = caller.ActorId;
            proposal.UpdatedAt = now;
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            return ToCastingProposal(proposal);
        }, cancellationToken);
    }

    public Task<ProjectCastingProposal> RejectCastingProposalAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        Guid proposalId,
        long expectedDraftRevision,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        ValidateCastingProposalId(proposalId);
        ValidateExpectedDraftRevision(expectedDraftRevision);

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            var proposal = await LockCastingProposalAsync(context, project.ProjectId, proposalId, token)
                .ConfigureAwait(false);
            if (proposal.State == ProjectCastingProposalState.Rejected &&
                proposal.DraftRevision == expectedDraftRevision)
                return ToCastingProposal(proposal);
            RequirePendingCastingProposal(proposal, expectedDraftRevision);

            proposal.State = ProjectCastingProposalState.Rejected;
            proposal.UpdatedByActorId = caller.ActorId;
            proposal.UpdatedAt = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(token).ConfigureAwait(false);
            return ToCastingProposal(proposal);
        }, cancellationToken);
    }

    private async Task<T> ExecuteCastingProposalTransactionAsync<T>(
        Func<ProjectsConfigDbContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (dbContextFactory is null)
                    return await RunCastingProposalTransactionAsync(db, operation, cancellationToken)
                        .ConfigureAwait(false);

                await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken)
                    .ConfigureAwait(false);
                return await RunCastingProposalTransactionAsync(context, operation, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                dbContextFactory is not null &&
                attempt + 1 < CastingProposalTransactionAttempts &&
                IsSerializationFailure(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> RunCastingProposalTransactionAsync<T>(
        ProjectsConfigDbContext context,
        Func<ProjectsConfigDbContext, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var result = await operation(context, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<ProjectRecord> LockCastingProjectAndAuthorityAsync(
        ProjectsConfigDbContext context,
        ProjectAuthorizationContext caller,
        string projectId,
        bool write,
        CancellationToken cancellationToken)
    {
        caller.RequireResourceBinding(projectId);
        if (!Guid.TryParseExact(projectId, "N", out _))
            throw ProjectConfigException.NotFound();

        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        await using (var projectLock = new NpgsqlCommand(
            $"""
            SELECT project_id
            FROM projects_config.projects
            WHERE project_id = @project_id
              AND tenant_id = @tenant_id
            FOR {(write ? "UPDATE" : "SHARE")}
            """,
            connection,
            transaction))
        {
            projectLock.Parameters.AddWithValue("project_id", projectId);
            projectLock.Parameters.AddWithValue("tenant_id", caller.TenantId);
            if (await projectLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw ProjectConfigException.NotFound();
        }

        await using (var authorityLock = new NpgsqlCommand(
            """
            SELECT projects_config.lock_casting_authority(
                @membership_id, @issuer, @subject, @tenant_id, @revision, @project_id, @write)
            """,
            connection,
            transaction))
        {
            authorityLock.Parameters.AddWithValue("membership_id", caller.MembershipId);
            authorityLock.Parameters.AddWithValue("issuer", caller.Issuer);
            authorityLock.Parameters.AddWithValue("subject", caller.ActorId);
            authorityLock.Parameters.AddWithValue("tenant_id", caller.TenantId);
            authorityLock.Parameters.AddWithValue("revision", caller.MembershipRevision);
            authorityLock.Parameters.AddWithValue("project_id", projectId);
            authorityLock.Parameters.AddWithValue("write", write);
            var status = await authorityLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException("The casting authority lock returned no status.");
            if (status == "membership_missing")
                throw ProjectConfigException.Forbidden();
            if (status == "assignment_missing")
                throw write ? ProjectConfigException.Forbidden() : ProjectConfigException.NotFound();
            if (status != "authorized")
                throw new InvalidOperationException($"The casting authority lock returned unknown status '{status}'.");
        }

        return await context.Projects.SingleAsync(
            project => project.ProjectId == projectId,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProjectCastingProposalRecord> LockCastingProposalAsync(
        ProjectsConfigDbContext context,
        string projectId,
        Guid proposalId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        await using var proposalLock = new NpgsqlCommand(
            """
            SELECT proposal_id
            FROM projects_config.project_casting_proposals
            WHERE proposal_id = @proposal_id
              AND project_id = @project_id
            FOR UPDATE
            """,
            connection,
            transaction);
        proposalLock.Parameters.AddWithValue("proposal_id", proposalId);
        proposalLock.Parameters.AddWithValue("project_id", projectId);
        if (await proposalLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw ProjectConfigException.NotFound();

        return await context.CastingProposals.SingleAsync(
            proposal => proposal.ProposalId == proposalId && proposal.ProjectId == projectId,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCastingProjectNarrowingAsync(
        ProjectsConfigDbContext context,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var limits = configuration.RunLimits;
        if (configuration.EgressNarrowing is null &&
            limits.MaxModelTurns is null &&
            limits.MaxToolCalls is null &&
            limits.MaxChildren is null &&
            limits.MaxConcurrentChildren is null &&
            limits.MaxWallTimeSeconds is null &&
            limits.MaxPromptTokens is null)
            return;

        var head = await context.PlatformRuntimeHeads.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == PlatformRuntimeHeadRecord.SingletonId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The platform runtime head row is missing from the Projects & Config schema.");
        if (head.CurrentRevision == 0)
            throw new ProjectConfigException(
                "platform_runtime_unconfigured",
                "Platform runtime defaults must be configured before project runtime overrides.",
                StatusCodes.Status409Conflict);
        var revision = await context.PlatformRuntimeRevisions.AsNoTracking()
            .SingleAsync(item => item.Revision == head.CurrentRevision, cancellationToken)
            .ConfigureAwait(false);
        var defaults = Deserialize<PlatformRuntimeDefaults>(revision.ConfigurationJson);
        _ = ProjectConfigurationValidator.ResolveEgress(
            defaults.EgressBaseline,
            configuration.EgressNarrowing,
            []);
        _ = ProjectConfigurationValidator.ResolveLimits(defaults, limits);
    }

    private static ProjectCastingProposal ToCastingProposal(ProjectCastingProposalRecord proposal) =>
        new(
            proposal.ProposalId,
            proposal.ProjectId,
            proposal.BaseConfigurationRevision,
            proposal.DraftRevision,
            proposal.State,
            Deserialize<ProjectConfiguration>(proposal.DraftJson),
            proposal.ConfirmedConfigurationRevision,
            proposal.ResultJson is null ? null : Deserialize<ProjectConfiguration>(proposal.ResultJson),
            proposal.TransferFormatVersion is null
                ? null
                : new ProjectCastingTransferProvenance(
                    proposal.TransferFormatVersion.Value,
                    proposal.TransferSourceProjectId!,
                    proposal.TransferSourceConfigurationRevision!.Value,
                    proposal.TransferContentDigest!),
            proposal.CreatedByActorId,
            proposal.CreatedAt,
            proposal.UpdatedByActorId,
            proposal.UpdatedAt);

    private async Task<ProjectCastingProposal> AddCastingProposalAsync(
        ProjectsConfigDbContext context,
        ProjectAuthorizationContext caller,
        ProjectRecord project,
        ProjectConfiguration draft,
        ProjectCastingTransferProvenance? transferProvenance,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var proposal = new ProjectCastingProposalRecord
        {
            ProposalId = Guid.NewGuid(),
            ProjectId = project.ProjectId,
            BaseConfigurationRevision = project.ConfigurationRevision,
            DraftRevision = 1,
            State = ProjectCastingProposalState.Pending,
            DraftJson = Serialize(draft),
            TransferFormatVersion = transferProvenance?.FormatVersion,
            TransferSourceProjectId = transferProvenance?.SourceProjectId,
            TransferSourceConfigurationRevision = transferProvenance?.SourceConfigurationRevision,
            TransferContentDigest = transferProvenance?.ContentDigest,
            CreatedByActorId = caller.ActorId,
            CreatedAt = now,
            UpdatedByActorId = caller.ActorId,
            UpdatedAt = now,
        };
        context.CastingProposals.Add(proposal);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToCastingProposal(proposal);
    }

    private static void RequireCastingRead(ProjectAuthorizationContext caller, string projectId)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireResourceBinding(projectId);
        ValidateCastingProjectId(projectId);
    }

    private static void RequireCastingWrite(ProjectAuthorizationContext caller, string projectId)
    {
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireScope(ProjectAuthorizationOwner.ProjectAdminScope);
        caller.RequireResourceBinding(projectId);
        ValidateCastingProjectId(projectId);
    }

    private static void ValidateCastingProjectId(string projectId)
    {
        if (!Guid.TryParseExact(projectId, "N", out _))
            throw ProjectConfigException.NotFound();
    }

    private static void ValidateCastingProposalId(Guid proposalId)
    {
        if (proposalId == Guid.Empty)
            throw ProjectConfigException.NotFound();
    }

    private static void ValidateExpectedDraftRevision(long expectedDraftRevision)
    {
        if (expectedDraftRevision <= 0)
            throw ProjectConfigException.Invalid(
                "invalid_draft_revision",
                "The expected casting proposal draft revision must be positive.");
    }

    private static void RequireActiveCastingProject(ProjectRecord project)
    {
        if (project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.Conflict("Archived projects cannot be reconfigured.");
    }

    private static void RequirePendingCastingProposal(
        ProjectCastingProposalRecord proposal,
        long expectedDraftRevision)
    {
        if (proposal.State != ProjectCastingProposalState.Pending)
            throw CastingProposalConflict("The casting proposal is no longer pending.");
        if (proposal.DraftRevision != expectedDraftRevision)
            throw ProjectConfigException.Conflict(
                "stale_casting_proposal",
                "The casting proposal draft has changed.");
    }

    private static ProjectConfigException CastingConfigurationConflict() =>
        ProjectConfigException.Conflict(
            "stale_project_configuration",
            "The project configuration changed after this casting proposal was created.");

    private static ProjectConfigException CastingProposalConflict(string message) =>
        ProjectConfigException.Conflict("casting_proposal_conflict", message);

    private static bool IsSerializationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure })
                return true;
        }
        return false;
    }
}
