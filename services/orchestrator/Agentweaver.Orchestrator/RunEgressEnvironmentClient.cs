using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

/// <summary>Reads stored run egress from the Environment owner using the caller's bearer and tenant selector.</summary>
internal sealed class RunEgressEnvironmentClient(HttpClient client, Uri ownerAddress)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8
    };

    private sealed record EnvironmentErrorPayload(string? Code);

    public async Task<RunEgressPage> ReadRunEgressAsync(
        RuntimeActorAuthorization actor,
        string projectId,
        string runId,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var owner = RuntimeOwnerHttpTransport.RequireOwnerAddress(ownerAddress);
        var path = $"/api/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/egress" +
            $"?limit={pageSize.ToString(CultureInfo.InvariantCulture)}" +
            (cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}");
        var endpoint = new Uri(owner, path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (client.Timeout != Timeout.InfiniteTimeSpan)
            deadline.CancelAfter(client.Timeout);
        var token = deadline.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Bearer.GetValue());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        if (actor.TenantSelector is not null)
            request.Headers.TryAddWithoutValidation(
                ProjectAuthorizationContextContract.TenantSelectorHeader,
                actor.TenantSelector);

        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399 || response.RequestMessage?.RequestUri != endpoint)
                throw Unavailable();
            if (response.Headers.CacheControl?.NoStore != true)
                throw Unavailable();

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var errorCode = await TryReadErrorCodeAsync(response, token).ConfigureAwait(false);
                throw new CoordinationException(
                    errorCode is "run_egress_cursor_invalid" or "run_egress_query_invalid"
                        ? errorCode
                        : "run_egress_query_invalid",
                    StatusCodes.Status400BadRequest);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new CoordinationException("caller_bearer_invalid", StatusCodes.Status401Unauthorized);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new CoordinationException("run_egress_permission_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new CoordinationException("run_not_found", StatusCodes.Status404NotFound);
            if (response.StatusCode != HttpStatusCode.OK)
                throw Unavailable();

            if (response.Content.Headers.ContentType?.MediaType is not { } mediaType ||
                !mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                response.Content.Headers.ContentLength is { } length &&
                (length <= 0 || length > RunEgressContract.MaximumResponseBytes))
                throw Unavailable();

            var bytes = await ReadBoundedBytesAsync(
                response.Content, RunEgressContract.MaximumResponseBytes, token).ConfigureAwait(false);
            var page = JsonSerializer.Deserialize<RunEgressPage>(bytes, JsonOptions);
            if (page is null ||
                page.RunId != runId ||
                page.Coverage != RunEgressContract.CoverageUnavailable ||
                page.DelayedUntil is not null ||
                page.Items.IsDefault ||
                page.Items.Length > pageSize ||
                page.Items.Any(item => item is null || item.Port is < 1 or > 65535 || item.Count < 1 ||
                    (item.Host is null && item.Address is null)))
                throw Unavailable();

            return page;
        }
        catch (CoordinationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException ||
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw Unavailable();
        }
    }

    private static async Task<string?> TryReadErrorCodeAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            var bytes = await ReadBoundedBytesAsync(response.Content, 16 * 1024, token).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EnvironmentErrorPayload>(bytes, ErrorJsonOptions)?.Code;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(
                   buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximumBytes - memory.Length + 1)), token)
                   .ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > maximumBytes)
                throw new InvalidDataException("Response exceeded the maximum allowed size.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    private static CoordinationException Unavailable() =>
        new("run_egress_unavailable", StatusCodes.Status503ServiceUnavailable);
}
