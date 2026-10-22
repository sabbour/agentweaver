using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config.AgentSuite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Projects.Config;

public sealed partial class ProjectsConfigService
{
    /// <summary>
    /// Validates a native AgentSuite archive offline, retains its original blobs and an immutable import
    /// record, and stages new agents as one pending casting proposal. Nothing is activated or executed.
    /// Repeating the same request returns the same proposal; existing agents are never overwritten.
    /// </summary>
    /// <exception cref="AgentSuiteImportRejectedException">The archive fails native or Agentweaver validation.</exception>
    /// <exception cref="ProjectConfigException">The request is invalid, stale, conflicting, or unauthorized.</exception>
    public async Task<ProjectAgentSuiteImportResult> ImportAgentSuiteAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        ImportProjectAgentSuiteRequest request,
        IObjectStore objectStore,
        CancellationToken cancellationToken)
    {
        // Check the caller's casting write role first, before any decoding or validation work.
        RequireCastingWrite(caller, projectId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(objectStore);
        // The caller must say which configuration revision it reviewed. Revisions start at 1.
        if (request.ExpectedConfigurationRevision <= 0)
            throw ProjectConfigException.Invalid(
                "invalid_configuration_revision",
                "The expected project configuration revision must be positive.");
        // Map the wire format name to the validator's archive format. Unknown names are rejected.
        var format = request.Format switch
        {
            ImportProjectAgentSuiteRequest.OciLayoutTarFormat => AgentSuiteArchiveFormat.OciLayoutTar,
            ImportProjectAgentSuiteRequest.ContentTarGzipFormat => AgentSuiteArchiveFormat.ContentTarGzip,
            _ => throw ProjectConfigException.Invalid(
                "invalid_agentsuite_format",
                $"Format must be '{ImportProjectAgentSuiteRequest.OciLayoutTarFormat}' or " +
                $"'{ImportProjectAgentSuiteRequest.ContentTarGzipFormat}'."),
        };
        var archive = DecodeAgentSuiteArchive(request.Archive);

        // Offline validation: only the archive bytes are read. Nothing is fetched from the network.
        var result = AgentSuiteValidator.Validate(format, archive);
        // Return every native and Agentweaver profile error together so the author can fix them in one pass.
        if (!result.Accepted || result.Graph is null)
            throw new AgentSuiteImportRejectedException(
                "agentsuite_invalid",
                "The AgentSuite archive is not valid.",
                [.. result.NativeErrors, .. result.ProfileErrors.Select(item => $"{item.Code}: {item.Message}")]);
        // Native agent IDs become cast roles. The platform owns reserved roles, such as the coordinator.
        // Sort the list so the error output is the same on every run.
        var reserved = result.Graph.Agents.Select(agent => agent.Id)
            .Where(ProjectConfigurationValidator.IsReservedOrchestrationRole)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (reserved.Length > 0)
            throw new AgentSuiteImportRejectedException(
                "agentsuite_invalid",
                "The AgentSuite archive is not valid.",
                [.. reserved.Select(id =>
                    $"agentsuite_reserved_role: Agent '{id}' uses a reserved orchestration role.")]);

        // Build everything that does not need the database first: the record, charters and proposal ID.
        // These are deterministic, so a repeated request produces the same values.
        var record = AgentSuiteImportPlanner.CreateRecord(
            projectId,
            result,
            NormalizeToolConnections(request.ToolConnections, result.Graph),
            NormalizeBlueprint(request.Blueprint, result.Graph));
        var charters = AgentSuiteImportPlanner.CreateCharters(record, result.Graph);
        // A supplied blueprint must pass the same 0.x rules as inline apply before anything is stored.
        // Validation runs outside the transaction because it only needs the request and the parsed suite.
        var applyBlueprint = request.Blueprint is not null;
        if (applyBlueprint)
        {
            // Skill binding roles must be native agent IDs from the suite graph.
            var errors = ProjectBlueprintCatalog.ValidateBlueprint(
                record.Blueprint, [.. result.Graph.Agents.Select(agent => agent.Id)]);
            if (!errors.IsEmpty)
                throw ProjectConfigException.Invalid(
                    "invalid_blueprint",
                    $"Blueprint is invalid: {string.Join(" ", errors)}");
        }
        // Maps each native agent ID to the agent ID it is cast as in this project, so bindings land on the right agent.
        var agentIds = record.Agents.ToDictionary(
            agent => agent.NativeId, agent => agent.EffectiveAgentId, StringComparer.Ordinal);
        var proposalId = AgentSuiteProposalId(projectId, record, request.ExpectedConfigurationRevision);
        var store = new AgentSuiteObjectStore(objectStore);

        return await ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            // Lock the project row and re-check write authority inside the transaction, so a role change
            // or a concurrent proposal cannot slip in between the check and the write.
            var project = await LockCastingProjectAndAuthorityAsync(
                context, caller, projectId, write: true, token).ConfigureAwait(false);
            RequireActiveCastingProject(project);

            // A lost response or restart replays the same request; return the proposal it already created.
            // This check runs before the revision check, because the first request may have moved the revision.
            var existing = await context.CastingProposals.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProposalId == proposalId && item.ProjectId == project.ProjectId,
                    token)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                // The record was written before the proposal, so it must exist. If it is gone, storage is damaged.
                var replayed = await store.ReadRecordAsync(projectId, record.SourceIdentity, JsonOptions, token)
                    .ConfigureAwait(false) ?? throw new ProjectConfigException(
                        "agentsuite_integrity_error",
                        "The import record for this proposal is missing.",
                        StatusCodes.Status500InternalServerError);
                var replayedProposal = ToCastingProposal(existing);
                // The stored draft already includes the blueprint. Reapply it only to recover the same
                // unresolved-binding report without changing the stored proposal.
                return new ProjectAgentSuiteImportResult(replayed, replayedProposal)
                {
                    UnresolvedSkillBindings = applyBlueprint
                        ? ProjectBlueprintCatalog.ApplyBlueprint(replayedProposal.Draft, record.Blueprint, agentIds)
                            .UnresolvedSkillBindings
                        : [],
                };
            }

            // Revision fence: the caller reviewed an older configuration, so reject with 409 instead of merging.
            if (project.ConfigurationRevision != request.ExpectedConfigurationRevision)
                throw CastingConfigurationConflict();
            // Load the configuration at the current revision; the new agents are merged into it.
            var currentRevision = await context.ProjectConfigurationRevisions.AsNoTracking()
                .SingleOrDefaultAsync(
                    item => item.ProjectId == project.ProjectId && item.Revision == project.ConfigurationRevision,
                    token)
                .ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var current = Deserialize<ProjectConfiguration>(currentRevision.ConfigurationJson);
            // Merge first: it throws on an identity conflict before anything is written to storage.
            var draft = MergeAgentSuiteImport(current, charters);
            var unresolved = ImmutableArray<BlueprintUnresolvedSkillBinding>.Empty;
            if (applyBlueprint)
            {
                // A repeated agent import can still change workflows, skills or sandbox settings.
                // Use the same apply path as inline blueprints, even when there are no new agents.
                var application = ProjectBlueprintCatalog.ApplyBlueprint(draft ?? current, record.Blueprint, agentIds);
                unresolved = application.UnresolvedSkillBindings;
                draft = draft is null && SameConfiguration(current, application.Draft) ? null : application.Draft;
            }

            // Store order: blobs, then the agent index, then the record. Every write is create-if-absent, so a
            // retry after a partial failure finishes the job without overwriting anything.
            await store.WriteBlobsAsync(result.Blobs, token).ConfigureAwait(false);
            await store.WriteAgentIndexAsync(record, token).ConfigureAwait(false);
            var stored = await store.WriteRecordAsync(record, JsonOptions, token).ConfigureAwait(false);
            // Nothing changed, but bindings that could not apply must still be visible in the response.
            if (draft is null)
                return new ProjectAgentSuiteImportResult(stored, null) { UnresolvedSkillBindings = unresolved };

            ProjectConfiguration normalized;
            try
            {
                // Run the normal configuration rules on the merged draft, as for any other casting change.
                normalized = ProjectConfigurationValidator.ValidateTransition(current, draft);
            }
            catch (ProjectConfigException exception)
            {
                // Report rule failures as 422 so the caller knows the suite, not the request, is the problem.
                throw new ProjectConfigException(
                    "incompatible_agentsuite_import",
                    $"The suite agents are incompatible with this project: {exception.Message}",
                    StatusCodes.Status422UnprocessableEntity);
            }
            await EnsureCastingProjectNarrowingAsync(context, normalized, token).ConfigureAwait(false);
            // Stage a pending proposal with the stable ID. It is only applied after review; nothing is activated.
            var proposal = await AddCastingProposalAsync(
                context, caller, project, normalized, null, token, proposalId).ConfigureAwait(false);
            return new ProjectAgentSuiteImportResult(stored, proposal) { UnresolvedSkillBindings = unresolved };
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one verified import record after checking the current project read role.</summary>
    public Task<ProjectAgentSuiteImport> GetAgentSuiteImportAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string sourceIdentity,
        IObjectStore objectStore,
        CancellationToken cancellationToken)
    {
        RequireCastingRead(caller, projectId);
        ArgumentNullException.ThrowIfNull(objectStore);
        // Reject malformed identities early; they are also used to build the storage key.
        RequireSourceIdentity(sourceIdentity);

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            // Re-check read authority against the current project state before reading storage.
            await LockCastingProjectAndAuthorityAsync(context, caller, projectId, write: false, token)
                .ConfigureAwait(false);
            // A record that was never written for this project returns 404, not an empty result.
            return await new AgentSuiteObjectStore(objectStore)
                .ReadRecordAsync(projectId, sourceIdentity, JsonOptions, token).ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
        }, cancellationToken);
    }

    /// <summary>
    /// Returns the policy bound to one imported effective agent ID, with a null policy when none is bound.
    /// Unknown effective IDs are not found. Reading a policy never enforces it.
    /// </summary>
    public Task<ProjectAgentSuiteAgentPolicy> GetAgentSuiteAgentPolicyAsync(
        ProjectAuthorizationContext caller,
        string projectId,
        string effectiveAgentId,
        IObjectStore objectStore,
        CancellationToken cancellationToken)
    {
        RequireCastingRead(caller, projectId);
        ArgumentNullException.ThrowIfNull(objectStore);
        // Cheap shape check: 256 is the identifier length limit, and every imported ID has the "agentsuite:" prefix.
        if (string.IsNullOrEmpty(effectiveAgentId) || effectiveAgentId.Length > 256 ||
            !effectiveAgentId.StartsWith(AgentSuiteImportPlanner.EffectiveIdPrefix + ":", StringComparison.Ordinal))
            throw ProjectConfigException.Invalid(
                "invalid_agentsuite_agent_id",
                "The effective agent ID must start with 'agentsuite:'.");

        return ExecuteCastingProposalTransactionAsync(async (context, token) =>
        {
            await LockCastingProjectAndAuthorityAsync(context, caller, projectId, write: false, token)
                .ConfigureAwait(false);
            var store = new AgentSuiteObjectStore(objectStore);
            // Two lookups: the agent index gives the source identity, which then locates the import record.
            var identity = await store.ReadAgentIndexAsync(projectId, effectiveAgentId, token).ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var record = await store.ReadRecordAsync(projectId, identity, JsonOptions, token).ConfigureAwait(false)
                ?? throw ProjectConfigException.NotFound();
            var agent = record.Agents.SingleOrDefault(item => item.EffectiveAgentId == effectiveAgentId)
                ?? throw ProjectConfigException.NotFound();
            // Match on both digest and owning agent, because two agents may share the same policy bytes.
            var policy = agent.PolicyDigest is null
                ? null
                : record.Policies.Single(item => item.Digest == agent.PolicyDigest && item.OwningAgent == agent.NativeId);
            return new ProjectAgentSuiteAgentPolicy(effectiveAgentId, identity, policy);
        }, cancellationToken);
    }

    // Accepts only "sha256:" plus 64 lowercase hex characters (7 + 64 = 71 characters in total).
    private static void RequireSourceIdentity(string sourceIdentity)
    {
        if (sourceIdentity is not { Length: 71 } ||
            !sourceIdentity.StartsWith("sha256:", StringComparison.Ordinal) ||
            sourceIdentity.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") >= 0)
            throw ProjectConfigException.Invalid(
                "invalid_agentsuite_identity",
                "The source identity must be sha256: followed by 64 lowercase hex characters.");
    }

    /// <summary>
    /// Checks each binding names a remote tool provider in the suite once, with a lowercase "D" GUID connection ID.
    /// The connection itself is resolved and verified by the Environment owner, not here.
    /// </summary>
    private static ImmutableArray<ProjectAgentSuiteToolConnection> NormalizeToolConnections(
        ImmutableArray<ProjectAgentSuiteToolConnection> connections,
        AgentSuiteGraph graph)
    {
        if (connections.IsDefaultOrEmpty)
            return [];
        // Only providers that declare a remote MCP endpoint can be bound to a connection.
        var remote = graph.ToolProviders
            .Where(item => item.HasRemoteMcp)
            .Select(item => AgentSuiteImportPlanner.ToolProviderKey(item.Id, item.Version))
            .ToHashSet(StringComparer.Ordinal);
        // Tracks providers already bound, to reject a second binding for the same provider.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            // JSON can carry null entries or null fields, so check them before the lookup.
            if (connection is null || connection.ToolProvider is null || !remote.Contains(connection.ToolProvider))
                throw ProjectConfigException.Invalid(
                    "invalid_agentsuite_tool_connection",
                    "Each tool connection must name a remote tool provider (id@version) in the suite.");
            if (!seen.Add(connection.ToolProvider))
                throw ProjectConfigException.Invalid(
                    "invalid_agentsuite_tool_connection",
                    $"Tool provider '{connection.ToolProvider}' has more than one connection.");
            // Require the exact lowercase "D" GUID text (round-trip check) so the same ID always has one spelling
            // and the record bytes stay stable. The empty GUID is not a real connection.
            if (connection.ConnectionId is not { Length: 36 } id ||
                !Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty ||
                !string.Equals(parsed.ToString("D"), id, StringComparison.Ordinal))
                throw ProjectConfigException.Invalid(
                    "invalid_agentsuite_tool_connection",
                    "The connection ID must be a nonempty lowercase GUID.");
        }
        // Sort by provider so the record (and so the proposal ID) does not depend on request order.
        return [.. connections.OrderBy(item => item.ToolProvider, StringComparer.Ordinal)];
    }

    /// <summary>Applies blueprint defaults and checks every value. Roles must be native agent IDs in the suite.</summary>
    private static ProjectAgentSuiteBlueprint NormalizeBlueprint(ProjectAgentSuiteBlueprint? blueprint, AgentSuiteGraph graph)
    {
        // A missing blueprint means "use all defaults".
        blueprint ??= new ProjectAgentSuiteBlueprint();
        static ProjectConfigException Invalid(string message) =>
            ProjectConfigException.Invalid("invalid_agentsuite_blueprint", message);
        // Identifiers: 1-256 ASCII letters, digits, '.', '_', '-' or ':'.
        static bool IsIdentifier(string? value) =>
            !string.IsNullOrEmpty(value) && value.Length <= 256 &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
        // Free text: not blank and within the given length.
        static bool IsText(string? value, int maximum) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

        // Optional fields are checked only when present.
        if (blueprint.SourceBlueprintId is not null && !IsIdentifier(blueprint.SourceBlueprintId))
            throw Invalid("The source blueprint ID must be an identifier.");
        if (blueprint.DisplayName is not null && !IsText(blueprint.DisplayName, 160))
            throw Invalid("The display name must be nonempty and at most 160 characters.");
        if (blueprint.Description is not null && !IsText(blueprint.Description, 2000))
            throw Invalid("The description must be nonempty and at most 2000 characters.");
        // Workflows: at least one, all identifiers, no duplicates (distinct count equals total count).
        if (blueprint.Workflows.IsDefaultOrEmpty || !blueprint.Workflows.All(IsIdentifier) ||
            blueprint.Workflows.Distinct(StringComparer.Ordinal).Count() != blueprint.Workflows.Length)
            throw Invalid("Workflows must be a nonempty list of unique identifiers.");
        if (!blueprint.Workflows.Contains(blueprint.DefaultWorkflow, StringComparer.Ordinal))
            throw Invalid("The default workflow must be one of the workflows.");
        if (!IsIdentifier(blueprint.ReviewPolicy))
            throw Invalid("The review policy must be an identifier.");
        if (blueprint.SandboxProfile is not (ProjectAgentSuiteBlueprint.DefaultId or ProjectAgentSuiteBlueprint.RestrictedSandbox))
            throw Invalid("The sandbox profile must be 'default' or 'restricted'.");
        // A default (uninitialized) array means the JSON had "skillBindings": null; an empty list is fine.
        if (blueprint.SkillBindings.IsDefault)
            throw Invalid("Skill bindings must be a list.");
        var agents = graph.Agents.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in blueprint.SkillBindings)
        {
            // Each binding must name a real suite agent, and each agent may appear in only one binding.
            if (binding is null || binding.Role is null || !agents.Contains(binding.Role) || !roles.Add(binding.Role))
                throw Invalid("Each skill binding must name a different agent in the suite.");
            if (binding.Skills.IsDefaultOrEmpty || !binding.Skills.All(IsIdentifier) ||
                binding.Skills.Distinct(StringComparer.Ordinal).Count() != binding.Skills.Length)
                throw Invalid($"Skills for '{binding.Role}' must be a nonempty list of unique identifiers.");
        }
        // Sort bindings by role so the stored record does not depend on request order.
        return blueprint with
        {
            SkillBindings = [.. blueprint.SkillBindings.OrderBy(item => item.Role, StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// Adds charters and cast entries that do not exist yet. Returns null when all already exist unchanged.
    /// An existing effective ID with different content is a conflict; it is never replaced. A supplied blueprint
    /// projection is applied afterward through <see cref="ProjectBlueprintCatalog.ApplyBlueprint"/>.
    /// </summary>
    private static ProjectConfiguration? MergeAgentSuiteImport(
        ProjectConfiguration current,
        ImmutableArray<(ProjectAgentCharter Charter, string Role)> charters)
    {
        var existing = current.AgentCharters.ToDictionary(item => item.AgentId, StringComparer.Ordinal);
        var added = new List<ProjectAgentCharter>();
        foreach (var (charter, _) in charters)
        {
            // New effective ID: stage it.
            if (!existing.TryGetValue(charter.AgentId, out var present))
            {
                added.Add(charter);
                continue;
            }
            // Same ID and same content: already imported, skip it. Same ID but different content: refuse (409).
            if (present != charter)
                throw ProjectConfigException.Conflict(
                    "agentsuite_identity_conflict",
                    $"Agent '{charter.AgentId}' already exists with different content. It was not changed.");
        }
        if (added.Count == 0)
            return null;

        // Append new cast entries after the highest existing order, keeping the charter order from the suite.
        var order = current.Casting.IsDefaultOrEmpty ? 0 : current.Casting.Max(item => item.Order) + 1;
        return current with
        {
            AgentCharters = current.AgentCharters.AddRange(added),
            Casting = current.Casting.AddRange(added.Select(
                (charter, index) => new ProjectAgentCast(charter.AgentId, charter.Role, order + index))),
        };
    }

    /// <summary>Compares configuration values, including the contents of immutable arrays.</summary>
    private static bool SameConfiguration(ProjectConfiguration left, ProjectConfiguration right) =>
        // Record equality compares immutable arrays by reference rather than by their contents.
        JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    // Decodes the base64 archive. The size check runs on the text first, so oversized input is never decoded.
    private static byte[] DecodeAgentSuiteArchive(string archive)
    {
        if (string.IsNullOrEmpty(archive))
            throw ProjectConfigException.Invalid("invalid_agentsuite_archive", "The archive is empty.");
        // Base64 uses 4 characters per 3 bytes, so this is the longest text that can decode within the limit.
        if (archive.Length > (AgentSuiteValidator.MaxArchiveBytes + 2) / 3 * 4)
            throw ProjectConfigException.Invalid(
                "agentsuite_archive_too_large",
                $"The archive exceeds {AgentSuiteValidator.MaxArchiveBytes} bytes.");
        try
        {
            return Convert.FromBase64String(archive);
        }
        catch (FormatException)
        {
            throw ProjectConfigException.Invalid("invalid_agentsuite_archive", "The archive is not valid base64.");
        }
    }

    /// <summary>
    /// Derives a stable proposal ID so a replayed request cannot create a second proposal. The canonical record
    /// covers the source, tool connections and blueprint data, so a different binding never replays.
    /// </summary>
    private static Guid AgentSuiteProposalId(string projectId, ProjectAgentSuiteImport record, long expectedRevision)
    {
        // Hash the canonical record JSON so any change in record content gives a different proposal ID.
        var recordDigest = AgentSuiteObjectStore.Digest(
            CanonicalJson.Canonicalize(JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions)));
        // Include the project and expected revision: the same suite imported at a new revision is a new proposal.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"agentweaver-agentsuite-import\n{projectId}\n{record.SourceIdentity}\n{recordDigest}\n{expectedRevision}"));
        // A GUID holds 16 bytes, so use the first 16 bytes of the hash.
        return new Guid(hash.AsSpan(0, 16));
    }
}
