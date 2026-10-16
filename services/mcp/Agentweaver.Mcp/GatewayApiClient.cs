using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;

namespace Agentweaver.Mcp;

public sealed class GatewayApiClient(
    HttpClient client,
    McpServiceOptions options)
{
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    public async Task<HttpResponseMessage> SendAsync(
        HttpContext context,
        string method,
        string pathAndQuery,
        JsonElement? body,
        string? tenantSelector,
        string? idempotencyKey,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!AllowedMethods.Contains(method))
            throw new ArgumentException("The Gateway method is not supported.", nameof(method));

        var bearer = await RequireValidatedBearerAsync(context).ConfigureAwait(false);
        var target = CreateTarget(pathAndQuery);
        using var request = new HttpRequestMessage(new HttpMethod(method), target);
        request.Headers.Authorization = bearer;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        AddHeader(request, "X-Agentweaver-Tenant", tenantSelector);
        AddHeader(request, "Idempotency-Key", idempotencyKey);
        AddHeader(request, "If-Match", ifMatch);
        if (body is { } json)
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(json));
        if (request.Content is not null)
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private Uri CreateTarget(string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(pathAndQuery) ||
            !Uri.TryCreate(pathAndQuery, UriKind.Relative, out var relative) ||
            relative.IsAbsoluteUri)
            throw new ArgumentException("The Gateway path must be a relative URI.", nameof(pathAndQuery));
        if (!pathAndQuery.StartsWith("/api/v1/", StringComparison.Ordinal) ||
            pathAndQuery.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("The Gateway path is outside the admitted API.", nameof(pathAndQuery));
        if (pathAndQuery.Contains('\\') || pathAndQuery.Contains('#'))
            throw new ArgumentException("The Gateway path contains an invalid separator.", nameof(pathAndQuery));
        if (HasTraversalSegment(pathAndQuery))
            throw new ArgumentException("The Gateway path contains an invalid segment.", nameof(pathAndQuery));

        var target = new Uri(options.GatewayBaseAddress, relative);
        if (target.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(target.Authority, options.GatewayBaseAddress.Authority, StringComparison.Ordinal))
            throw new InvalidOperationException("The Gateway request escaped its configured service root.");
        return target;
    }

    private static bool HasTraversalSegment(string pathAndQuery)
    {
        var path = pathAndQuery.Split('?', 2)[0];
        var segments = path.Split('/');
        foreach (var segment in segments.Skip(1))
        {
            if (segment.Length == 0)
                return true;
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded is "." or ".." || decoded.Contains('/') || decoded.Contains('\\'))
                return true;
        }
        return false;
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
            throw new UnauthorizedAccessException("A current validated MCP bearer token is required.");

        return bearer;
    }

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (value is null)
            return;
        if (value.Length is 0 or > 8192 ||
            value.Any(character => character is '\r' or '\n' or '\0') ||
            !request.Headers.TryAddWithoutValidation(name, value))
            throw new ArgumentException($"The {name} header value is invalid.", nameof(value));
    }
}
