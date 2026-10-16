using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

/// <summary>Stores durable receipts for a runtime resume bootstrap operation.</summary>
public interface IEnvironmentRuntimeResumeBootstrapStore
{
    /// <summary>Reserves or reuses an operation, reconciles its receipt, and saves it under current placement authority.</summary>
    Task<RuntimeResumeBootstrapReceipt> ExecuteAsync(
        RuntimeResumeBootstrapRequest request,
        Func<CancellationToken, Task> requireCurrent,
        Func<RuntimeResumeBootstrapReceipt?, CancellationToken, Task<RuntimeResumeBootstrapReceipt>> reconcile,
        CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates idempotent resume-bootstrap work with the current owner, lifecycle, lease, and runtime placement.
/// </summary>
public sealed class EnvironmentRuntimeResumeBootstrapStore(
    NpgsqlDataSource dataSource, TimeProvider timeProvider) : IEnvironmentRuntimeResumeBootstrapStore
{
    private const string Effects = "\"environment\".\"owner_effects\"";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ResourceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Serializes work for an operation ID, reconciles the host receipt, and persists a stable result.
    /// Current authority and placement are checked before and after the external reconciliation.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">The operation, placement, request, or saved receipt is invalid or stale.</exception>
    public async Task<RuntimeResumeBootstrapReceipt> ExecuteAsync(
        RuntimeResumeBootstrapRequest request,
        Func<CancellationToken, Task> requireCurrent,
        Func<RuntimeResumeBootstrapReceipt?, CancellationToken, Task<RuntimeResumeBootstrapReceipt>> reconcile,
        CancellationToken cancellationToken)
    {
        request.Validate();
        ArgumentNullException.ThrowIfNull(requireCurrent);
        ArgumentNullException.ThrowIfNull(reconcile);
        var json = JsonSerializer.Serialize(request, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > SessionMaterialValidation.MaximumBytes)
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_request_too_large");
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var lockName = $"environment:runtime-resume-bootstrap:{request.Resume.OperationId:N}";
        await using (var acquire = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtextextended(@lock_name, 0))", connection))
        {
            acquire.Parameters.AddWithValue("lock_name", NpgsqlDbType.Text, lockName);
            await acquire.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await requireCurrent(cancellationToken).ConfigureAwait(false);
            var saved = await ReadOrReserveAsync(
                connection, request, json, fingerprint, cancellationToken).ConfigureAwait(false);
            var receipt = (await reconcile(saved, cancellationToken).ConfigureAwait(false)).ValidateFor(request);
            if (saved is not null && !SameJson(saved, receipt))
                throw new RuntimeAuthorizationException("runtime_resume_bootstrap_receipt_changed");
            await requireCurrent(cancellationToken).ConfigureAwait(false);
            await SaveAsync(connection, request, json, fingerprint, receipt, cancellationToken).ConfigureAwait(false);
            await requireCurrent(cancellationToken).ConfigureAwait(false);
            return receipt;
        }
        finally
        {
            await using var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@lock_name, 0))", connection);
            release.Parameters.AddWithValue("lock_name", NpgsqlDbType.Text, lockName);
            if (await release.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("The runtime resume bootstrap lock was not held.");
        }
    }

    /// <summary>Locks the current placement, reuses a matching reservation, or inserts a new operation reservation.</summary>
    private async Task<RuntimeResumeBootstrapReceipt?> ReadOrReserveAsync(
        NpgsqlConnection connection, RuntimeResumeBootstrapRequest request, string json, string fingerprint,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequirePlacementAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
        var existing = await ReadAsync(connection, transaction, request, json, fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (existing.Exists)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing.Receipt;
        }
        await using (var stale = new NpgsqlCommand($"""
            UPDATE {Effects}
            SET effect_state = 'Stale'
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND effect_kind = 'RuntimeResumeBootstrap' AND effect_state = 'Reserved'
              AND specification_json -> 'resume' ->> 'operationId' = @resume_operation
              AND specification_json -> 'resume' -> 'sourceManifest' ->> 'manifestId' = @manifest_id
              AND operation_id <> @operation_id
            """, connection, transaction))
        {
            AddOwner(stale, request);
            stale.Parameters.AddWithValue("resume_operation", NpgsqlDbType.Text, request.Resume.OperationId.ToString());
            stale.Parameters.AddWithValue("manifest_id", NpgsqlDbType.Text, request.Resume.SourceManifest.ManifestId.ToString());
            stale.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, request.BootstrapOperationId);
            await stale.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var binding = request.Registration.Binding;
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO {Effects}
                (operation_id, tenant_id, project_id, run_id, environment_id, lifecycle_generation,
                 effect_kind, resource_id, expected_provider_seam, expected_provider_id,
                 expected_provider_resource_id, operation, specification_json,
                 expected_provider_binding_json, idempotency_key, request_fingerprint, effect_state, created_at)
            VALUES
                (@operation_id, @tenant_id, @project_id, @run_id, @environment_id, @lifecycle_generation,
                 'RuntimeResumeBootstrap', @resource_id, 'Sandbox', @provider_id,
                 @resource_id, 'Activate', @request_json, @registration_json,
                 @idempotency_key, @fingerprint, 'Reserved', @created_at)
            """, connection, transaction);
        AddOwner(insert, request);
        insert.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, request.BootstrapOperationId);
        insert.Parameters.AddWithValue("lifecycle_generation", NpgsqlDbType.Bigint, binding.EnvironmentLifecycleGeneration);
        insert.Parameters.AddWithValue("resource_id", NpgsqlDbType.Text, binding.PlacementUid);
        insert.Parameters.AddWithValue("provider_id", NpgsqlDbType.Text, binding.PlacementProviderId!);
        insert.Parameters.AddWithValue("request_json", NpgsqlDbType.Jsonb, json);
        insert.Parameters.AddWithValue("registration_json", NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(request.Registration, JsonOptions));
        insert.Parameters.AddWithValue("idempotency_key", NpgsqlDbType.Text,
            $"runtime-resume-bootstrap:{request.BootstrapOperationId:N}");
        insert.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Text, fingerprint);
        insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_operation_conflict");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Stores a completed receipt only when its reservation and request fingerprint still match.</summary>
    private async Task SaveAsync(
        NpgsqlConnection connection, RuntimeResumeBootstrapRequest request, string json, string fingerprint,
        RuntimeResumeBootstrapReceipt receipt, CancellationToken cancellationToken)
    {
        var receiptJson = JsonSerializer.Serialize(receipt, JsonOptions);
        if (Encoding.UTF8.GetByteCount(receiptJson) > SessionMaterialValidation.MaximumBytes)
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_receipt_too_large");
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RequirePlacementAsync(connection, transaction, request, cancellationToken).ConfigureAwait(false);
        var existing = await ReadAsync(connection, transaction, request, json, fingerprint, cancellationToken)
            .ConfigureAwait(false);
        if (!existing.Exists)
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_reservation_missing");
        if (existing.Receipt is not null)
        {
            if (!SameJson(existing.Receipt, receipt))
                throw new RuntimeAuthorizationException("runtime_resume_bootstrap_receipt_changed");
        }
        else
        {
            await using var update = new NpgsqlCommand($"""
                UPDATE {Effects}
                SET target_provider_binding_json = @receipt_json, effect_state = 'Completed', completed_at = @completed_at
                WHERE operation_id = @operation_id AND effect_kind = 'RuntimeResumeBootstrap'
                  AND effect_state = 'Reserved' AND request_fingerprint = @fingerprint
                """, connection, transaction);
            update.Parameters.AddWithValue("receipt_json", NpgsqlDbType.Jsonb, receiptJson);
            update.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
            update.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, request.BootstrapOperationId);
            update.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Text, fingerprint);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new RuntimeAuthorizationException("runtime_resume_bootstrap_reservation_changed");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads and validates the durable operation row while holding its transaction lock.</summary>
    private static async Task<(bool Exists, RuntimeResumeBootstrapReceipt? Receipt)> ReadAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, RuntimeResumeBootstrapRequest request,
        string json, string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT tenant_id, project_id, run_id, environment_id, lifecycle_generation, effect_kind,
                   request_fingerprint, specification_json::text, effect_state, target_provider_binding_json::text
            FROM {Effects} WHERE operation_id = @operation_id FOR UPDATE
            """, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, request.BootstrapOperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return (false, null);
        var binding = request.Registration.Binding;
        if (reader.GetString(0) != binding.TenantId || reader.GetString(1) != binding.ProjectId ||
            reader.GetString(2) != binding.RunId || reader.GetString(3) != binding.EnvironmentId ||
            reader.GetInt64(4) != binding.EnvironmentLifecycleGeneration ||
            reader.GetString(5) != "RuntimeResumeBootstrap" || reader.GetString(6) != fingerprint ||
            reader.IsDBNull(7) || !JsonElement.DeepEquals(
                JsonSerializer.Deserialize<JsonElement>(reader.GetString(7)), JsonSerializer.Deserialize<JsonElement>(json)))
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_operation_conflict");
        var state = reader.GetString(8);
        if (state is not ("Reserved" or "Completed") ||
            (state == "Completed") == reader.IsDBNull(9))
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_state_invalid");
        if (state == "Reserved")
            return (true, null);
        try
        {
            var receipt = JsonSerializer.Deserialize<RuntimeResumeBootstrapReceipt>(reader.GetString(9), JsonOptions)
                ?? throw new JsonException("The saved runtime bootstrap receipt is empty.");
            return (true, receipt.ValidateFor(request));
        }
        catch (JsonException)
        {
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_receipt_corrupt");
        }
    }

    /// <summary>Locks the owner and confirms every current lease generation, fence, revision, and expiry value.</summary>
    private async Task RequirePlacementAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        RuntimeResumeBootstrapRequest request, CancellationToken cancellationToken)
    {
        var binding = request.Registration.Binding;
        var owner = request.Resume.SourceManifest.EnvironmentFence!.Owner;
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(connection, transaction, owner, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT lease.resource_json::text
            FROM "environment"."owners" AS owner
            JOIN "environment"."sandbox_leases" AS lease
              ON lease.tenant_id = owner.tenant_id AND lease.project_id = owner.project_id
             AND lease.run_id = owner.run_id AND lease.environment_id = owner.environment_id
            WHERE owner.tenant_id = @tenant_id AND owner.project_id = @project_id
              AND owner.run_id = @run_id AND owner.environment_id = @environment_id
              AND owner.state = 'Active' AND owner.lifecycle_generation = @lifecycle_generation
              AND lease.lifecycle_generation = owner.lifecycle_generation
              AND lease.resource_generation = @resource_generation
              AND lease.current_fencing_generation = @current_fence
              AND lease.provider_fencing_generation = @provider_fence
              AND lease.lease_revision = @lease_revision AND lease.lease_state = 'Active' AND lease.is_current
              AND lease.lease_expires_at = @expires_at AND lease.lease_expires_at > @now
            FOR UPDATE OF owner, lease
            """, connection, transaction);
        AddOwner(command, request);
        command.Parameters.AddWithValue("lifecycle_generation", NpgsqlDbType.Bigint, binding.EnvironmentLifecycleGeneration);
        command.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, binding.PlacementGeneration);
        command.Parameters.AddWithValue("current_fence", NpgsqlDbType.Bigint, binding.EnvironmentCurrentFencingGeneration);
        command.Parameters.AddWithValue("provider_fence", NpgsqlDbType.Bigint, binding.EnvironmentProviderFencingGeneration);
        command.Parameters.AddWithValue("lease_revision", NpgsqlDbType.Bigint, binding.EnvironmentLeaseRevision);
        command.Parameters.AddWithValue("expires_at", NpgsqlDbType.TimestampTz, request.Registration.ExpiresAt);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, timeProvider.GetUtcNow());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_placement_changed");
        SandboxProvisionedResource resource;
        try
        {
            resource = (JsonSerializer.Deserialize<SandboxProvisionedResource>(reader.GetString(0), ResourceJsonOptions)
                ?? throw new JsonException("The current runtime placement has no resource.")).Validate();
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_placement_corrupt");
        }
        if (resource.Resource != new ProviderResourceRef(ProviderSeam.Sandbox,
            binding.PlacementProviderId!, binding.PlacementUid, binding.PlacementGeneration))
            throw new RuntimeAuthorizationException("runtime_resume_bootstrap_placement_changed");
    }

    /// <summary>Adds the owner keys from the accepted runtime registration to a database command.</summary>
    private static void AddOwner(NpgsqlCommand command, RuntimeResumeBootstrapRequest request)
    {
        var binding = request.Registration.Binding;
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, binding.TenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Text, binding.ProjectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Text, binding.RunId);
        command.Parameters.AddWithValue("environment_id", NpgsqlDbType.Text, binding.EnvironmentId);
    }

    /// <summary>Compares receipt values by their JSON representation.</summary>
    private static bool SameJson<T>(T left, T right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left, JsonOptions),
            JsonSerializer.SerializeToElement(right, JsonOptions));
}
