using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Environment;

public interface IEnvironmentProviderLifecycleReportStore
{
    /// <summary>
    /// Reserves the provider report against its exact active lease. The provider identity must
    /// come from an admitted authenticator, never from the report body. No current provider
    /// authenticator is wired to production routes yet.
    /// </summary>
    Task<EnvironmentProviderLifecycleReportSnapshot> ReserveAsync(
        EnvironmentProviderLifecycleReportRequest request,
        string authenticatedProviderId,
        CancellationToken cancellationToken);
}

public sealed class EnvironmentProviderLifecycleReportStore(
    NpgsqlDataSource dataSource,
    TimeProvider timeProvider) : IEnvironmentProviderLifecycleReportStore
{
    private const string Owners = "\"environment\".\"owners\"";
    private const string Leases = "\"environment\".\"sandbox_leases\"";
    private const string Reports = "\"environment\".\"provider_lifecycle_reports\"";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<EnvironmentProviderLifecycleReportSnapshot> ReserveAsync(
        EnvironmentProviderLifecycleReportRequest request,
        string authenticatedProviderId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ValidateProviderIdentity(authenticatedProviderId);
        if (!string.Equals(
                request.Resource.ProviderId,
                authenticatedProviderId,
                StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "environment_provider_identity_mismatch",
                "The authenticated provider does not match the report's provider identity.");

        var reportedAt = NormalizeTimestamp(request.ReportedAt);
        var fingerprint = RequestFingerprint(request, authenticatedProviderId, reportedAt);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnvironmentLifecycleStore.AcquireOwnerLockAsync(
            connection,
            transaction,
            request.Fence.Owner,
            cancellationToken).ConfigureAwait(false);

        await RequireActiveOwnerAsync(connection, transaction, request.Fence, cancellationToken)
            .ConfigureAwait(false);
        var lease = await ReadCurrentLeaseAsync(
            connection,
            transaction,
            request,
            authenticatedProviderId,
            cancellationToken).ConfigureAwait(false);

        var existing = await ReadReportAsync(
            connection,
            transaction,
            request.Fence.Owner,
            request.ProviderEventId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new EnvironmentLifecycleException(
                    "environment_provider_event_conflict",
                    "The provider event ID was already used for a different report.");
            if (existing.SandboxOperationId != lease.OperationId ||
                existing.LeaseRevision != lease.LeaseRevision ||
                existing.Resource != request.Resource ||
                existing.Fence.LifecycleGeneration != request.Fence.LifecycleGeneration)
                throw new EnvironmentLifecycleException(
                    "environment_provider_report_lease_stale",
                    "The report's exact Sandbox lease version is no longer current.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return existing with { Replayed = true };
        }

        var now = NormalizeTimestamp(timeProvider.GetUtcNow());
        var coreOperationKey = CreateCoreOperationKey(
            request.Fence.Owner,
            lease.OperationId,
            request.ProviderEventId);
        var lastErrorCode = request.ContractVersion == 1
            ? null
            : "environment_provider_report_version_unsupported";

        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO {Reports}
                (tenant_id, project_id, run_id, environment_id, provider_event_id,
                 contract_version, event_kind, reported_at, provider_id, adapter_version, resource_id,
                 lifecycle_generation, resource_generation, provider_fencing_generation,
                 current_fencing_generation, lease_revision, sandbox_operation_id,
                 request_fingerprint, core_operation_key, core_execution_fence,
                 reconciliation_state, last_error_code, created_at, updated_at)
            VALUES
                (@tenant_id, @project_id, @run_id, @environment_id, @provider_event_id,
                 @contract_version, @event_kind, @reported_at, @provider_id, @adapter_version, @resource_id,
                 @lifecycle_generation, @resource_generation, @provider_fencing_generation,
                 @current_fencing_generation, @lease_revision, @sandbox_operation_id,
                 @request_fingerprint, @core_operation_key, NULL,
                 'Pending', @last_error_code, @created_at, @updated_at)
            """, connection, transaction))
        {
            AddOwnerParameters(insert, request.Fence.Owner);
            insert.Parameters.AddWithValue("provider_event_id", NpgsqlDbType.Uuid, request.ProviderEventId);
            insert.Parameters.AddWithValue("contract_version", NpgsqlDbType.Integer, request.ContractVersion);
            insert.Parameters.AddWithValue("event_kind", NpgsqlDbType.Text, request.Kind.ToString());
            insert.Parameters.AddWithValue("reported_at", NpgsqlDbType.TimestampTz, reportedAt);
            insert.Parameters.AddWithValue("provider_id", NpgsqlDbType.Text, authenticatedProviderId);
            insert.Parameters.AddWithValue("adapter_version", NpgsqlDbType.Text, lease.AdapterVersion);
            insert.Parameters.AddWithValue("resource_id", NpgsqlDbType.Text, lease.Resource.ResourceId);
            insert.Parameters.AddWithValue(
                "lifecycle_generation",
                NpgsqlDbType.Bigint,
                request.Fence.LifecycleGeneration);
            insert.Parameters.AddWithValue("resource_generation", NpgsqlDbType.Bigint, lease.Resource.Generation);
            insert.Parameters.AddWithValue(
                "provider_fencing_generation",
                NpgsqlDbType.Bigint,
                lease.ProviderFencingGeneration);
            insert.Parameters.AddWithValue(
                "current_fencing_generation",
                NpgsqlDbType.Bigint,
                lease.CurrentFencingGeneration);
            insert.Parameters.AddWithValue("lease_revision", NpgsqlDbType.Bigint, lease.LeaseRevision);
            insert.Parameters.AddWithValue("sandbox_operation_id", NpgsqlDbType.Uuid, lease.OperationId);
            insert.Parameters.AddWithValue("request_fingerprint", NpgsqlDbType.Text, fingerprint);
            insert.Parameters.AddWithValue("core_operation_key", NpgsqlDbType.Text, coreOperationKey);
            insert.Parameters.AddWithValue(
                "last_error_code",
                NpgsqlDbType.Text,
                (object?)lastErrorCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, now);
            insert.Parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            request.Fence,
            request.ProviderEventId,
            request.ContractVersion,
            request.Kind,
            reportedAt,
            authenticatedProviderId,
            lease.AdapterVersion,
            lease.Resource,
            lease.ProviderFencingGeneration,
            lease.CurrentFencingGeneration,
            lease.LeaseRevision,
            lease.OperationId,
            fingerprint,
            coreOperationKey,
            CoreExecutionFence: null,
            EnvironmentProviderLifecycleReportState.Pending,
            lastErrorCode,
            now,
            now);
    }

    private static async Task RequireActiveOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT lifecycle_generation, state
            FROM {Owners}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
            """, connection, transaction);
        AddOwnerParameters(command, fence.Owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        var generation = reader.GetInt64(0);
        var state = reader.GetString(1);
        await reader.DisposeAsync().ConfigureAwait(false);
        if (state != EnvironmentLifecycleState.Active.ToString())
            throw new EnvironmentLifecycleException(
                "environment_released",
                "A released Environment cannot accept provider lifecycle reports.");
        if (generation != fence.LifecycleGeneration)
            throw new EnvironmentLifecycleException(
                "environment_fence_stale",
                "The Environment lifecycle fence is stale.");
    }

    private static async Task<LeaseBinding> ReadCurrentLeaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentProviderLifecycleReportRequest request,
        string authenticatedProviderId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT lifecycle_generation, provider_fencing_generation,
                   current_fencing_generation, operation_id, lease_state,
                   provider_id, adapter_version, resource_json, is_current, lease_revision
            FROM {Leases}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND resource_generation = @resource_generation
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(command, request.Fence.Owner);
        command.Parameters.AddWithValue(
            "resource_generation",
            NpgsqlDbType.Bigint,
            request.Resource.Generation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new EnvironmentLifecycleException(
                "sandbox_lease_unknown",
                "The report does not identify a persisted Sandbox lease.");

        var lifecycleGeneration = reader.GetInt64(0);
        var providerFence = reader.GetInt64(1);
        var currentFence = reader.GetInt64(2);
        var operationId = reader.GetGuid(3);
        var state = reader.GetString(4);
        var providerId = reader.GetString(5);
        var adapterVersion = reader.GetString(6);
        var resourceJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        var isCurrent = reader.GetBoolean(8);
        var leaseRevision = reader.GetInt64(9);
        await reader.DisposeAsync().ConfigureAwait(false);

        if (lifecycleGeneration != request.Fence.LifecycleGeneration ||
            providerFence != request.ProviderFencingGeneration ||
            currentFence != providerFence)
            throw new EnvironmentLifecycleException(
                "sandbox_fence_stale",
                "The report does not match the exact current Environment and provider fencing generations.");
        if (state != SandboxLeaseState.Active.ToString() || !isCurrent || resourceJson is null)
            throw new EnvironmentLifecycleException(
                "sandbox_lease_not_active",
                "Only the exact current active Sandbox lease can accept a lifecycle report.");
        if (!string.Equals(providerId, authenticatedProviderId, StringComparison.Ordinal) ||
            !string.Equals(providerId, request.Resource.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(adapterVersion, request.AdapterVersion, StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "sandbox_provider_binding_mismatch",
                "The authenticated provider does not match the exact selected Sandbox lease.");

        SandboxProvisionedResource resource;
        try
        {
            resource = (JsonSerializer.Deserialize<SandboxProvisionedResource>(resourceJson, JsonOptions)
                ?? throw new JsonException("The active Sandbox lease has no resource binding."))
                .Validate();
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new EnvironmentLifecycleException(
                "sandbox_provider_binding_invalid",
                "The active Sandbox lease resource binding is invalid.");
        }

        if (resource.Resource != request.Resource ||
            !string.Equals(resource.ProviderBinding.AdapterVersion, adapterVersion, StringComparison.Ordinal) ||
            leaseRevision < 1)
            throw new EnvironmentLifecycleException(
                "sandbox_provider_binding_mismatch",
                "The report resource does not match the exact active Sandbox lease.");

        return new(
            lifecycleGeneration,
            providerFence,
            currentFence,
            operationId,
            providerId,
            adapterVersion,
            resource.Resource,
            leaseRevision);
    }

    private static async Task<EnvironmentProviderLifecycleReportSnapshot?> ReadReportAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EnvironmentOwnerIdentity owner,
        Guid providerEventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT lifecycle_generation, resource_generation, provider_fencing_generation,
                   current_fencing_generation, lease_revision, sandbox_operation_id,
                   contract_version, event_kind, reported_at, provider_id, adapter_version, resource_id,
                   request_fingerprint, core_operation_key, core_execution_fence,
                   reconciliation_state, last_error_code, created_at, updated_at
            FROM {Reports}
            WHERE tenant_id = @tenant_id AND project_id = @project_id
              AND run_id = @run_id AND environment_id = @environment_id
              AND provider_event_id = @provider_event_id
            FOR UPDATE
            """, connection, transaction);
        AddOwnerParameters(command, owner);
        command.Parameters.AddWithValue("provider_event_id", NpgsqlDbType.Uuid, providerEventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var resourceGeneration = reader.GetInt64(1);
        var resource = new ProviderResourceRef(
            ProviderSeam.Sandbox,
            reader.GetString(9),
            reader.GetString(11),
            resourceGeneration);
        return new(
            new EnvironmentGenerationFence(owner, reader.GetInt64(0)),
            providerEventId,
            reader.GetInt32(6),
            Enum.Parse<EnvironmentProviderLifecycleReportKind>(reader.GetString(7), ignoreCase: false),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetString(9),
            reader.GetString(10),
            resource,
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetGuid(5),
            reader.GetString(12),
            reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetInt64(14),
            Enum.Parse<EnvironmentProviderLifecycleReportState>(reader.GetString(15), ignoreCase: false),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.GetFieldValue<DateTimeOffset>(17),
            reader.GetFieldValue<DateTimeOffset>(18));
    }

    private static string RequestFingerprint(
        EnvironmentProviderLifecycleReportRequest request,
        string authenticatedProviderId,
        DateTimeOffset reportedAt) =>
        Fingerprint(string.Join('\0',
            "environment-provider-lifecycle-report-v1",
            request.Fence.Owner.TenantId,
            request.Fence.Owner.ProjectId,
            request.Fence.Owner.RunId,
            request.Fence.Owner.EnvironmentId,
            request.Fence.LifecycleGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.ProviderEventId.ToString("N"),
            request.ContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Kind.ToString(),
            authenticatedProviderId,
            request.AdapterVersion,
            request.Resource.Seam.ToString(),
            request.Resource.ProviderId,
            request.Resource.ResourceId,
            request.Resource.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.ProviderFencingGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reportedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static DateTimeOffset NormalizeTimestamp(DateTimeOffset value)
    {
        var utcTicks = value.UtcTicks;
        return new DateTimeOffset(utcTicks - utcTicks % 10, TimeSpan.Zero);
    }

    private static string CreateCoreOperationKey(
        EnvironmentOwnerIdentity owner,
        Guid sandboxOperationId,
        Guid providerEventId) =>
        "environment-provider-lifecycle:" + Fingerprint(string.Join('\0',
            "environment-provider-lifecycle-operation-v1",
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            owner.EnvironmentId,
            sandboxOperationId.ToString("N"),
            providerEventId.ToString("N")));

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void ValidateProviderIdentity(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (providerId.Length > 128 || providerId.Any(char.IsControl))
            throw new ArgumentException(
                "The authenticated provider identity must be bounded and contain no control characters.",
                nameof(providerId));
    }

    private static void AddOwnerParameters(NpgsqlCommand command, EnvironmentOwnerIdentity owner)
    {
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, owner.TenantId);
        command.Parameters.AddWithValue("project_id", NpgsqlDbType.Text, owner.ProjectId);
        command.Parameters.AddWithValue("run_id", NpgsqlDbType.Text, owner.RunId);
        command.Parameters.AddWithValue("environment_id", NpgsqlDbType.Text, owner.EnvironmentId);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record LeaseBinding(
        long LifecycleGeneration,
        long ProviderFencingGeneration,
        long CurrentFencingGeneration,
        Guid OperationId,
        string ProviderId,
        string AdapterVersion,
        ProviderResourceRef Resource,
        long LeaseRevision);
}
