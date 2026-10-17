using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Defines the server-owned boundary for negotiating an existing Sandbox resource without provisioning or releasing it.
/// </summary>
internal interface ICoordinatorSandboxResourceProvider
{
    /// <summary>
    /// Gets the stable identifier of the Sandbox provider served by this adapter.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// Gets the isolation choices the adapter can negotiate for an existing resource.
    /// </summary>
    ImmutableArray<string> IsolationChoices { get; }

    // This boundary may resolve an existing registered resource, but must not provision or release one.
    /// <summary>
    /// Negotiates capabilities against an already registered resource for the accepted run.
    /// </summary>
    /// <remarks>The provider boundary must not create or delete a resource.</remarks>
    Task<ProviderResult<ResourceNegotiation>> NegotiateAsync(
        ProviderCandidate candidate,
        EffectiveRunSelection selection,
        CancellationToken cancellationToken);
}

/// <summary>
/// Returns a run's validated selection context and any binding that still needs durable persistence.
/// </summary>
internal sealed record CoordinatorRunSelectionContextResolution(
    WorkPlanRunSelectionContext Context,
    PendingCoordinatorRunSelectionBinding? PendingBinding);

/// <summary>
/// Holds an accepted candidate and its completed negotiation until the owner transaction pins it.
/// </summary>
internal sealed record PendingCoordinatorRunSelectionBinding(
    EffectiveRunSelection Selection,
    long ExecutionFence,
    EffectiveProviderCandidate SelectedCandidate,
    ResourceNegotiation Negotiation,
    ImmutableArray<string> IsolationChoices,
    ImmutableArray<RoleRunSelection> Roles,
    WorkPlanRunSelectionContext Context);

/// <summary>
/// Resolves and pins provider, role, and task-model selections used to validate a coordinator work plan.
/// </summary>
internal sealed class CoordinatorRunSelectionContextStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _bindings;
    private readonly ProviderCatalog _catalog;
    private readonly ProviderResolver _resolver;
    private readonly ImmutableDictionary<string, ICoordinatorSandboxResourceProvider> _providers;

    /// <summary>
    /// Creates the store and indexes unique server-registered Sandbox resource providers by stable identifier.
    /// </summary>
    /// <exception cref="ArgumentException">The schema or registered provider identities and choices are invalid.</exception>
    public CoordinatorRunSelectionContextStore(
        NpgsqlDataSource dataSource,
        string schema,
        ProviderCatalog catalog,
        ProviderResolver resolver,
        IEnumerable<ICoordinatorSandboxResourceProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(providers);
        if (schema is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                schema, "^[a-z][a-z0-9_]{0,62}\\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));

        _dataSource = dataSource;
        _bindings = $"\"{schema}\".coordinator_run_selection_context_versions";
        _catalog = catalog;
        _resolver = resolver;
        var providerBuilder = ImmutableDictionary.CreateBuilder<string, ICoordinatorSandboxResourceProvider>(
            StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider is null ||
                !IsStableIdentifier(provider.ProviderId) ||
                provider.IsolationChoices.IsDefault ||
                provider.IsolationChoices.Any(choice => !IsStableIdentifier(choice)) ||
                provider.IsolationChoices.Distinct(StringComparer.Ordinal).Count() !=
                provider.IsolationChoices.Length ||
                !providerBuilder.TryAdd(provider.ProviderId, provider))
                throw new ArgumentException(
                    "Sandbox providers must have unique stable IDs and isolation choices.", nameof(providers));
        }
        _providers = providerBuilder.ToImmutable();
    }

    /// <summary>
    /// Rehydrates a Source Control repository pin using the server's provider catalog and resolver.
    /// </summary>
    public SourceControlRepositoryPin RestoreSourceControlRepositoryPin(string serializedPin) =>
        SourceControlOwnerStore.RestorePin(serializedPin, _catalog, _resolver);

    /// <summary>
    /// Reuses a persisted run context or negotiates a server-registered Sandbox candidate for first-time pinning.
    /// </summary>
    /// <exception cref="CoordinationException">The selection has no unique valid candidate or the resource provider is unavailable.</exception>
    public async Task<CoordinatorRunSelectionContextResolution> ResolveForPlanAsync(
        EffectiveRunSelection selection,
        long executionFence,
        CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(selection, executionFence, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return new CoordinatorRunSelectionContextResolution(existing, PendingBinding: null);

        var selected = ReadSelectedSandboxCandidate(selection.Snapshot);
        var candidate = ResolveCandidate(selected);
        var provider = RequireProvider(candidate.ProviderId);
        var negotiationResult = await provider.NegotiateAsync(
            candidate, selection, cancellationToken).ConfigureAwait(false);
        if (!negotiationResult.IsSuccess || negotiationResult.Value is null)
            throw BindingUnavailable("The registered Sandbox provider could not negotiate a run resource.");

        var negotiation = negotiationResult.Value;
        var pinned = _resolver.Pin(
            selection.RunId,
            candidate,
            negotiation.Resource?.ResourceId ?? string.Empty,
            negotiation);
        if (!pinned.IsSuccess || pinned.Value is null)
            throw BindingUnavailable(
                pinned.Error?.Message ?? "The registered Sandbox provider returned an invalid negotiation.");

        var baseContext = CoordinatorWorkflowCatalog.CreateRunSelectionContext(selection.Snapshot);
        var roleContext = CreateAuthorizedRoles(baseContext.Roles, provider.IsolationChoices);
        var context = new WorkPlanRunSelectionContext(roleContext, pinned.Value)
        {
            ModelTaskSelections = baseContext.ModelTaskSelections
        };
        var pendingBinding = new PendingCoordinatorRunSelectionBinding(
            selection,
            executionFence,
            selected,
            negotiation,
            provider.IsolationChoices,
            roleContext,
            context);
        return new CoordinatorRunSelectionContextResolution(context, pendingBinding);
    }

    /// <summary>
    /// Reads and revalidates the persisted role and Sandbox binding for one execution fence.
    /// </summary>
    /// <returns>The exact stored context, or <see langword="null"/> when that fence has no binding.</returns>
    /// <exception cref="CoordinationException">The stored binding no longer matches the accepted selection or server catalog.</exception>
    public async Task<WorkPlanRunSelectionContext?> ReadAsync(
        EffectiveRunSelection selection,
        long executionFence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (executionFence <= 0)
            throw new CoordinationException("coordinator_decision_invalid", StatusCodes.Status400BadRequest);

        PersistedBinding? persisted;
        await using (var connection = await _dataSource.OpenConnectionAsync(cancellationToken))
        await using (var command = new NpgsqlCommand($"""
            SELECT accepted_selection_hash, project_revision, project_configuration_revision,
                   platform_runtime_revision, context_revision, execution_fence, role_context,
                   provider_id, adapter_version, options_schema_version, options_revision,
                   hosting, resource_id, resource_generation, advertised_capabilities,
                   required_capabilities, negotiated_capabilities, isolation_choices
            FROM {_bindings}
            WHERE project_id = @project AND run_id = @run AND execution_fence = @fence
            """, connection))
        {
            command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, selection.ProjectId);
            command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, selection.RunId);
            command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;
            persisted = ReadPersistedBinding(reader);
        }

        if (persisted.SelectionHash != HashSelection(selection) ||
            persisted.ProjectRevision != selection.ProjectRevision ||
            persisted.ProjectConfigurationRevision != selection.ProjectConfigurationRevision ||
            persisted.PlatformRuntimeRevision != selection.PlatformRuntimeRevision ||
            persisted.ContextRevision != selection.ContextRevision ||
            persisted.ExecutionFence != executionFence)
            throw new CoordinationException(
                "coordinator_sandbox_binding_stale", StatusCodes.Status409Conflict);

        var selected = ReadSelectedSandboxCandidate(selection.Snapshot);
        if (!persisted.Matches(selected))
            throw BindingUnavailable("The accepted Sandbox candidate no longer matches its persisted run binding.");
        var candidate = ResolveCandidate(selected);
        var provider = RequireProvider(candidate.ProviderId);
        if (!provider.IsolationChoices.SequenceEqual(persisted.IsolationChoices, StringComparer.Ordinal))
            throw BindingUnavailable("The registered Sandbox provider's isolation choices have changed.");

        var negotiation = new ResourceNegotiation(
            new ProviderResourceRef(
                ProviderSeam.Sandbox,
                persisted.ProviderId,
                persisted.ResourceId,
                persisted.ResourceGeneration),
            persisted.NegotiatedCapabilities.ToImmutableHashSet(StringComparer.Ordinal));
        var pinned = _resolver.Pin(selection.RunId, candidate, persisted.ResourceId, negotiation);
        if (!pinned.IsSuccess || pinned.Value is null)
            throw BindingUnavailable(
                pinned.Error?.Message ?? "The exact persisted Sandbox binding could not be reconstructed.");

        var baseContext = CoordinatorWorkflowCatalog.CreateRunSelectionContext(selection.Snapshot);
        var expectedRoles = CreateAuthorizedRoles(baseContext.Roles, persisted.IsolationChoices);
        if (!RolesMatch(expectedRoles, persisted.Roles))
            throw BindingUnavailable("The durable role-selection context does not match the accepted run selection.");

        return new WorkPlanRunSelectionContext(persisted.Roles, pinned.Value)
        {
            ModelTaskSelections = baseContext.ModelTaskSelections
        };
    }

    /// <summary>
    /// Saves a newly negotiated binding in the decision transaction that accepts its work plan.
    /// </summary>
    /// <exception cref="CoordinationException">The pending binding is stale or another binding already occupies the run fence.</exception>
    internal async Task PersistBindingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EffectiveRunSelection selection,
        long executionFence,
        PendingCoordinatorRunSelectionBinding pendingBinding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(pendingBinding);
        if (executionFence <= 0 ||
            pendingBinding.ExecutionFence != executionFence ||
            !SelectionMatches(pendingBinding.Selection, selection) ||
            !RolesMatch(pendingBinding.Roles, pendingBinding.Context.Roles) ||
            !TaskSelectionsMatch(
                CoordinatorWorkflowCatalog.CreateRunSelectionContext(selection.Snapshot).ModelTaskSelections,
                pendingBinding.Context.ModelTaskSelections))
            throw new CoordinationException(
                "coordinator_sandbox_binding_stale", StatusCodes.Status409Conflict);

        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_bindings}
                (project_id, run_id, accepted_selection_hash, project_revision,
                 project_configuration_revision, platform_runtime_revision, context_revision,
                 execution_fence, role_context, provider_id, adapter_version,
                 options_schema_version, options_revision, hosting, resource_id,
                 resource_generation, advertised_capabilities, required_capabilities,
                 negotiated_capabilities, isolation_choices)
            VALUES
                (@project, @run, @selection_hash, @project_revision,
                 @configuration_revision, @platform_revision, @context_revision,
                 @fence, @roles, @provider, @adapter_version,
                 @schema_version, @options_revision, @hosting, @resource,
                 @generation, @advertised, @required, @negotiated, @isolation_choices)
            ON CONFLICT (project_id, run_id, execution_fence) DO NOTHING
            """, connection);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, selection.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, selection.RunId);
        command.Parameters.AddWithValue("selection_hash", NpgsqlDbType.Char, HashSelection(selection));
        command.Parameters.AddWithValue("project_revision", NpgsqlDbType.Bigint, selection.ProjectRevision);
        command.Parameters.AddWithValue(
            "configuration_revision", NpgsqlDbType.Bigint, selection.ProjectConfigurationRevision);
        command.Parameters.AddWithValue(
            "platform_revision", NpgsqlDbType.Bigint, selection.PlatformRuntimeRevision);
        command.Parameters.AddWithValue("context_revision", NpgsqlDbType.Varchar, selection.ContextRevision);
        command.Parameters.AddWithValue("fence", NpgsqlDbType.Bigint, executionFence);
        command.Parameters.AddWithValue(
            "roles", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(pendingBinding.Roles, JsonOptions));
        command.Parameters.AddWithValue(
            "provider", NpgsqlDbType.Varchar, pendingBinding.SelectedCandidate.ProviderId);
        command.Parameters.AddWithValue(
            "adapter_version", NpgsqlDbType.Varchar, pendingBinding.SelectedCandidate.AdapterVersion);
        command.Parameters.AddWithValue(
            "schema_version", NpgsqlDbType.Integer, pendingBinding.SelectedCandidate.OptionsSchemaVersion);
        command.Parameters.AddWithValue(
            "options_revision", NpgsqlDbType.Varchar, pendingBinding.SelectedCandidate.OptionsRevision);
        command.Parameters.AddWithValue(
            "hosting", NpgsqlDbType.Varchar, pendingBinding.SelectedCandidate.Hosting.ToString());
        command.Parameters.AddWithValue(
            "resource", NpgsqlDbType.Varchar, pendingBinding.Negotiation.Resource.ResourceId);
        command.Parameters.AddWithValue(
            "generation", NpgsqlDbType.Bigint, pendingBinding.Negotiation.Resource.Generation);
        command.Parameters.AddWithValue(
            "advertised",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(pendingBinding.SelectedCandidate.AdvertisedCapabilities, JsonOptions));
        command.Parameters.AddWithValue(
            "required",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(pendingBinding.SelectedCandidate.RequiredCapabilities, JsonOptions));
        command.Parameters.AddWithValue(
            "negotiated",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(pendingBinding.Negotiation.Capabilities, JsonOptions));
        command.Parameters.AddWithValue(
            "isolation_choices",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(pendingBinding.IsolationChoices, JsonOptions));
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted != 1)
            throw new CoordinationException(
                "coordinator_sandbox_binding_conflict", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Copies a validated provider binding from the previous execution fence to its immediate successor.
    /// </summary>
    /// <exception cref="CoordinationException">The fence sequence is invalid or the source binding is missing.</exception>
    internal async Task CarryForwardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EffectiveRunSelection selection,
        long previousFence,
        long nextFence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(selection);
        if (previousFence < 1 || nextFence != previousFence + 1)
            throw new CoordinationException(
                "coordinator_sandbox_binding_stale", StatusCodes.Status409Conflict);

        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_bindings}
                (project_id, run_id, accepted_selection_hash, project_revision,
                 project_configuration_revision, platform_runtime_revision, context_revision,
                 execution_fence, role_context, provider_id, adapter_version,
                 options_schema_version, options_revision, hosting, resource_id,
                 resource_generation, advertised_capabilities, required_capabilities,
                 negotiated_capabilities, isolation_choices)
            SELECT project_id, run_id, accepted_selection_hash, project_revision,
                   project_configuration_revision, platform_runtime_revision, context_revision,
                   @nextFence, role_context, provider_id, adapter_version,
                   options_schema_version, options_revision, hosting, resource_id,
                   resource_generation, advertised_capabilities, required_capabilities,
                   negotiated_capabilities, isolation_choices
            FROM {_bindings}
            WHERE project_id = @project AND run_id = @run AND execution_fence = @previousFence
            """, connection, transaction);
        command.Parameters.AddWithValue("project", NpgsqlDbType.Varchar, selection.ProjectId);
        command.Parameters.AddWithValue("run", NpgsqlDbType.Varchar, selection.RunId);
        command.Parameters.AddWithValue("previousFence", NpgsqlDbType.Bigint, previousFence);
        command.Parameters.AddWithValue("nextFence", NpgsqlDbType.Bigint, nextFence);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new CoordinationException(
                "coordinator_sandbox_binding_conflict", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Resolves an accepted provider candidate against the server catalog and selected required capabilities.
    /// </summary>
    /// <exception cref="CoordinationException">The candidate is malformed, disabled, or differs from server registration.</exception>
    private ProviderCandidate ResolveCandidate(EffectiveProviderCandidate selected)
    {
        if (!Version.TryParse(selected.AdapterVersion, out var adapterVersion) ||
            selected.OptionsSchemaVersion < 1 ||
            string.IsNullOrWhiteSpace(selected.OptionsRevision) ||
            selected.AdvertisedCapabilities.IsDefault ||
            selected.RequiredCapabilities.IsDefault)
            throw BindingUnavailable("The accepted Sandbox provider candidate is malformed.");

        if (!_catalog.TryGetProvider(selected.ProviderId, out var registration) ||
            registration is null ||
            !registration.Enabled ||
            registration.Descriptor.Seam != ProviderSeam.Sandbox ||
            registration.Descriptor.AdapterVersion != adapterVersion ||
            registration.OptionsSchemaVersion != selected.OptionsSchemaVersion ||
            registration.Descriptor.OptionsSchemaVersion != selected.OptionsSchemaVersion ||
            registration.OptionsRevision != selected.OptionsRevision ||
            registration.Descriptor.Hosting != selected.Hosting ||
            !registration.Descriptor.AdvertisedCapabilities.SetEquals(
                selected.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)))
            throw BindingUnavailable("The accepted Sandbox provider candidate is not in the server catalog.");

        _catalog.TryGetDefault(ProviderSeam.Sandbox, out var defaultProviderId);
        var overrideId = string.Equals(defaultProviderId, selected.ProviderId, StringComparison.Ordinal)
            ? null
            : selected.ProviderId;
        var result = _resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Sandbox,
            overrideId,
            adapterVersion,
            selected.OptionsSchemaVersion,
            selected.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal)));
        var candidate = result.Value?.Candidate;
        if (!result.IsSuccess || candidate is null ||
            candidate.ProviderId != selected.ProviderId ||
            candidate.OptionsRevision != selected.OptionsRevision ||
            !candidate.AdvertisedCapabilities.SetEquals(
                selected.AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)) ||
            !candidate.RequiredCapabilities.SetEquals(
                selected.RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal)))
            throw BindingUnavailable(result.Error?.Message ?? "The accepted Sandbox provider could not be resolved.");
        return candidate;
    }

    /// <summary>
    /// Returns the configured resource adapter for a provider or fails closed when none is registered.
    /// </summary>
    private ICoordinatorSandboxResourceProvider RequireProvider(string providerId) =>
        _providers.TryGetValue(providerId, out var provider)
            ? provider
            : throw BindingUnavailable(
                $"The server has no registered Sandbox resource provider for '{providerId}'.");

    /// <summary>
    /// Reads the single exclusive Sandbox candidate embedded in an accepted selection snapshot.
    /// </summary>
    /// <exception cref="CoordinationException">The provider snapshot is absent, malformed, ambiguous, or not exclusive.</exception>
    private static EffectiveProviderCandidate ReadSelectedSandboxCandidate(JsonElement selection)
    {
        if (selection.ValueKind != JsonValueKind.Object ||
            !selection.TryGetProperty("providers", out var providers) ||
            providers.ValueKind != JsonValueKind.Array)
            throw BindingUnavailable("The accepted run selection has no provider-candidate snapshot.");
        ImmutableArray<EffectiveProviderSelection> decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<ImmutableArray<EffectiveProviderSelection>>(
                providers.GetRawText(), JsonOptions);
        }
        catch (JsonException)
        {
            throw BindingUnavailable("The accepted provider-candidate snapshot is invalid.");
        }

        if (decoded.IsDefault)
            throw BindingUnavailable("The accepted provider-candidate snapshot is uninitialized.");
        var sandbox = decoded.Where(item => item.Seam == ProviderSeam.Sandbox).ToArray();
        if (sandbox.Length != 1 ||
            sandbox[0].Cardinality != ProviderCardinality.Exclusive ||
            sandbox[0].Candidates.IsDefault ||
            sandbox[0].Candidates.Length != 1)
            throw BindingUnavailable(
                "The accepted run selection must contain exactly one exclusive Sandbox candidate.");
        var candidate = sandbox[0].Candidates[0];
        if (candidate.Seam != ProviderSeam.Sandbox ||
            !IsStableIdentifier(candidate.ProviderId) ||
            candidate.AdvertisedCapabilities.IsDefault ||
            candidate.AdvertisedCapabilities.Any(string.IsNullOrWhiteSpace) ||
            candidate.AdvertisedCapabilities.Distinct(StringComparer.Ordinal).Count() !=
            candidate.AdvertisedCapabilities.Length ||
            candidate.RequiredCapabilities.IsDefault ||
            candidate.RequiredCapabilities.Any(string.IsNullOrWhiteSpace) ||
            candidate.RequiredCapabilities.Distinct(StringComparer.Ordinal).Count() !=
            candidate.RequiredCapabilities.Length)
            throw BindingUnavailable("The accepted Sandbox provider candidate is invalid.");
        return candidate;
    }

    /// <summary>
    /// Adds the provider's supported isolation choices to every authorized role selection.
    /// </summary>
    private static ImmutableArray<RoleRunSelection> CreateAuthorizedRoles(
        ImmutableArray<RoleRunSelection> roles,
        ImmutableArray<string> isolationChoices) =>
        roles.Select(role => role with { EligibleIsolationChoices = isolationChoices })
            .ToImmutableArray();

    /// <summary>
    /// Compares role contexts using the same deterministic JSON representation used for persistence.
    /// </summary>
    private static bool RolesMatch(
        ImmutableArray<RoleRunSelection> expected,
        ImmutableArray<RoleRunSelection> persisted) =>
        !expected.IsDefault &&
        !persisted.IsDefault &&
        string.Equals(
            JsonSerializer.Serialize(expected, JsonOptions),
            JsonSerializer.Serialize(persisted, JsonOptions),
            StringComparison.Ordinal);

    private static bool TaskSelectionsMatch(
        ImmutableArray<ModelTaskSelectionReference> expected,
        ImmutableArray<ModelTaskSelectionReference> persisted) =>
        !expected.IsDefault &&
        !persisted.IsDefault &&
        string.Equals(
            JsonSerializer.Serialize(expected, JsonOptions),
            JsonSerializer.Serialize(persisted, JsonOptions),
            StringComparison.Ordinal);

    /// <summary>
    /// Decodes the durable row and converts malformed fields into a fail-closed provider error.
    /// </summary>
    private static PersistedBinding ReadPersistedBinding(NpgsqlDataReader reader)
    {
        try
        {
            return new PersistedBinding(
                reader.GetString(0).TrimEnd(),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt64(5),
                JsonSerializer.Deserialize<ImmutableArray<RoleRunSelection>>(reader.GetString(6), JsonOptions),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetInt32(9),
                reader.GetString(10),
                Enum.Parse<ProviderHostingPattern>(reader.GetString(11), ignoreCase: false),
                reader.GetString(12),
                reader.GetInt64(13),
                ReadStringArray(reader.GetString(14)),
                ReadStringArray(reader.GetString(15)),
                ReadStringArray(reader.GetString(16)),
                ReadStringArray(reader.GetString(17)));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw BindingUnavailable("The durable accepted Sandbox binding is invalid.");
        }
    }

    /// <summary>
    /// Reads a persisted unique string array and rejects uninitialized, blank, or repeated values.
    /// </summary>
    private static ImmutableArray<string> ReadStringArray(string json)
    {
        var values = JsonSerializer.Deserialize<ImmutableArray<string>>(json, JsonOptions);
        if (values.IsDefault || values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new JsonException("The persisted string list is invalid.");
        return values;
    }

    /// <summary>
    /// Hashes the accepted selection snapshot so the binding cannot be reused with different run inputs.
    /// </summary>
    private static string HashSelection(EffectiveRunSelection selection) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selection.Snapshot.GetRawText())));

    /// <summary>
    /// Compares project, run, revisions, context, and selection hash before carrying a pending binding forward.
    /// </summary>
    private static bool SelectionMatches(EffectiveRunSelection first, EffectiveRunSelection second) =>
        first.ProjectId == second.ProjectId &&
        first.RunId == second.RunId &&
        first.ProjectRevision == second.ProjectRevision &&
        first.ProjectConfigurationRevision == second.ProjectConfigurationRevision &&
        first.PlatformRuntimeRevision == second.PlatformRuntimeRevision &&
        first.ContextRevision == second.ContextRevision &&
        HashSelection(first) == HashSelection(second);

    /// <summary>
    /// Checks that a provider, role, or choice identifier uses the supported stable identifier syntax.
    /// </summary>
    private static bool IsStableIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= WorkflowDomainLimits.MaximumIdentifierLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is '.' or '_' or '-' or ':');

    /// <summary>
    /// Creates the consistent service-unavailable error used when an exact Sandbox binding cannot be supplied.
    /// </summary>
    private static CoordinationException BindingUnavailable(string message) =>
        new("coordinator_sandbox_provider_unavailable", StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// Holds the validated durable Sandbox and role-selection data for one run fence.
    /// </summary>
    private sealed record PersistedBinding(
        string SelectionHash,
        long ProjectRevision,
        long ProjectConfigurationRevision,
        long PlatformRuntimeRevision,
        string ContextRevision,
        long ExecutionFence,
        ImmutableArray<RoleRunSelection> Roles,
        string ProviderId,
        string AdapterVersion,
        int OptionsSchemaVersion,
        string OptionsRevision,
        ProviderHostingPattern Hosting,
        string ResourceId,
        long ResourceGeneration,
        ImmutableArray<string> AdvertisedCapabilities,
        ImmutableArray<string> RequiredCapabilities,
        ImmutableArray<string> NegotiatedCapabilities,
        ImmutableArray<string> IsolationChoices)
    {
        /// <summary>
        /// Checks all selected provider descriptor fields against the accepted selection candidate.
        /// </summary>
        public bool Matches(EffectiveProviderCandidate candidate) =>
            ProviderId == candidate.ProviderId &&
            AdapterVersion == candidate.AdapterVersion &&
            OptionsSchemaVersion == candidate.OptionsSchemaVersion &&
            OptionsRevision == candidate.OptionsRevision &&
            Hosting == candidate.Hosting &&
            AdvertisedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)
                .SetEquals(candidate.AdvertisedCapabilities) &&
            RequiredCapabilities.ToImmutableHashSet(StringComparer.Ordinal)
                .SetEquals(candidate.RequiredCapabilities);
    }
}
