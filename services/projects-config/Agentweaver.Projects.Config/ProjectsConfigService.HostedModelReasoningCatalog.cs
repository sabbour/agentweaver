using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Projects.Config;

public sealed partial class ProjectsConfigService
{
    private static readonly TimeSpan HostedReasoningCatalogMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan HostedReasoningCatalogMaxClockSkew = TimeSpan.FromMinutes(5);
    private static readonly ImmutableHashSet<string> HostedReasoningEffortNames =
        ImmutableHashSet.Create(StringComparer.Ordinal, "low", "medium", "high", "xhigh");

    /// <summary>Stores AgentHost-verified reasoning levels for the caller's current hosted connection.</summary>
    public async Task PublishHostedModelReasoningCatalogAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        HostedModelReasoningCatalogPublicationV1 publication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        caller.RequireScope(ProjectAuthorizationOwner.ApiReadScope);
        caller.RequireResourceBinding(projectId);
        var project = await FindProjectAsync(caller, projectId, ProjectAccess.Read, cancellationToken)
            .ConfigureAwait(false);
        if (project.State != ProjectLifecycleState.Active)
            throw ProjectConfigException.Conflict("Archived projects cannot publish model capabilities.");

        if (!modelBindings.IsAvailable ||
            !string.Equals(publication.BindingsRevision, modelBindings.Revision, StringComparison.Ordinal) ||
            publication.ConnectionId == Guid.Empty ||
            !IsValidHostedReasoningCatalogPublication(publication))
            throw ProjectConfigException.Invalid(
                "invalid_hosted_model_reasoning_catalog",
                "The hosted model reasoning catalog does not match the current model bindings.");

        var binding = modelBindings.Bindings.SingleOrDefault(item => item.Reference == publication.Reference);
        if (binding is not { Enabled: true, SourceMode: "HostedCopilot" })
            throw ProjectConfigException.Conflict("The hosted model binding is no longer eligible.");

        var configurationRevision = await db.ProjectConfigurationRevisions.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ProjectId == project.ProjectId && item.Revision == project.ConfigurationRevision,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw ProjectConfigException.NotFound();
        var configuration = Deserialize<ProjectConfiguration>(configurationRevision.ConfigurationJson);
        var platformHead = await GetPlatformHeadAsync(cancellationToken).ConfigureAwait(false);
        var platform = await ReadPlatformDefaultsAsync(platformHead, cancellationToken).ConfigureAwait(false);
        var currentConnection = configuration.ModelSelection is { } projectSelection
            ? projectSelection.ConnectionId
            : platform.Defaults?.ModelSelection?.ConnectionId;
        if (currentConnection != publication.ConnectionId)
            throw ProjectConfigException.Conflict("The hosted model connection is no longer current.");

        var now = timeProvider.GetUtcNow();
        // The SDK-read time bounds both excessive clock skew and stale data; receipt time is not an observation time.
        if (publication.VerifiedAt < now - HostedReasoningCatalogMaxAge ||
            publication.VerifiedAt > now + HostedReasoningCatalogMaxClockSkew)
            throw ProjectConfigException.Conflict("The hosted model catalog verification is stale.");

        // Scope keys prevent unrelated choices from being reused; the timestamp condition stops a delayed older run
        // from replacing a newer verification for the same scope.
        var affectedRows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO projects_config.hosted_model_reasoning_catalog AS current
                (tenant_id, project_id, actor_id, connection_id, bindings_revision, reference,
                 supported_reasoning_efforts, verified_at)
            VALUES
                ({caller.TenantId}, {project.ProjectId}, {caller.ActorId}, {publication.ConnectionId},
                 {publication.BindingsRevision}, {publication.Reference},
                 {publication.SupportedReasoningEfforts.ToArray()}, {publication.VerifiedAt})
            ON CONFLICT
                (tenant_id, project_id, actor_id, connection_id, bindings_revision, reference)
            DO UPDATE SET
                supported_reasoning_efforts = EXCLUDED.supported_reasoning_efforts,
                verified_at = EXCLUDED.verified_at
            WHERE current.verified_at <= EXCLUDED.verified_at
            """, cancellationToken).ConfigureAwait(false);
        if (affectedRows == 0)
            throw ProjectConfigException.Conflict("A newer hosted model catalog verification is already stored.");
    }

    private static bool IsValidHostedReasoningCatalogPublication(
        HostedModelReasoningCatalogPublicationV1 publication)
    {
        // Keep the stored vocabulary closed and unique so readers can trust it without reinterpreting SDK values.
        return !string.IsNullOrWhiteSpace(publication.Reference) &&
            !publication.SupportedReasoningEfforts.IsDefault &&
            publication.SupportedReasoningEfforts.All(HostedReasoningEffortNames.Contains) &&
            publication.SupportedReasoningEfforts.Distinct(StringComparer.Ordinal).Count() ==
                publication.SupportedReasoningEfforts.Length;
    }
}
