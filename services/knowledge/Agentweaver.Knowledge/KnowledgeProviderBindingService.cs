using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Providers;
using Agentweaver.Telemetry;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Knowledge;

public sealed record KnowledgeProviderContext(
    IMemoryProvider Provider,
    PinnedProviderBinding Binding,
    long ProjectConfigurationRevision,
    string ContextRevision,
    ProjectRunSelectionResponse Selection);

public sealed class KnowledgeProviderBindingService(
    NpgsqlDataSource dataSource,
    NativePostgresMemoryOptions options,
    ProviderCatalog catalog,
    ProviderResolver resolver,
    ProjectsConfigClient projects,
    IReadOnlyDictionary<string, IMemoryProvider> providers)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ImmutableHashSet<string> OriginalSixMemoryCapabilities =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "memory.records.read",
            "memory.records.write",
            "memory.records.search",
            "memory.records.revisions",
            "memory.proposals.promote",
            "memory.context.compose");

    public async Task<KnowledgeProviderContext> ResolveAndVerifyAsync(
        ProjectAuthorizationContextResponse authority,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
        => await ResolveAsync(authority, projectId, runId, allowCreate: true, cancellationToken)
            .ConfigureAwait(false);

    public async Task<KnowledgeProviderContext> ResolveExistingAndVerifyAsync(
        ProjectAuthorizationContextResponse authority,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
        => await ResolveAsync(authority, projectId, runId, allowCreate: false, cancellationToken)
            .ConfigureAwait(false);

    private async Task<KnowledgeProviderContext> ResolveAsync(
        ProjectAuthorizationContextResponse authority,
        string projectId,
        string runId,
        bool allowCreate,
        CancellationToken cancellationToken)
    {
        ProjectsConfigClient.RequireRunSelectionPermission(authority, projectId);
        var selection = await projects.GetRunSelectionAsync(projectId, runId, cancellationToken)
            .ConfigureAwait(false);
        var memorySelections = selection.Providers
            .Where(item => item.Seam == ProviderSeam.Memory)
            .ToArray();
        if (memorySelections.Length != 1 ||
            memorySelections[0].Cardinality != ProviderCardinality.Exclusive ||
            memorySelections[0].Candidates.IsDefault ||
            memorySelections[0].Candidates.Length != 1)
            throw new KnowledgeProviderUnavailableException(
                "Projects & Config did not select exactly one exclusive Memory provider for this run.");

        var selected = memorySelections[0].Candidates[0];
        if (selected.Seam != ProviderSeam.Memory ||
            !Version.TryParse(selected.AdapterVersion, out var adapterVersion) ||
            selected.OptionsSchemaVersion < 1 ||
            string.IsNullOrWhiteSpace(selected.OptionsRevision) ||
            selected.AdvertisedCapabilities.IsDefault ||
            selected.RequiredCapabilities.IsDefault ||
            selected.AdvertisedCapabilities.Any(string.IsNullOrWhiteSpace) ||
            selected.RequiredCapabilities.Any(string.IsNullOrWhiteSpace))
            throw new KnowledgeProviderUnavailableException(
                "The selected Memory provider metadata is invalid.");

        var selectedCapabilities = selected.AdvertisedCapabilities
            .ToImmutableHashSet(StringComparer.Ordinal);
        var selectedRequiredCapabilities = selected.RequiredCapabilities
            .ToImmutableHashSet(StringComparer.Ordinal);
        var originalSixSelection =
            selectedCapabilities.SetEquals(OriginalSixMemoryCapabilities) &&
            selectedRequiredCapabilities.SetEquals(OriginalSixMemoryCapabilities);

        if (!catalog.TryGetProvider(selected.ProviderId, out var registration) ||
            registration is null ||
            registration.Descriptor.Seam != ProviderSeam.Memory ||
            registration.Descriptor.AdapterVersion != adapterVersion ||
            registration.OptionsSchemaVersion != selected.OptionsSchemaVersion ||
            !string.Equals(registration.OptionsRevision, selected.OptionsRevision, StringComparison.Ordinal) ||
            registration.Descriptor.Hosting != selected.Hosting ||
            (!registration.Descriptor.AdvertisedCapabilities.SetEquals(selectedCapabilities) &&
             !(originalSixSelection &&
               OriginalSixMemoryCapabilities.IsSubsetOf(registration.Descriptor.AdvertisedCapabilities))))
            throw new KnowledgeProviderUnavailableException(
                "The selected Memory provider no longer matches the catalog snapshot supplied to Knowledge.");

        if (!providers.TryGetValue(selected.ProviderId, out var provider))
            throw new KnowledgeProviderUnavailableException(
                $"The selected Memory provider '{selected.ProviderId}' has no available adapter.");

        if (!selectedRequiredCapabilities.IsSubsetOf(selectedCapabilities))
            throw new KnowledgeProviderUnavailableException(
                "The selected Memory provider requires a capability that its selection does not advertise.");

        var requiredCapabilities = (originalSixSelection
                ? OriginalSixMemoryCapabilities
                : MemoryProviderCapabilities.All)
            .Union(selectedRequiredCapabilities, StringComparer.Ordinal)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var advertised = selectedCapabilities;
        if (!requiredCapabilities.IsSubsetOf(advertised))
            throw new KnowledgeProviderUnavailableException(
                "The selected Memory provider does not advertise every capability required for this operation.");

        catalog.TryGetDefault(ProviderSeam.Memory, out var defaultProviderId);
        var projectOverrideId = string.Equals(
            defaultProviderId, selected.ProviderId, StringComparison.Ordinal)
            ? null
            : selected.ProviderId;
        var resolution = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Memory,
            projectOverrideId,
            adapterVersion,
            selected.OptionsSchemaVersion,
            requiredCapabilities));
        if (!resolution.IsSuccess || resolution.Value?.Candidate is not { } candidate)
            throw new KnowledgeProviderUnavailableException(
                resolution.Error?.Message ?? "The selected Memory provider cannot be resolved.");

        var negotiation = await provider.NegotiateAsync(candidate, cancellationToken)
            .ConfigureAwait(false);
        var snapshotNegotiation = negotiation with
        {
            Capabilities = negotiation.Capabilities
                .Where(advertised.Contains)
                .ToImmutableHashSet(StringComparer.Ordinal)
        };
        var pinned = resolver.Pin(
            runId, candidate, snapshotNegotiation.Resource.ResourceId, snapshotNegotiation);
        if (!pinned.IsSuccess || pinned.Value is null)
            throw new KnowledgeProviderUnavailableException(
                pinned.Error?.Message ?? "The selected Memory provider resource could not be pinned.");

        if (allowCreate)
            await PersistOrVerifyBindingAsync(
                projectId,
                selection,
                pinned.Value,
                cancellationToken).ConfigureAwait(false);
        else
            await VerifyPersistedBindingAsync(
                projectId,
                selection,
                pinned.Value,
                cancellationToken).ConfigureAwait(false);
        RecordPinnedBinding(selected.ProviderId, pinned.Value);
        return new KnowledgeProviderContext(
            provider,
            pinned.Value,
            selection.ProjectConfigurationRevision,
            selection.ContextRevision,
            selection);
    }

    private async Task PersistOrVerifyBindingAsync(
        string projectId,
        ProjectRunSelectionResponse selection,
        PinnedProviderBinding binding,
        CancellationToken cancellationToken)
    {
        var schema = $"\"{options.Schema}\"";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var capabilities = JsonSerializer.Serialize(
            binding.NegotiatedCapabilities.OrderBy(value => value, StringComparer.Ordinal), JsonOptions);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {schema}.memory_provider_bindings
                (project_id, run_id, project_revision, project_configuration_revision, context_revision,
                 provider_id, adapter_version, options_schema_version, options_revision,
                 resource_id, resource_generation, negotiated_capabilities)
            VALUES
                (@project, @run, @project_revision, @config_revision, @context_revision,
                 @provider, @version, @schema_version, @options_revision,
                 @resource, @generation, @capabilities)
            ON CONFLICT (project_id, run_id) DO NOTHING
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            insert.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, selection.RunId);
            insert.Parameters.AddWithValue("project_revision", NpgsqlDbType.Bigint, selection.ProjectRevision);
            insert.Parameters.AddWithValue("config_revision", NpgsqlDbType.Bigint,
                selection.ProjectConfigurationRevision);
            insert.Parameters.AddWithValue("context_revision", NpgsqlDbType.Varchar, selection.ContextRevision);
            insert.Parameters.AddWithValue("provider", NpgsqlDbType.Varchar, binding.ProviderId);
            insert.Parameters.AddWithValue("version", NpgsqlDbType.Varchar, binding.AdapterVersion.ToString());
            insert.Parameters.AddWithValue("schema_version", NpgsqlDbType.Integer, binding.OptionsSchemaVersion);
            insert.Parameters.AddWithValue("options_revision", NpgsqlDbType.Varchar, binding.OptionsRevision);
            insert.Parameters.AddWithValue("resource", NpgsqlDbType.Varchar, binding.Resource.ResourceId);
            insert.Parameters.AddWithValue("generation", NpgsqlDbType.Bigint, binding.Resource.Generation);
            insert.Parameters.AddWithValue("capabilities", NpgsqlDbType.Jsonb, capabilities);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await VerifyBindingAsync(connection, transaction, projectId, selection, binding, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyPersistedBindingAsync(
        string projectId,
        ProjectRunSelectionResponse selection,
        PinnedProviderBinding binding,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await VerifyBindingAsync(connection, transaction, projectId, selection, binding, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        ProjectRunSelectionResponse selection,
        PinnedProviderBinding binding,
        CancellationToken cancellationToken)
    {
        var schema = $"\"{options.Schema}\"";
        await using (var read = new NpgsqlCommand($"""
            SELECT project_revision, project_configuration_revision, context_revision,
                   provider_id, adapter_version,
                   options_schema_version, options_revision, resource_id, resource_generation,
                   negotiated_capabilities
            FROM {schema}.memory_provider_bindings
            WHERE project_id = @project AND run_id = @run
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, projectId);
            read.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, selection.RunId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new KnowledgeProviderUnavailableException(
                    "This run has no persisted Memory provider binding.");
            using var document = JsonDocument.Parse(reader.GetString(9));
            var persistedCapabilities = document.RootElement.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToImmutableHashSet(StringComparer.Ordinal);
            if (reader.GetInt64(0) != selection.ProjectRevision ||
                reader.GetInt64(1) != selection.ProjectConfigurationRevision ||
                !string.Equals(reader.GetString(2), selection.ContextRevision, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(3), binding.ProviderId, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(4), binding.AdapterVersion.ToString(), StringComparison.Ordinal) ||
                reader.GetInt32(5) != binding.OptionsSchemaVersion ||
                !string.Equals(reader.GetString(6), binding.OptionsRevision, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(7), binding.Resource.ResourceId, StringComparison.Ordinal) ||
                reader.GetInt64(8) != binding.Resource.Generation ||
                !persistedCapabilities.SetEquals(binding.NegotiatedCapabilities))
                throw new KnowledgeApiException(
                    "memory_provider_binding_conflict",
                    "This run already has a different immutable Memory provider binding.",
                    StatusCodes.Status409Conflict);
        }
    }

    private static void RecordPinnedBinding(string selectedProviderId, PinnedProviderBinding binding)
    {
        using var activity = TelemetrySignals.Activities.StartActivity(
            "knowledge.provider.binding.pinned", ActivityKind.Internal);
        if (activity is null)
            return;
        activity.SetTag("provider.seam", ProviderSeam.Memory.ToString());
        activity.SetTag("provider.resolved.id", selectedProviderId);
        activity.SetTag("provider.pinned.id", binding.ProviderId);
        activity.SetTag("provider.adapter.version", binding.AdapterVersion.ToString());
        activity.SetTag("provider.options.schema_version", binding.OptionsSchemaVersion);
        activity.SetTag("provider.options.revision", binding.OptionsRevision);
        activity.SetTag("provider.negotiated_capabilities",
            binding.NegotiatedCapabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        activity.SetTag("provider.resource.id_hash",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding.Resource.ResourceId)))[..24]);
        activity.SetTag("provider.resource.generation", binding.Resource.Generation);
    }
}
