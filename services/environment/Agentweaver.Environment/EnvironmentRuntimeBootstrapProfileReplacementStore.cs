using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

/// <summary>Persists and retrieves owner-scoped replacements for runtime bootstrap profiles.</summary>
public interface IEnvironmentRuntimeBootstrapProfileReplacementStore
{
    /// <summary>Reads a replacement by Environment owner and profile identifier.</summary>
    Task<EnvironmentRuntimeBootstrapProfileReplacement?> ReadAsync(
        EnvironmentOwnerIdentity owner,
        string profileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Appends a replacement once, then rechecks authority and lease expiry before committing it.
    /// An existing different value is treated as a conflict.
    /// </summary>
    Task<EnvironmentRuntimeBootstrapProfileReplacement> AppendAsync(
        EnvironmentRuntimeBootstrapProfileReplacement replacement,
        Func<CancellationToken, Task> requireCurrentAuthority,
        CancellationToken cancellationToken);
}

/// <summary>PostgreSQL implementation that stores immutable runtime bootstrap profile replacements.</summary>
public sealed class EnvironmentRuntimeBootstrapProfileReplacementStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider) : IEnvironmentRuntimeBootstrapProfileReplacementStore
{
    private const string Replacements = "\"environment\".\"runtime_bootstrap_profile_replacements\"";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Reads and validates the saved owner/profile replacement, if one exists.</summary>
    public async Task<EnvironmentRuntimeBootstrapProfileReplacement?> ReadAsync(
        EnvironmentOwnerIdentity owner,
        string profileId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!EnvironmentRuntimeBootstrapProfile.IsIdentifier(profileId))
            throw new RuntimeAuthorizationException("runtime_delivery_unavailable");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"""
            SELECT replacement_json::text
            FROM {Replacements}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND profile_id = @profile_id
            """, connection);
        AddOwner(command, owner);
        command.Parameters.AddWithValue("profile_id", NpgsqlDbType.Text, profileId);
        var json = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : Deserialize(json);
    }

    /// <summary>
    /// Inserts the replacement if absent and verifies the canonical stored value before committing.
    /// Stale leases, oversized payloads, changed authority, and conflicting values are rejected.
    /// </summary>
    public async Task<EnvironmentRuntimeBootstrapProfileReplacement> AppendAsync(
        EnvironmentRuntimeBootstrapProfileReplacement replacement,
        Func<CancellationToken, Task> requireCurrentAuthority,
        CancellationToken cancellationToken)
    {
        replacement = (replacement ?? throw new ArgumentNullException(nameof(replacement))).Validate();
        ArgumentNullException.ThrowIfNull(requireCurrentAuthority);
        if (replacement.LeaseExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_bootstrap_profile_placement_stale");
        var json = JsonSerializer.Serialize(replacement, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 64 * 1024)
            throw new RuntimeAuthorizationException("runtime_bootstrap_profile_replacement_too_large");

        var owner = replacement.Registration.Owner;
        var profileId = replacement.Registration.Profile.ProfileId;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {Replacements}
                (tenant_id, project_id, run_id, environment_id, profile_id, source_profile_id,
                 lifecycle_generation, current_fencing_generation, provider_fencing_generation,
                 lease_revision, lease_expires_at, replacement_json, created_at)
            VALUES
                (@tenant_id, @project_id, @run_id, @environment_id, @profile_id, @source_profile_id,
                 @lifecycle_generation, @current_fencing_generation, @provider_fencing_generation,
                 @lease_revision, @lease_expires_at, @replacement_json, @created_at)
            ON CONFLICT (tenant_id, project_id, run_id, environment_id, profile_id) DO NOTHING
            """, connection, transaction))
        {
            AddOwner(insert, owner);
            insert.Parameters.AddWithValue("profile_id", NpgsqlDbType.Text, profileId);
            insert.Parameters.AddWithValue("source_profile_id", NpgsqlDbType.Text, replacement.SourceProfileId);
            insert.Parameters.AddWithValue("lifecycle_generation", NpgsqlDbType.Bigint, replacement.LifecycleGeneration);
            insert.Parameters.AddWithValue(
                "current_fencing_generation", NpgsqlDbType.Bigint, replacement.CurrentFencingGeneration);
            insert.Parameters.AddWithValue(
                "provider_fencing_generation", NpgsqlDbType.Bigint, replacement.ProviderFencingGeneration);
            insert.Parameters.AddWithValue("lease_revision", NpgsqlDbType.Bigint, replacement.LeaseRevision);
            insert.Parameters.AddWithValue("lease_expires_at", NpgsqlDbType.TimestampTz, replacement.LeaseExpiresAt);
            insert.Parameters.AddWithValue("replacement_json", NpgsqlDbType.Jsonb, json);
            insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var savedJson = await ReadJsonAsync(
            connection, transaction, owner, profileId, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_bootstrap_profile_replacement_missing");
        if (!SameJson(json, savedJson))
            throw new RuntimeAuthorizationException("runtime_bootstrap_profile_replacement_conflict");
        await requireCurrentAuthority(cancellationToken).ConfigureAwait(false);
        if (replacement.LeaseExpiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_bootstrap_profile_placement_stale");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Deserialize(savedJson);
    }

    /// <summary>Reads the replacement JSON inside the append transaction for compare-before-commit checks.</summary>
    private static async Task<string?> ReadJsonAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT replacement_json::text
            FROM {Replacements}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND profile_id = @profile_id
            """, connection, transaction);
        AddOwner(command, owner);
        command.Parameters.AddWithValue("profile_id", NpgsqlDbType.Text, profileId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deserializes and validates saved data, mapping malformed rows to a fail-closed authorization error.</summary>
    private static EnvironmentRuntimeBootstrapProfileReplacement Deserialize(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<EnvironmentRuntimeBootstrapProfileReplacement>(json, JsonOptions)
                ?? throw new JsonException("The stored Runtime bootstrap profile replacement is empty.")).Validate();
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            throw new RuntimeAuthorizationException("runtime_bootstrap_profile_replacement_corrupt");
        }
    }

    /// <summary>Adds all four Environment owner keys to a parameterized database command.</summary>
    private static void AddOwner(NpgsqlCommand command, EnvironmentOwnerIdentity owner)
    {
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, owner.TenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Text, owner.ProjectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Text, owner.RunId);
        command.Parameters.AddWithValue("environment_id", NpgsqlDbType.Text, owner.EnvironmentId);
    }

    /// <summary>Compares two serialized representations as JSON values rather than by formatting.</summary>
    private static bool SameJson(string left, string right) =>
        JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(left), JsonSerializer.Deserialize<JsonElement>(right));
}
