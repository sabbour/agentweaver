using System.Collections.Immutable;
using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentweaver.Projects.Config;

internal enum EnvironmentHostRequestOutcome
{
    Delivered,
    PermanentFailure,
    TransientFailure,
}

internal sealed record EnvironmentHostRequestSendResult(
    EnvironmentHostRequestOutcome Outcome,
    ProviderHostRequestReceipt? Receipt = null,
    string? FailureCode = null);

internal interface IEnvironmentHostRequestClient
{
    Task<EnvironmentHostRequestSendResult> SendHostRequestAsync(
        ProviderHostRequestDispatch dispatch,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, ProviderHostRequestStatusItem>?> GetStatusesAsync(
        IReadOnlyCollection<Guid> requestIds,
        CancellationToken cancellationToken);
}

internal sealed class HttpEnvironmentHostRequestClient(
    IHttpClientFactory httpClientFactory,
    RuntimeServiceTlsIdentity tlsIdentity) : IEnvironmentHostRequestClient
{
    internal const string ChannelKey = "ProviderHostRequest";
    internal const string SectionName = "ProjectsConfig:ProviderHostRequest:Tls";
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<EnvironmentHostRequestSendResult> SendHostRequestAsync(
        ProviderHostRequestDispatch dispatch,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(tlsIdentity.PeerOwnerAddress, "/internal/egress/host-requests"))
            {
                Content = JsonContent.Create(dispatch, options: Json),
            };
            using var response = await httpClientFactory.CreateClient(ChannelKey)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (IsRedirect(response) || response.Headers.CacheControl?.NoStore != true)
                return new(EnvironmentHostRequestOutcome.TransientFailure, FailureCode: "environment_protocol_invalid");

            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            {
                var error = await TryReadErrorCodeAsync(response.Content, cancellationToken).ConfigureAwait(false);
                return new(
                    EnvironmentHostRequestOutcome.PermanentFailure,
                    FailureCode: error ?? $"environment_rejected_{(int)response.StatusCode}");
            }
            if (!response.IsSuccessStatusCode)
                return new(
                    EnvironmentHostRequestOutcome.TransientFailure,
                    FailureCode: $"environment_http_{(int)response.StatusCode}");

            var receipt = await response.Content.ReadFromJsonAsync<ProviderHostRequestReceipt>(Json, cancellationToken)
                .ConfigureAwait(false);
            if (receipt is null)
                return new(EnvironmentHostRequestOutcome.TransientFailure, FailureCode: "environment_receipt_invalid");
            return new(EnvironmentHostRequestOutcome.Delivered, Receipt: receipt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return new(EnvironmentHostRequestOutcome.TransientFailure, FailureCode: "environment_receipt_invalid");
        }
        catch (HttpRequestException)
        {
            return new(EnvironmentHostRequestOutcome.TransientFailure, FailureCode: "environment_transport_unavailable");
        }
    }

    public async Task<IReadOnlyDictionary<Guid, ProviderHostRequestStatusItem>?> GetStatusesAsync(
        IReadOnlyCollection<Guid> requestIds,
        CancellationToken cancellationToken)
    {
        if (requestIds.Count == 0)
            return new Dictionary<Guid, ProviderHostRequestStatusItem>();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(tlsIdentity.PeerOwnerAddress, "/internal/egress/host-requests/status"))
            {
                Content = JsonContent.Create(
                    new ProviderHostRequestStatusQuery(requestIds.ToImmutableArray()),
                    options: Json),
            };
            using var response = await httpClientFactory.CreateClient(ChannelKey)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (IsRedirect(response) ||
                !response.IsSuccessStatusCode ||
                response.Headers.CacheControl?.NoStore != true)
                return null;

            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content
                .ReadFromJsonAsync<ProviderHostRequestStatusResponse>(Json, cancellationToken)
                .ConfigureAwait(false);
            if (payload is null || payload.Statuses.IsDefault)
                return null;
            return payload.Statuses.ToDictionary(item => item.RequestId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is >= 300 and < 400;

    private static async Task<string?> TryReadErrorCodeAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            var error = await content.ReadFromJsonAsync<ServiceError>(Json, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(error?.Code) ? null : error.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ServiceError(string? Code);
}

internal sealed class ModelProviderHostRequestRelay(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ModelProviderHostRequestRelay> logger,
    TimeSpan? leaseDuration = null,
    TimeSpan? attemptDeadline = null,
    TimeSpan? completionDeadline = null) : BackgroundService
{
    private const int MaxItemsPerPass = 20;
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultAttemptDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private readonly string leaseOwner = $"projects-config-relay-{Environment.ProcessId}";
    private readonly TimeSpan effectiveLeaseDuration = leaseDuration ?? DefaultLeaseDuration;
    private readonly TimeSpan effectiveAttemptDeadline = attemptDeadline ?? DefaultAttemptDeadline;
    private readonly TimeSpan effectiveCompletionDeadline = completionDeadline ?? attemptDeadline ?? DefaultAttemptDeadline;

    internal Func<ClaimedHostRequest, CancellationToken, Task>? OnBeforeCompleteAsync { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                if (processed < MaxItemsPerPass)
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Provider host-request relay iteration failed.");
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        while (processed < MaxItemsPerPass && !cancellationToken.IsCancellationRequested)
        {
            var item = await ClaimNextAsync(cancellationToken).ConfigureAwait(false);
            if (item is null)
                break;
            processed++;

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(effectiveAttemptDeadline);
            using var completionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var leaseLost = false;
            var renewInterval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(10).Ticks, effectiveLeaseDuration.Ticks / 3));

            var renewalTask = Task.Run(async () =>
            {
                try
                {
                    while (!renewalCts.IsCancellationRequested)
                    {
                        await Task.Delay(renewInterval, timeProvider, renewalCts.Token).ConfigureAwait(false);
                        if (!await RenewLeaseAsync(item, renewalCts.Token).ConfigureAwait(false))
                        {
                            leaseLost = true;
                            await attemptCts.CancelAsync().ConfigureAwait(false);
                            await completionCts.CancelAsync().ConfigureAwait(false);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (renewalCts.IsCancellationRequested)
                {
                }
            }, CancellationToken.None);

            try
            {
                EnvironmentHostRequestSendResult result;
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var client = scope.ServiceProvider.GetRequiredService<IEnvironmentHostRequestClient>();
                    result = await client.SendHostRequestAsync(
                        new ProviderHostRequestDispatch(
                            item.RequestId,
                            item.TenantId,
                            item.Scope,
                            item.ProjectId,
                            item.ProviderId,
                            item.ProviderRevision,
                            item.ConfigurationHash,
                            item.OperationId,
                            item.ActorId,
                            item.Host,
                            item.Port),
                        attemptCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    result = new(EnvironmentHostRequestOutcome.TransientFailure, FailureCode: "environment_attempt_timeout");
                }

                if (leaseLost)
                {
                    logger.LogWarning("Provider host-request lease lost before completion for {RequestId}.", item.RequestId);
                    continue;
                }

                // Keep lease renewal active through bounded completion so a slow DB write does not lose the lease.
                completionCts.CancelAfter(effectiveCompletionDeadline);
                try
                {
                    if (OnBeforeCompleteAsync is not null)
                        await OnBeforeCompleteAsync(item, completionCts.Token).ConfigureAwait(false);

                    bool acknowledged;
                    if (result.Outcome == EnvironmentHostRequestOutcome.Delivered)
                    {
                        if (result.Receipt is { } receipt && ReceiptMatches(item, receipt))
                            acknowledged = await MarkDeliveredAsync(item, receipt.DestinationId, completionCts.Token).ConfigureAwait(false);
                        else
                            acknowledged = await MarkFailedAsync(item, "environment_receipt_mismatch", completionCts.Token).ConfigureAwait(false);
                    }
                    else if (result.Outcome == EnvironmentHostRequestOutcome.PermanentFailure)
                    {
                        acknowledged = await MarkFailedAsync(
                            item,
                            TruncateFailureCode(result.FailureCode ?? "environment_rejected"),
                            completionCts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        acknowledged = await ScheduleRetryAsync(
                            item,
                            TruncateFailureCode(result.FailureCode ?? "environment_unavailable"),
                            completionCts.Token).ConfigureAwait(false);
                    }

                    if (!acknowledged)
                        logger.LogWarning("Provider host-request stale acknowledgement ignored for {RequestId}.", item.RequestId);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        leaseLost
                            ? "Provider host-request lease lost during completion for {RequestId}."
                            : "Provider host-request completion timed out for {RequestId}.",
                        item.RequestId);
                }
            }
            finally
            {
                await renewalCts.CancelAsync().ConfigureAwait(false);
                await renewalTask.ConfigureAwait(false);
            }
        }

        return processed;
    }

    internal async Task<ClaimedHostRequest?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var leaseToken = Guid.NewGuid();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            WITH claimed AS (
                SELECT request_id
                FROM projects_config.model_provider_host_requests
                WHERE delivery_state = 'Pending'
                  AND next_attempt_at <= clock_timestamp()
                  AND (lease_expires_at IS NULL OR lease_expires_at <= clock_timestamp())
                ORDER BY next_attempt_at, created_at, request_id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE projects_config.model_provider_host_requests AS r
            SET lease_owner = @leaseOwner,
                lease_token = @leaseToken,
                lease_expires_at = clock_timestamp() + @leaseDuration,
                last_attempt_at = clock_timestamp(),
                attempt_count = r.attempt_count + 1,
                updated_at = clock_timestamp()
            FROM claimed
            WHERE r.request_id = claimed.request_id
            RETURNING r.request_id, r.tenant_id, r.scope, r.project_id, r.provider_id,
                      r.provider_revision, r.configuration_hash, r.operation_id, r.actor_id,
                      r.host, r.port, r.attempt_count
            """,
            connection);
        command.Parameters.AddWithValue("leaseOwner", leaseOwner);
        command.Parameters.AddWithValue("leaseToken", leaseToken);
        command.Parameters.AddWithValue("leaseDuration", effectiveLeaseDuration);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new ClaimedHostRequest(
            reader.GetGuid(0),
            reader.GetString(1),
            Enum.Parse<ModelProviderBindingScope>(reader.GetString(2), ignoreCase: false),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetGuid(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt32(10),
            reader.GetInt32(11),
            leaseToken);
    }

    private async Task<bool> RenewLeaseAsync(
        ClaimedHostRequest item,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE projects_config.model_provider_host_requests
            SET lease_expires_at = clock_timestamp() + @leaseDuration,
                updated_at = clock_timestamp()
            WHERE request_id = @requestId
              AND delivery_state = 'Pending'
              AND lease_token = @leaseToken
              AND lease_expires_at > clock_timestamp()
            """,
            connection);
        command.Parameters.AddWithValue("leaseDuration", effectiveLeaseDuration);
        command.Parameters.AddWithValue("requestId", item.RequestId);
        command.Parameters.AddWithValue("leaseToken", item.LeaseToken);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    internal async Task<bool> MarkDeliveredAsync(
        ClaimedHostRequest item,
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE projects_config.model_provider_host_requests
            SET delivery_state = 'Delivered',
                environment_destination_id = @destinationId,
                delivered_at = clock_timestamp(),
                failure_code = NULL,
                lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE request_id = @requestId
              AND delivery_state = 'Pending'
              AND lease_token = @leaseToken
              AND lease_expires_at > clock_timestamp()
            """,
            connection);
        command.Parameters.AddWithValue("destinationId", destinationId);
        command.Parameters.AddWithValue("requestId", item.RequestId);
        command.Parameters.AddWithValue("leaseToken", item.LeaseToken);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    internal async Task<bool> MarkFailedAsync(
        ClaimedHostRequest item,
        string failureCode,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE projects_config.model_provider_host_requests
            SET delivery_state = 'Failed',
                failure_code = @failureCode,
                lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE request_id = @requestId
              AND delivery_state = 'Pending'
              AND lease_token = @leaseToken
              AND lease_expires_at > clock_timestamp()
            """,
            connection);
        command.Parameters.AddWithValue("failureCode", failureCode);
        command.Parameters.AddWithValue("requestId", item.RequestId);
        command.Parameters.AddWithValue("leaseToken", item.LeaseToken);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    internal async Task<bool> ScheduleRetryAsync(
        ClaimedHostRequest item,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var exponent = Math.Clamp(item.AttemptCount - 1, 0, 7);
        var delaySeconds = Math.Min(300, 2 * (1 << exponent));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            UPDATE projects_config.model_provider_host_requests
            SET failure_code = @failureCode,
                next_attempt_at = clock_timestamp() + @retryDelay,
                lease_owner = NULL,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = clock_timestamp()
            WHERE request_id = @requestId
              AND delivery_state = 'Pending'
              AND lease_token = @leaseToken
              AND lease_expires_at > clock_timestamp()
            """,
            connection);
        command.Parameters.AddWithValue("failureCode", failureCode);
        command.Parameters.AddWithValue("retryDelay", TimeSpan.FromSeconds(delaySeconds));
        command.Parameters.AddWithValue("requestId", item.RequestId);
        command.Parameters.AddWithValue("leaseToken", item.LeaseToken);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    internal static bool ReceiptMatches(ClaimedHostRequest item, ProviderHostRequestReceipt receipt) =>
        receipt.DestinationId != Guid.Empty &&
        receipt.RequestId == item.RequestId &&
        string.Equals(receipt.TenantId, item.TenantId, StringComparison.Ordinal) &&
        receipt.Scope == item.Scope &&
        string.Equals(receipt.ProjectId, item.ProjectId, StringComparison.Ordinal) &&
        string.Equals(receipt.ProviderId, item.ProviderId, StringComparison.Ordinal) &&
        receipt.ProviderRevision == item.ProviderRevision &&
        string.Equals(receipt.ConfigurationHash, item.ConfigurationHash, StringComparison.Ordinal) &&
        receipt.OperationId == item.OperationId &&
        string.Equals(receipt.ActorId, item.ActorId, StringComparison.Ordinal) &&
        string.Equals(receipt.Host, item.Host, StringComparison.Ordinal) &&
        receipt.Port == item.Port;

    private static string TruncateFailureCode(string code) =>
        code.Length <= 64 ? code : code[..64];

    internal sealed record ClaimedHostRequest(
        Guid RequestId,
        string TenantId,
        ModelProviderBindingScope Scope,
        string? ProjectId,
        string ProviderId,
        long ProviderRevision,
        string ConfigurationHash,
        Guid OperationId,
        string ActorId,
        string Host,
        int Port,
        int AttemptCount,
        Guid LeaseToken);
}

internal static class ProviderHostRequestProofEndpoints
{
    private const int MaximumBodyBytes = 64 * 1024;
    private const int MaximumItems = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void MapProviderHostRequestProof(this IEndpointRouteBuilder app, bool enabled)
    {
        if (!enabled)
            return;

        app.MapPost("/internal/egress/host-requests/proof", async (
            HttpContext context,
            ProjectsConfigDbContext db,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var identity = context.RequestServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(
                    HttpEnvironmentHostRequestClient.ChannelKey);
                identity.RequirePeerConnection(
                    context.Request.IsHttps,
                    context.Features.Get<ITlsHandshakeFeature>()?.Protocol,
                    context.Connection.LocalPort,
                    context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate);
            }
            catch (RuntimeAuthorizationException exception)
            {
                return Results.Json(new { code = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
                sizeFeature.MaxRequestBodySize = MaximumBodyBytes;

            ProviderHostRequestProofQuery? query;
            try
            {
                query = await JsonSerializer.DeserializeAsync<ProviderHostRequestProofQuery>(
                    context.Request.Body, Json, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { code = "invalid_host_request_proof_query" });
            }

            if (query is null || query.Items.IsDefault || query.Items.Length > MaximumItems)
                return Results.BadRequest(new { code = "invalid_host_request_proof_query" });

            if (query.Items.Length == 0)
                return Results.Ok(new ProviderHostRequestProofResponse([]));

            var requestIds = query.Items.Select(item => item.RequestId).Distinct().ToArray();
            var rows = await (
                from request in db.ModelProviderHostRequests.AsNoTracking()
                join head in db.ModelProviderDrafts.AsNoTracking()
                    on request.ProviderId equals head.ProviderId
                join project in db.Projects.AsNoTracking()
                    on request.ProjectId equals project.ProjectId into projectGroup
                from project in projectGroup.DefaultIfEmpty()
                where requestIds.Contains(request.RequestId)
                select new
                {
                    request.RequestId,
                    request.TenantId,
                    request.Scope,
                    request.ProjectId,
                    request.ProviderId,
                    request.ProviderRevision,
                    request.Host,
                    request.Port,
                    request.DeliveryState,
                    HeadScope = head.Scope,
                    HeadProjectId = head.ProjectId,
                    HeadRevision = head.CurrentRevision,
                    ProjectState = project == null ? (ProjectLifecycleState?)null : project.State,
                    ProjectTenantId = project == null ? null : project.TenantId,
                })
                .ToDictionaryAsync(item => item.RequestId, cancellationToken)
                .ConfigureAwait(false);

            var results = query.Items.Select(item =>
            {
                if (!rows.TryGetValue(item.RequestId, out var row))
                    return new ProviderHostRequestProofResult(item.RequestId, false);
                var isCurrent =
                    row.DeliveryState == ModelProviderHostRequestDeliveryState.Delivered &&
                    string.Equals(row.TenantId, item.TenantId, StringComparison.Ordinal) &&
                    row.Scope == item.Scope &&
                    string.Equals(row.ProjectId, item.ProjectId, StringComparison.Ordinal) &&
                    string.Equals(row.ProviderId, item.ProviderId, StringComparison.Ordinal) &&
                    row.ProviderRevision == item.ProviderRevision &&
                    string.Equals(row.Host, item.Host, StringComparison.Ordinal) &&
                    row.Port == item.Port &&
                    row.HeadScope == item.Scope &&
                    string.Equals(row.HeadProjectId, item.ProjectId, StringComparison.Ordinal) &&
                    row.HeadRevision == item.ProviderRevision &&
                    (item.Scope == ModelProviderBindingScope.Platform ||
                     (row.ProjectState == ProjectLifecycleState.Active &&
                      string.Equals(row.ProjectTenantId, item.TenantId, StringComparison.Ordinal)));
                return new ProviderHostRequestProofResult(item.RequestId, isCurrent);
            }).ToImmutableArray();

            return Results.Ok(new ProviderHostRequestProofResponse(results));
        }).ExcludeFromDescription();
    }
}
