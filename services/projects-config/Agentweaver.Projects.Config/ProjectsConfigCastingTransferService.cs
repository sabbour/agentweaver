using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Projects.Config;

public sealed partial class ProjectsConfigService
{
    public Task<ProjectCastingTransfer> ExportCastingTransferAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        long? revision,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        if (revision is <= 0)
            throw ProjectConfigException.Invalid(
                "invalid_configuration_revision",
                "The requested project configuration revision must be positive.");

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            var selectedRevision = revision ?? project.ConfigurationRevision;
            if (selectedRevision > project.ConfigurationRevision)
                throw ProjectConfigException.NotFound();
            var configuration = await context.ProjectConfigurationRevisions.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == project.ProjectId && item.Revision == selectedRevision,
                    token)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var projectConfiguration = Deserialize<ProjectConfiguration>(configuration.ConfigurationJson);
            var transfer = new ProjectCastingTransfer
            {
                FormatVersion = ProjectCastingTransfer.CurrentFormatVersion,
                SourceProjectId = project.ProjectId,
                SourceConfigurationRevision = selectedRevision,
                AgentCharters = projectConfiguration.AgentCharters,
                Casting = projectConfiguration.Casting,
                ContentDigest = string.Empty,
            };
            return transfer with
            {
                ContentDigest = ComputeCastingTransferDigest(transfer),
            };
        }, cancellationToken);
    }

    public Task<ProjectCastingProposal> ImportCastingTransferAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        ImportProjectCastingTransferRequest request,
        CancellationToken cancellationToken)
    {
        RequireCastingWrite(caller, projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Transfer);
        if (request.ExpectedConfigurationRevision <= 0)
            throw ProjectConfigException.Invalid(
                "invalid_configuration_revision",
                "The expected project configuration revision must be positive.");
        var provenance = ValidateCastingTransfer(request.Transfer);

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            RequireActiveCastingProject(project);
            if (project.ConfigurationRevision != request.ExpectedConfigurationRevision)
                throw CastingConfigurationConflict();
            var currentRevision = await context.ProjectConfigurationRevisions.AsNoTracking()
                .SingleAsync(
                    item => item.ProjectId == project.ProjectId &&
                        item.Revision == project.ConfigurationRevision,
                    token)
                .ConfigureAwait(false);
            var current = Deserialize<ProjectConfiguration>(currentRevision.ConfigurationJson);
            ProjectConfiguration imported;
            try
            {
                imported = ProjectConfigurationValidator.ValidateTransition(
                    current,
                    current with
                    {
                        AgentCharters = request.Transfer.AgentCharters,
                        Casting = request.Transfer.Casting,
                    });
            }
            catch (ProjectConfigException exception)
            {
                throw new ProjectConfigException(
                    "incompatible_casting_transfer",
                    $"The imported team is incompatible with this project: {exception.Message} " +
                    "Edit the transfer or resolve the affected project settings, then import it again.",
                    StatusCodes.Status422UnprocessableEntity);
            }

            await EnsureCastingProjectNarrowingAsync(context, imported, token).ConfigureAwait(false);
            return await AddCastingProposalAsync(
                context, caller, project, imported, provenance, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static ProjectCastingTransferProvenance ValidateCastingTransfer(ProjectCastingTransfer transfer)
    {
        if (transfer.FormatVersion != ProjectCastingTransfer.CurrentFormatVersion)
            throw new ProjectConfigException(
                "unsupported_casting_transfer_version",
                $"Casting transfer format {transfer.FormatVersion} is not supported. " +
                $"Export again using format {ProjectCastingTransfer.CurrentFormatVersion}.",
                StatusCodes.Status422UnprocessableEntity);
        if (!Guid.TryParseExact(transfer.SourceProjectId, "N", out _) ||
            transfer.SourceConfigurationRevision <= 0 ||
            transfer.AgentCharters.IsDefault ||
            transfer.Casting.IsDefault)
            throw ProjectConfigException.Invalid(
                "invalid_casting_transfer",
                "The transfer is missing a valid source project, source revision, charter list, or casting list.");
        if (!IsLowercaseSha256(transfer.ContentDigest))
            throw ProjectConfigException.Invalid(
                "invalid_casting_transfer_digest",
                "The transfer content digest must be a 64-character lowercase SHA-256 value.");
        if (!string.Equals(
                transfer.ContentDigest,
                ComputeCastingTransferDigest(transfer),
                StringComparison.Ordinal))
            throw ProjectConfigException.Invalid(
                "casting_transfer_digest_mismatch",
                "The transfer content does not match its digest. Export a fresh transfer instead of editing the file.");

        return new ProjectCastingTransferProvenance(
            transfer.FormatVersion,
            transfer.SourceProjectId,
            transfer.SourceConfigurationRevision,
            transfer.ContentDigest);
    }

    private static string ComputeCastingTransferDigest(ProjectCastingTransfer transfer) =>
        HashFingerprint(new
        {
            formatVersion = transfer.FormatVersion,
            sourceProjectId = transfer.SourceProjectId,
            sourceConfigurationRevision = transfer.SourceConfigurationRevision,
            agentCharters = transfer.AgentCharters,
            casting = transfer.Casting,
        });

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
}
