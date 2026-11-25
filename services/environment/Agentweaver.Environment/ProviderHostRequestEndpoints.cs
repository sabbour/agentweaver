using System.Buffers.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Environment;

internal interface IProjectsHostRequestProofClient
{
    Task<IReadOnlyDictionary<Guid, bool>?> VerifyCurrentAsync(
        IReadOnlyList<ProviderHostRequestProofItem> items,
        CancellationToken cancellationToken);
}

internal sealed class HttpProjectsHostRequestProofClient(
    IHttpClientFactory httpClientFactory,
    RuntimeServiceTlsIdentity tlsIdentity) : IProjectsHostRequestProofClient
{
    internal const string ChannelKey = "ProviderHostRequest";
    internal const string SectionName = "Environment:ProviderHostRequest:Tls";
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<IReadOnlyDictionary<Guid, bool>?> VerifyCurrentAsync(
        IReadOnlyList<ProviderHostRequestProofItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return new Dictionary<Guid, bool>();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(tlsIdentity.PeerOwnerAddress, "/internal/egress/host-requests/proof"))
            {
                Content = JsonContent.Create(
                    new ProviderHostRequestProofQuery(items.ToImmutableArray()),
                    options: Json),
            };
            using var response = await httpClientFactory.CreateClient(ChannelKey)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 ||
                !response.IsSuccessStatusCode ||
                response.Headers.CacheControl?.NoStore != true)
                return null;

            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content
                .ReadFromJsonAsync<ProviderHostRequestProofResponse>(Json, cancellationToken)
                .ConfigureAwait(false);
            if (payload is null || payload.Results.IsDefault)
                return null;
            return payload.Results.ToDictionary(item => item.RequestId, item => item.IsCurrent);
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
}

internal sealed class UnavailableProjectsHostRequestProofClient : IProjectsHostRequestProofClient
{
    public Task<IReadOnlyDictionary<Guid, bool>?> VerifyCurrentAsync(
        IReadOnlyList<ProviderHostRequestProofItem> items,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, bool>?>(null);
}

internal sealed class ProviderHostRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class ProviderHostRequestConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class ProviderHostRequestProofUnavailableException()
    : Exception("Projects current-scope proof is unavailable.");

internal sealed record ProviderHostRequestReceiveResult(
    ProviderHostRequestReceipt Receipt,
    bool Created);

internal sealed class ProviderHostRequestStore(
    EnvironmentDbContext db,
    TimeProvider timeProvider,
    IProjectsHostRequestProofClient proofClient)
{
    private const int MaxBatchSize = 100;

    public async Task<ProviderHostRequestReceiveResult> ReceiveAsync(
        ProviderHostRequestDispatch dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        ValidateDispatch(dispatch);

        var fingerprint = ComputeFingerprint(dispatch);
        if (await ReadReceiptByIdAsync(dispatch.RequestId, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new ProviderHostRequestConflictException(
                    "host_request_conflict",
                    "The host request ID was already used with different data.");
            return new ProviderHostRequestReceiveResult(existing.Receipt, Created: false);
        }

        var now = timeProvider.GetUtcNow();
        var receiptRow = new ProviderHostRequestReceiptRow
        {
            RequestId = dispatch.RequestId,
            DestinationId = Guid.NewGuid(),
            TenantId = dispatch.TenantId,
            Scope = dispatch.Scope,
            ProjectId = dispatch.ProjectId,
            ProviderId = dispatch.ProviderId,
            ProviderRevision = dispatch.ProviderRevision,
            ConfigurationHash = dispatch.ConfigurationHash,
            OperationId = dispatch.OperationId,
            ActorId = dispatch.ActorId,
            Host = dispatch.Host,
            Port = dispatch.Port,
            RequestFingerprint = fingerprint,
            CreatedAt = now,
        };
        db.ProviderHostRequestReceipts.Add(receiptRow);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ProviderHostRequestReceiveResult(
                new ProviderHostRequestReceipt(
                    receiptRow.RequestId,
                    receiptRow.DestinationId,
                    receiptRow.TenantId,
                    receiptRow.Scope,
                    receiptRow.ProjectId,
                    receiptRow.ProviderId,
                    receiptRow.ProviderRevision,
                    receiptRow.ConfigurationHash,
                    receiptRow.OperationId,
                    receiptRow.ActorId,
                    receiptRow.Host,
                    receiptRow.Port,
                    PlatformEgressDestinationState.Requested),
                Created: true);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            db.ChangeTracker.Clear();

            if (await ReadReceiptByIdAsync(dispatch.RequestId, cancellationToken).ConfigureAwait(false) is { } concurrent)
            {
                if (!string.Equals(concurrent.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new ProviderHostRequestConflictException(
                        "host_request_conflict",
                        "The host request ID was already used with different data.");
                return new ProviderHostRequestReceiveResult(concurrent.Receipt, Created: false);
            }

            throw new ProviderHostRequestConflictException(
                "host_request_conflict",
                "The host request conflicted with a concurrent receipt insert.");
        }
    }

    public async Task<ProviderHostRequestStatusResponse> GetStatusesAsync(
        IReadOnlyCollection<Guid> requestIds,
        CancellationToken cancellationToken)
    {
        if (requestIds.Count == 0)
            return new ProviderHostRequestStatusResponse([]);
        if (requestIds.Count > MaxBatchSize)
            throw new ProviderHostRequestValidationException(
                "invalid_status_query",
                "Too many request IDs were supplied.");

        var distinctIds = requestIds.Distinct().ToArray();
        var items = await db.ProviderHostRequestReceipts.AsNoTracking()
            .Where(receipt => distinctIds.Contains(receipt.RequestId))
            .Select(receipt => new ProviderHostRequestStatusItem(
                receipt.RequestId,
                receipt.DestinationId,
                receipt.Host,
                receipt.Port,
                PlatformEgressDestinationState.Requested))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ProviderHostRequestStatusResponse(items.ToImmutableArray());
    }

    public async Task<PlatformEgressDestinationPage> ListRequestedDestinationsAsync(
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > MaxBatchSize)
            throw new ProviderHostRequestValidationException("invalid_pagination", "Page size must be between 1 and 100.");

        DateTimeOffset? cursorCreatedAt = null;
        Guid? cursorRequestId = null;
        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, out var parsedTime, out var parsedId))
                throw new ProviderHostRequestValidationException("invalid_pagination", "The cursor is invalid.");
            cursorCreatedAt = parsedTime;
            cursorRequestId = parsedId;
        }

        var query = db.ProviderHostRequestReceipts.AsNoTracking();

        if (cursorCreatedAt.HasValue && cursorRequestId.HasValue)
        {
            var time = cursorCreatedAt.Value;
            var id = cursorRequestId.Value;
            query = query.Where(receipt =>
                receipt.CreatedAt > time ||
                (receipt.CreatedAt == time && receipt.RequestId.CompareTo(id) > 0));
        }

        var rows = await query
            .OrderBy(receipt => receipt.CreatedAt)
            .ThenBy(receipt => receipt.RequestId)
            .Take(pageSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Length > pageSize;
        var examined = rows.Take(pageSize).ToArray();
        if (examined.Length == 0)
            return new PlatformEgressDestinationPage([], null);

        var proofItems = examined
            .Select(receipt => new ProviderHostRequestProofItem(
                receipt.RequestId,
                receipt.TenantId,
                receipt.Scope,
                receipt.ProjectId,
                receipt.ProviderId,
                receipt.ProviderRevision,
                receipt.Host,
                receipt.Port))
            .ToArray();

        var proof = await proofClient.VerifyCurrentAsync(proofItems, cancellationToken).ConfigureAwait(false);
        if (proof is null)
            throw new ProviderHostRequestProofUnavailableException();

        var current = examined
            .Where(receipt => proof.TryGetValue(receipt.RequestId, out var isCurrent) && isCurrent)
            .Select(receipt => new PlatformEgressDestinationSummary(
                receipt.DestinationId,
                receipt.RequestId,
                receipt.Host,
                receipt.Port,
                PlatformEgressDestinationState.Requested,
                PlatformEgressDestinationOrigin.ModelProvider,
                receipt.Scope,
                receipt.ProjectId,
                receipt.TenantId,
                receipt.ProviderId,
                receipt.ProviderRevision,
                receipt.ActorId,
                receipt.CreatedAt))
            .ToArray();

        var nextCursor = hasMore
            ? EncodeCursor(examined[^1].CreatedAt, examined[^1].RequestId)
            : null;
        return new PlatformEgressDestinationPage(current, nextCursor);
    }

    private async Task<(ProviderHostRequestReceipt Receipt, string Fingerprint)?> ReadReceiptByIdAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var r = await db.ProviderHostRequestReceipts.AsNoTracking()
            .SingleOrDefaultAsync(receipt => receipt.RequestId == requestId, cancellationToken)
            .ConfigureAwait(false);
        if (r is null)
            return null;
        return (
            new ProviderHostRequestReceipt(
                r.RequestId, r.DestinationId, r.TenantId, r.Scope, r.ProjectId,
                r.ProviderId, r.ProviderRevision, r.ConfigurationHash, r.OperationId,
                r.ActorId, r.Host, r.Port, PlatformEgressDestinationState.Requested),
            r.RequestFingerprint);
    }

    private static void ValidateDispatch(ProviderHostRequestDispatch dispatch)
    {
        if (dispatch.RequestId == Guid.Empty ||
            dispatch.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(dispatch.TenantId) ||
            dispatch.TenantId.Length > 256 ||
            !Enum.IsDefined(dispatch.Scope) ||
            (dispatch.Scope == ModelProviderBindingScope.Platform && dispatch.ProjectId is not null) ||
            (dispatch.Scope == ModelProviderBindingScope.Project &&
             (string.IsNullOrWhiteSpace(dispatch.ProjectId) || dispatch.ProjectId.Length > 32)) ||
            string.IsNullOrWhiteSpace(dispatch.ProviderId) ||
            dispatch.ProviderId.Length > 128 ||
            dispatch.ProviderRevision < 1 ||
            !IsSha256(dispatch.ConfigurationHash) ||
            string.IsNullOrWhiteSpace(dispatch.ActorId) ||
            dispatch.ActorId.Length > 256)
            throw new ProviderHostRequestValidationException(
                "invalid_host_request",
                "The host request payload is incomplete or malformed.");

        if (PlatformEgressHostValidator.Validate(dispatch.Host, dispatch.Port) is { } error)
            throw new ProviderHostRequestValidationException(error.Code, error.Message);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static string ComputeFingerprint(ProviderHostRequestDispatch dispatch) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dispatch))).ToLowerInvariant();

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    internal static string EncodeCursor(DateTimeOffset createdAt, Guid requestId)
    {
        var raw = $"{createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{requestId:D}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    internal static bool TryDecodeCursor(string cursor, out DateTimeOffset createdAt, out Guid requestId)
    {
        createdAt = default;
        requestId = default;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 256)
            return false;
        try
        {
            var bytes = Base64Url.DecodeFromChars(cursor);
            var text = Encoding.UTF8.GetString(bytes);
            var parts = text.Split(':');
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTimeOffset.MinValue.UtcTicks ||
                ticks > DateTimeOffset.MaxValue.UtcTicks ||
                !Guid.TryParseExact(parts[1], "D", out requestId) ||
                requestId == Guid.Empty)
                return false;
            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal static class ProviderHostRequestEndpoints
{
    private const int MaximumInternalBodyBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IEndpointRouteBuilder MapProviderHostRequestInternalEndpoints(
        this IEndpointRouteBuilder endpoints,
        bool enabled)
    {
        if (!enabled)
            return endpoints;

        endpoints.MapPost("/internal/egress/host-requests", async (
            HttpContext context,
            ProviderHostRequestStore store,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryRequirePeer(context, out var denied))
                return denied!;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
                sizeFeature.MaxRequestBodySize = MaximumInternalBodyBytes;

            ProviderHostRequestDispatch? dispatch;
            try
            {
                dispatch = await JsonSerializer.DeserializeAsync<ProviderHostRequestDispatch>(
                    context.Request.Body, Json, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { code = "invalid_host_request" });
            }
            if (dispatch is null)
                return Results.BadRequest(new { code = "invalid_host_request" });

            try
            {
                var result = await store.ReceiveAsync(dispatch, cancellationToken).ConfigureAwait(false);
                return Results.Json(
                    result.Receipt,
                    Json,
                    statusCode: result.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            }
            catch (ProviderHostRequestValidationException exception)
            {
                return Results.BadRequest(new { code = exception.Code });
            }
            catch (ProviderHostRequestConflictException exception)
            {
                return Results.Conflict(new { code = exception.Code });
            }
        }).ExcludeFromDescription();

        endpoints.MapPost("/internal/egress/host-requests/status", async (
            HttpContext context,
            ProviderHostRequestStore store,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryRequirePeer(context, out var denied))
                return denied!;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
                sizeFeature.MaxRequestBodySize = MaximumInternalBodyBytes;

            ProviderHostRequestStatusQuery? query;
            try
            {
                query = await JsonSerializer.DeserializeAsync<ProviderHostRequestStatusQuery>(
                    context.Request.Body, Json, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { code = "invalid_status_query" });
            }
            if (query is null || query.RequestIds.IsDefault)
                return Results.BadRequest(new { code = "invalid_status_query" });

            try
            {
                var response = await store.GetStatusesAsync(query.RequestIds, cancellationToken).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (ProviderHostRequestValidationException exception)
            {
                return Results.BadRequest(new { code = exception.Code });
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    public static IEndpointRouteBuilder MapPlatformEgressDestinationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/platform/egress/destinations", async (
            HttpContext context,
            IProjectsConfigClient projectsClient,
            ProviderHostRequestStore store,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!TryReadQuery(context.Request, out var pageSize, out var cursor))
                return Results.BadRequest(new { code = "invalid_pagination" });

            if (!TryReadCaller(context, out var caller))
                return Results.Unauthorized();

            ProjectAuthorizationContextResponse authContext;
            try
            {
                authContext = await projectsClient.GetAuthorizationContextAsync(caller!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProjectsConfigApiException exception) when (exception.Code == "authorization_context_denied")
            {
                return Results.Json(new { code = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ProjectsConfigApiException)
            {
                return Results.Json(
                    new { code = "egress_proof_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (HttpRequestException)
            {
                return Results.Json(
                    new { code = "egress_proof_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (authContext.BoundProjectId is not null ||
                authContext.BoundRunId is not null ||
                !HasPlatformAdminReadPermission(authContext))
                return Results.Json(new { code = "forbidden" }, statusCode: StatusCodes.Status403Forbidden);

            try
            {
                var page = await store.ListRequestedDestinationsAsync(pageSize, cursor, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(page);
            }
            catch (ProviderHostRequestValidationException exception)
            {
                return Results.BadRequest(new { code = exception.Code });
            }
            catch (ProviderHostRequestProofUnavailableException)
            {
                return Results.Json(
                    new { code = "egress_proof_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization();

        return endpoints;
    }

    private static bool TryRequirePeer(HttpContext context, out IResult? denied)
    {
        try
        {
            var identity = context.RequestServices.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(
                HttpProjectsHostRequestProofClient.ChannelKey);
            identity.RequirePeerConnection(
                context.Request.IsHttps,
                context.Features.Get<ITlsHandshakeFeature>()?.Protocol,
                context.Connection.LocalPort,
                context.Features.Get<ITlsConnectionFeature>()?.ClientCertificate);
            denied = null;
            return true;
        }
        catch (RuntimeAuthorizationException exception)
        {
            denied = Results.Json(new { code = exception.Code }, statusCode: StatusCodes.Status403Forbidden);
            return false;
        }
    }

    private static bool HasPlatformAdminReadPermission(ProjectAuthorizationContextResponse auth) =>
        !auth.EffectiveAuthority.IsDefault &&
        auth.EffectiveAuthority.Any(resource =>
            resource.ResourceType == ProjectAuthorityResourceType.Platform &&
            string.Equals(resource.ResourceId, "default", StringComparison.Ordinal) &&
            resource.Permissions.Any(grant =>
                grant.Permission == ProjectAuthorizationPermission.ReadPlatformRuntimeDefaults &&
                grant.RoleRevision > 0));

    private static bool TryReadQuery(HttpRequest request, out int pageSize, out string? cursor)
    {
        pageSize = 50;
        cursor = null;
        foreach (var key in request.Query.Keys)
        {
            var values = request.Query[key];
            if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
                return false;
            if (key == "pageSize")
            {
                if (!int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) ||
                    pageSize is < 1 or > 100)
                    return false;
            }
            else if (key == "cursor")
            {
                cursor = values[0];
            }
            else if (key == "state")
            {
                if (!string.Equals(values[0], "requested", StringComparison.Ordinal))
                    return false;
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryReadCaller(HttpContext context, out CurrentCallerRequest? caller)
    {
        caller = null;
        var authorizationHeaders = context.Request.Headers.Authorization;
        if (authorizationHeaders.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(authorizationHeaders[0], out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
            return false;
        var tenantHeaders = context.Request.Headers[ProjectAuthorizationContextContract.TenantSelectorHeader];
        if (tenantHeaders.Count > 1)
            return false;
        var tenantSelector = tenantHeaders.Count == 1 ? tenantHeaders[0] : null;
        caller = new CurrentCallerRequest(header.Parameter, tenantSelector);
        return true;
    }
}
