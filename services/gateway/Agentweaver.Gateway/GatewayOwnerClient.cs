using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;

namespace Agentweaver.Gateway;

internal sealed class GatewayOwnerFailureException(
    int statusCode,
    string contentType,
    byte[] body) : Exception("An owner API request failed.")
{
    public int StatusCode { get; } = statusCode;
    public string ContentType { get; } = contentType;
    public byte[] Body { get; } = body;
}

public sealed class GatewayOwnerClient(
    IHttpClientFactory clients,
    GatewayOptions options)
{
    private const long MaximumProxyResponseBytes = 8 * 1024 * 1024;
    private const long MaximumOwnerErrorBytes = 64 * 1024;
    private const long MaximumRunEventPageBytes = 1024 * 1024;
    private const int MaximumSseCursorLength = 8192;
    private const string TenantSelectorHeader = ProjectAuthorizationContextContract.TenantSelectorHeader;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16,
    };

    public async Task ProxyAsync(
        HttpContext context,
        GatewayRoute route,
        CancellationToken cancellationToken)
    {
        try
        {
            var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
            using var timeout = CreateOwnerTimeout(cancellationToken);
            using var request = CreateOwnerRequest(context, route, bearer);
            using var response = await SendAsync(route.Owner, request, timeout.Token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                await WriteProblemAsync(context, "owner_redirect_rejected", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
                return;
            }
            if (response.Content.Headers.ContentLength is > MaximumProxyResponseBytes)
            {
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            CopyResponseHeaders(context, response, route.Owner);
            if (response.Content.Headers.ContentType is { } contentType)
                context.Response.ContentType = contentType.ToString();
            if (response.Content.Headers.ContentLength is { } contentLength)
                context.Response.ContentLength = contentLength;

            if (!HttpMethods.IsHead(context.Request.Method) &&
                response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.NotModified)
                await CopyBoundedAsync(
                    await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                    context.Response.Body,
                    MaximumProxyResponseBytes,
                    timeout.Token).ConfigureAwait(false);
        }
        catch (GatewayOwnerFailureException exception)
        {
            await WriteOwnerFailureAsync(context, exception).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteProblemAsync(context, "owner_timeout", StatusCodes.Status504GatewayTimeout)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (IOException)
        {
            await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                .ConfigureAwait(false);
        }
    }

    public async Task SubscribeRunEventsAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        try
        {
            var cursor = ReadSseCursor(context);
            await EnsureCurrentProjectReadAsync(context, projectId, runId, cancellationToken)
                .ConfigureAwait(false);
            var page = await ReadRunEventPageAsync(
                context, projectId, runId, cursor, cancellationToken).ConfigureAwait(false);
            await EnsureCurrentProjectReadAsync(context, projectId, runId, cancellationToken)
                .ConfigureAwait(false);

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);
            if (page.Event is not null)
            {
                await WriteEventAsync(context, page, cancellationToken).ConfigureAwait(false);
                cursor = page.NextCursor;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                await EnsureCurrentProjectReadAsync(context, projectId, runId, cancellationToken)
                    .ConfigureAwait(false);
                page = await ReadRunEventPageAsync(
                    context, projectId, runId, cursor, cancellationToken).ConfigureAwait(false);
                if (page.Event is not null)
                {
                    await EnsureCurrentProjectReadAsync(context, projectId, runId, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteEventAsync(context, page, cancellationToken).ConfigureAwait(false);
                    cursor = page.NextCursor;
                }
                else
                {
                    await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await Task.Delay(
                        GatewayOptions.EventPollIntervalMilliseconds,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (GatewayOwnerFailureException exception)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteOwnerFailureAsync(context, exception).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_timeout", StatusCodes.Status504GatewayTimeout)
                    .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_contract_invalid", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_response_too_large", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "invalid_cursor", StatusCodes.Status400BadRequest)
                    .ConfigureAwait(false);
        }
        catch (IOException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await WriteProblemAsync(context, "owner_unavailable", StatusCodes.Status502BadGateway)
                    .ConfigureAwait(false);
        }
    }

    private async Task EnsureCurrentProjectReadAsync(
        HttpContext context,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
        using var timeout = CreateOwnerTimeout(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildOwnerUri(
                GatewayOwner.Projects,
                $"/api/projects/{Uri.EscapeDataString(projectId)}?runId={Uri.EscapeDataString(runId)}"));
        request.Headers.Authorization = bearer;
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        CopyTenantSelector(context, request);
        using var response = await SendAsync(GatewayOwner.Projects, request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw await ToOwnerFailureAsync(response, timeout.Token).ConfigureAwait(false);
        if (response.Headers.CacheControl?.NoStore != true)
            throw OwnerContractFailure("projects_read_cache_policy_missing");
        await response.Content.CopyToAsync(Stream.Null, timeout.Token).ConfigureAwait(false);
    }

    private async Task<GatewayRunEventPage> ReadRunEventPageAsync(
        HttpContext context,
        string projectId,
        string runId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
        var query = cursor is null
            ? "?limit=1"
            : $"?cursor={Uri.EscapeDataString(cursor)}&limit=1";
        var path = $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}/events{query}";
        using var timeout = CreateOwnerTimeout(cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Get, BuildOwnerUri(GatewayOwner.Events, path));
        request.Headers.Authorization = bearer;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        CopyTenantSelector(context, request);
        using var response = await SendAsync(GatewayOwner.Events, request, timeout.Token).ConfigureAwait(false);
        if (IsRedirect(response.StatusCode))
            throw OwnerContractFailure("events_owner_redirect_rejected");
        if (response.StatusCode != HttpStatusCode.OK)
            throw await ToOwnerFailureAsync(response, timeout.Token).ConfigureAwait(false);
        var body = await ReadBoundedAsync(
            response.Content,
            MaximumRunEventPageBytes,
            timeout.Token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Array ||
            events.GetArrayLength() > 1 ||
            !root.TryGetProperty("nextCursor", out var nextCursorElement))
            throw OwnerContractFailure("events_page_contract_invalid");

        var nextCursor = nextCursorElement.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => nextCursorElement.GetString(),
            _ => throw OwnerContractFailure("events_page_contract_invalid"),
        };
        if (events.GetArrayLength() == 0)
        {
            if (!string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                throw OwnerContractFailure("events_page_cursor_invalid");
            return new GatewayRunEventPage(null, cursor);
        }
        if (string.IsNullOrWhiteSpace(nextCursor) ||
            nextCursor.Length > MaximumSseCursorLength ||
            !IsSafeSseValue(nextCursor) ||
            string.Equals(nextCursor, cursor, StringComparison.Ordinal))
            throw OwnerContractFailure("events_page_cursor_invalid");
        return new GatewayRunEventPage(events[0].Clone(), nextCursor);
    }

    private async Task<GatewayOwnerFailureException> ToOwnerFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (IsRedirect(response.StatusCode))
            return OwnerContractFailure("owner_redirect_rejected");
        var body = await ReadBoundedAsync(
            response.Content,
            MaximumOwnerErrorBytes,
            cancellationToken).ConfigureAwait(false);
        return new GatewayOwnerFailureException(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString() ?? "application/problem+json",
            body);
    }

    private HttpRequestMessage CreateOwnerRequest(
        HttpContext context,
        GatewayRoute route,
        AuthenticationHeaderValue bearer)
    {
        var path = ExpandOwnerPath(route.OwnerPath, context);
        var target = BuildOwnerUri(route.Owner, path + context.Request.QueryString);
        var request = new HttpRequestMessage(new HttpMethod(route.Method), target);
        request.Headers.Authorization = bearer;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        CopyTenantSelector(context, request);
        CopySingleRequestHeader(context, request, "Idempotency-Key");
        CopySingleRequestHeader(context, request, "If-Match");
        if (route.HasJsonBody)
        {
            var contentType = context.Request.ContentType;
            if (contentType is not null &&
                (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
                    parsed.MediaType is null ||
                    !(parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                      parsed.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))))
            {
                request.Dispose();
                throw new GatewayOwnerFailureException(
                    StatusCodes.Status415UnsupportedMediaType,
                    "application/problem+json",
                    ProblemBody("request_content_type_invalid", StatusCodes.Status415UnsupportedMediaType));
            }
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentLength is { } contentLength)
                request.Content.Headers.ContentLength = contentLength;
            request.Content.Headers.ContentType = contentType is null
                ? new MediaTypeHeaderValue("application/json")
                : MediaTypeHeaderValue.Parse(contentType);
        }
        return request;
    }

    private static async Task<AuthenticationHeaderValue> RequireValidatedBearerAsync(HttpContext context)
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiresAt = authentication.Principal?.GetExpirationDate();
        if (expiresAt is null &&
            long.TryParse(
                authentication.Principal?.FindFirst("exp")?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var unixExpiration) &&
            unixExpiration >= DateTimeOffset.MinValue.ToUnixTimeSeconds() &&
            unixExpiration <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixExpiration);
        var values = context.Request.Headers.Authorization;
        if (!authentication.Succeeded ||
            expiresAt is null ||
            expiresAt.Value <= DateTimeOffset.UtcNow ||
            context.User.Identity?.IsAuthenticated != true ||
            values.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(values[0], out var bearer) ||
            !string.Equals(bearer.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(bearer.Parameter) ||
            bearer.Parameter.Length > 16_384 ||
            bearer.Parameter.Any(char.IsWhiteSpace))
            throw new GatewayOwnerFailureException(
                StatusCodes.Status401Unauthorized,
                "application/problem+json",
                ProblemBody("unauthenticated", StatusCodes.Status401Unauthorized));
        return bearer;
    }

    private static string? ReadSseCursor(HttpContext context)
    {
        if (context.Request.Query.Keys.Any(key => key != "cursor") ||
            context.Request.Query["cursor"].Count > 1)
            throw new ArgumentException("The event stream accepts only one cursor.");
        var queryCursor = context.Request.Query["cursor"].FirstOrDefault();
        var headerValues = context.Request.Headers["Last-Event-ID"];
        if (headerValues.Count > 1)
            throw new ArgumentException("Only one Last-Event-ID value is permitted.");
        var headerCursor = headerValues.FirstOrDefault();
        if (queryCursor is not null && headerCursor is not null &&
            !string.Equals(queryCursor, headerCursor, StringComparison.Ordinal))
            throw new ArgumentException("The event cursor sources must match.");
        var cursor = headerCursor ?? queryCursor;
        if (cursor is not null &&
            (cursor.Length > MaximumSseCursorLength || !IsSafeSseValue(cursor)))
            throw new ArgumentException("The event cursor is invalid.");
        return cursor;
    }

    private static bool IsSafeSseValue(string value) =>
        value.Length > 0 &&
        value.All(character => character is not '\r' and not '\n' and not '\0');

    private async Task WriteEventAsync(
        HttpContext context,
        GatewayRunEventPage page,
        CancellationToken cancellationToken)
    {
        _ = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
        var cursor = page.NextCursor
            ?? throw new InvalidDataException("Events owner omitted the cursor for a journal event.");
        var eventText = $"id: {cursor}\nevent: session-event\n" +
            $"data: {JsonSerializer.Serialize(page.Event, JsonOptions)}\n\n";
        await context.Response.WriteAsync(eventText, cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ExpandOwnerPath(string template, HttpContext context)
    {
        var result = System.Text.RegularExpressions.Regex.Replace(
            template,
            @"\{(?<name>[A-Za-z][A-Za-z0-9]*)(?::[^}]+)?\}",
            match =>
            {
                var key = match.Groups["name"].Value;
                var value = context.Request.RouteValues[key]?.ToString()
                    ?? throw new InvalidOperationException($"Missing route value '{key}'.");
                return Uri.EscapeDataString(value);
            },
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return result;
    }

    private Uri BuildOwnerUri(GatewayOwner owner, string pathAndQuery) =>
        new(options.GetOwnerBaseAddress(owner), pathAndQuery);

    private HttpClient OwnerHttpClient(GatewayOwner owner) =>
        clients.CreateClient(owner.ToString());

    private CancellationTokenSource CreateOwnerTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.OwnerRequestTimeout);
        return timeout;
    }

    private Task<HttpResponseMessage> SendAsync(
        GatewayOwner owner,
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        OwnerHttpClient(owner).SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private static void CopyTenantSelector(HttpContext context, HttpRequestMessage request)
    {
        foreach (var value in context.Request.Headers[TenantSelectorHeader])
            if (value is not null)
                request.Headers.TryAddWithoutValidation(TenantSelectorHeader, value);
    }

    private static void CopySingleRequestHeader(
        HttpContext context,
        HttpRequestMessage request,
        string name)
    {
        var values = context.Request.Headers[name];
        if (values.Count > 1)
            throw new GatewayOwnerFailureException(
                StatusCodes.Status400BadRequest,
                "application/problem+json",
                ProblemBody("request_header_invalid", StatusCodes.Status400BadRequest));
        if (values.Count == 1 && values[0] is { } value)
            request.Headers.TryAddWithoutValidation(name, value);
    }

    private void CopyResponseHeaders(
        HttpContext context,
        HttpResponseMessage response,
        GatewayOwner owner)
    {
        CopyHeader(response.Headers, context.Response.Headers, "ETag");
        CopyHeader(response.Content.Headers, context.Response.Headers, "Last-Modified");
        CopyHeader(response.Headers, context.Response.Headers, "Retry-After");
        if (response.Headers.Location is { } location &&
            RewriteLocation(location, owner) is { } rewritten)
            context.Response.Headers.Location = rewritten;
    }

    private string? RewriteLocation(Uri location, GatewayOwner owner)
    {
        string pathAndQuery;
        if (location.IsAbsoluteUri)
        {
            var baseAddress = options.GetOwnerBaseAddress(owner);
            if (!baseAddress.IsBaseOf(location))
                return null;
            pathAndQuery = location.PathAndQuery;
        }
        else
        {
            pathAndQuery = location.OriginalString;
        }
        if (!pathAndQuery.StartsWith("/api/", StringComparison.Ordinal) ||
            pathAndQuery.StartsWith("//", StringComparison.Ordinal) ||
            pathAndQuery.Contains('\r') || pathAndQuery.Contains('\n'))
            return null;
        return VersionPrefixLocation(pathAndQuery);
    }

    private static string VersionPrefixLocation(string pathAndQuery) =>
        GatewayRouteCatalog.VersionPrefix + pathAndQuery[4..];

    private static void CopyHeader(
        HttpHeaders source,
        IHeaderDictionary destination,
        string name)
    {
        if (source.TryGetValues(name, out var values))
            destination[name] = values.ToArray();
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        (int)status is >= 300 and <= 399;

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    return;
                total += read;
                if (total > maximumBytes)
                    throw new InvalidDataException("The owner response exceeded the Gateway limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidDataException("The owner response exceeded the Gateway limit.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var buffer = new MemoryStream();
        await CopyBoundedAsync(source, buffer, maximumBytes, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task WriteOwnerFailureAsync(
        HttpContext context,
        GatewayOwnerFailureException exception)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        context.Response.StatusCode = exception.StatusCode;
        context.Response.ContentType = exception.ContentType;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.ContentLength = exception.Body.Length;
        await context.Response.Body.WriteAsync(exception.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private static Task WriteProblemAsync(HttpContext context, string code, int statusCode) =>
        context.Response.HasStarted
            ? AbortResponseAsync(context)
            : Results.Problem(
                title: code,
                statusCode: statusCode,
                extensions: new Dictionary<string, object?> { ["code"] = code })
            .ExecuteAsync(context);

    private static Task AbortResponseAsync(HttpContext context)
    {
        context.Abort();
        return Task.CompletedTask;
    }

    private static GatewayOwnerFailureException OwnerContractFailure(string code) =>
        new(
            StatusCodes.Status502BadGateway,
            "application/problem+json",
            ProblemBody(code, StatusCodes.Status502BadGateway));

    private static byte[] ProblemBody(string code, int statusCode) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "about:blank",
            title = code,
            status = statusCode,
            code,
        }, JsonOptions);

    private sealed record GatewayRunEventPage(JsonElement? Event, string? NextCursor);
}
