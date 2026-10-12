using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public static class RuntimeOwnerHttpTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false };

    public static Uri RequireOwnerAddress(Uri address)
    {
        if (!RuntimeContractValidation.IsHttpsEndpoint(address) || address.AbsolutePath != "/")
            throw new RuntimeAuthorizationException("runtime_owner_address_invalid");
        return address;
    }

    public static async Task<TResponse> SendAsync<TResponse>(
        HttpClient client, Uri ownerAddress, string path, RuntimeActorAuthorization actor,
        object? body, CancellationToken cancellationToken) where TResponse : class
        => await SendCoreAsync<TResponse>(client, ownerAddress, path, actor, body,
            allowNotFound: false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");

    public static Task<TResponse?> ReadOptionalAsync<TResponse>(
        HttpClient client, Uri ownerAddress, string path, RuntimeActorAuthorization actor,
        CancellationToken cancellationToken) where TResponse : class =>
        SendCoreAsync<TResponse>(client, ownerAddress, path, actor, null,
            allowNotFound: true, cancellationToken);

    public static async Task<TResponse?> SendBoundedAsync<TResponse>(
        HttpClient client, Uri ownerAddress, string path, RuntimeActorAuthorization actor,
        object? body, int maximumBytes, bool allowNotFound, CancellationToken cancellationToken,
        SandboxRuntimeHttpsEndpoint? sandboxEndpoint = null)
        where TResponse : class
    {
        if (maximumBytes is < 1 or > 4 * SessionMaterialValidation.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var owner = RequireOwnerAddress(ownerAddress);
        var endpoint = new Uri(owner, path);
        if (!path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            endpoint.Scheme != owner.Scheme || endpoint.Authority != owner.Authority ||
            endpoint.Fragment.Length != 0)
            throw new RuntimeAuthorizationException("runtime_owner_address_invalid");
        var payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), JsonOptions);
        if (payload?.Length > maximumBytes)
            throw new RuntimeAuthorizationException("runtime_owner_request_size_invalid");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (client.Timeout != Timeout.InfiniteTimeSpan)
            deadline.CancelAfter(client.Timeout);
        var token = deadline.Token;
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, endpoint);
        if (sandboxEndpoint is not null)
        {
            sandboxEndpoint.Validate();
            if (sandboxEndpoint.Address != owner)
                throw new RuntimeAuthorizationException("runtime_sandbox_endpoint_invalid");
            request.Options.Set(RuntimeSandboxHttpTransport.EndpointOption, sandboxEndpoint);
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Bearer.GetValue());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        if (actor.TenantSelector is not null)
            request.Headers.Add("X-Agentweaver-Tenant", actor.TenantSelector);
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and <= 399 || response.RequestMessage?.RequestUri != endpoint)
            throw new RuntimeAuthorizationException("runtime_owner_redirect_rejected");
        if (response.Headers.CacheControl?.NoStore != true)
            throw new RuntimeAuthorizationException("runtime_owner_cache_contract_invalid");
        if (allowNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new RuntimeAuthorizationException(
                response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        "runtime_owner_denied",
                    System.Net.HttpStatusCode.Conflict => "runtime_owner_binding_changed",
                    _ => "runtime_owner_unavailable"
                });
        if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength is { } length && (length <= 0 || length > maximumBytes))
            throw new RuntimeAuthorizationException("runtime_owner_response_size_invalid");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(
                   buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximumBytes - bytes.Length + 1)), token)
                   .ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > maximumBytes)
                throw new RuntimeAuthorizationException("runtime_owner_response_size_invalid");
            bytes.Write(buffer, 0, count);
        }
        try
        {
            return JsonSerializer.Deserialize<TResponse>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), JsonOptions)
                ?? throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");
        }
        catch (JsonException)
        {
            throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");
        }
    }

    public static async Task<byte[]> ReadBuildTestSeedAsync(
        HttpClient client, Uri ownerAddress, string path, RuntimeActorAuthorization actor,
        SandboxBuildTestWorkspaceSource source, CancellationToken cancellationToken)
    {
        _ = source.Validate();
        return await ReadBuildTestBytesAsync(client, ownerAddress, path, actor,
            source.SeedByteLength, source.SeedSha256, "buildtest_seed", cancellationToken).ConfigureAwait(false);
    }

    public static Task<byte[]> ReadBuildTestCaptureAsync(
        HttpClient client, Uri ownerAddress, string path, RuntimeActorAuthorization actor,
        SandboxBuildTestWorkspaceCaptureReference capture, CancellationToken cancellationToken)
    {
        if (capture.DocumentByteLength is < ProducedRunCaptureLimits.DocumentHeaderBytes or
                > ProducedRunCaptureLimits.MaximumDocumentBytes ||
            capture.DocumentSha256 is not { Length: 64 } || !capture.DocumentSha256.All(Uri.IsHexDigit))
            throw new RuntimeAuthorizationException("buildtest_capture_contract_invalid");
        return ReadBuildTestBytesAsync(client, ownerAddress, path, actor,
            capture.DocumentByteLength, capture.DocumentSha256, "buildtest_capture", cancellationToken);
    }

    private static async Task<byte[]> ReadBuildTestBytesAsync(
        HttpClient client, Uri ownerAddress, string path, RuntimeActorAuthorization actor,
        long byteLength, string sha256, string codePrefix, CancellationToken cancellationToken)
    {
        var owner = RequireOwnerAddress(ownerAddress);
        var endpoint = new Uri(owner, path);
        if (!path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            endpoint.Scheme != owner.Scheme || endpoint.Authority != owner.Authority ||
            endpoint.Fragment.Length != 0)
            throw new RuntimeAuthorizationException(codePrefix + "_owner_address_invalid");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (client.Timeout != Timeout.InfiniteTimeSpan)
            deadline.CancelAfter(client.Timeout);
        var readToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Bearer.GetValue());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        if (actor.TenantSelector is not null)
            request.Headers.Add("X-Agentweaver-Tenant", actor.TenantSelector);
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, readToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and <= 399 || response.RequestMessage?.RequestUri != endpoint)
            throw new RuntimeAuthorizationException("runtime_owner_redirect_rejected");
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            throw new RuntimeAuthorizationException(
                response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? "runtime_owner_denied" : "runtime_owner_unavailable");
        if (response.Headers.CacheControl?.NoStore != true ||
            response.Content.Headers.ContentType?.MediaType != "application/octet-stream" ||
            response.Content.Headers.ContentLength is { } length && length != byteLength)
            throw new RuntimeAuthorizationException(codePrefix + "_contract_invalid");
        var bytes = new byte[checked((int)byteLength)];
        await using var stream = await response.Content.ReadAsStreamAsync(readToken).ConfigureAwait(false);
        try
        {
            await stream.ReadExactlyAsync(bytes, readToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new RuntimeAuthorizationException(codePrefix + "_contract_invalid");
        }
        if (await stream.ReadAsync(new byte[1], readToken).ConfigureAwait(false) != 0 ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes), Convert.FromHexString(sha256)))
            throw new RuntimeAuthorizationException(codePrefix + "_integrity_invalid");
        return bytes;
    }

    private static async Task<TResponse?> SendCoreAsync<TResponse>(
        HttpClient client, Uri ownerAddress, string path, RuntimeActorAuthorization actor,
        object? body, bool allowNotFound, CancellationToken cancellationToken) where TResponse : class
    {
        var endpoint = new Uri(RequireOwnerAddress(ownerAddress), path);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Bearer.GetValue());
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        if (actor.TenantSelector is not null)
            request.Headers.Add("X-Agentweaver-Tenant", actor.TenantSelector);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and <= 399 ||
            response.RequestMessage?.RequestUri != endpoint)
            throw new RuntimeAuthorizationException("runtime_owner_redirect_rejected");
        if (allowNotFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            if (response.Headers.CacheControl?.NoStore != true)
                throw new RuntimeAuthorizationException("runtime_owner_cache_contract_invalid");
            return null;
        }
        if (!response.IsSuccessStatusCode)
            throw new RuntimeAuthorizationException(
                response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? "runtime_owner_denied" : "runtime_owner_unavailable");
        if (response.Headers.CacheControl?.NoStore != true)
            throw new RuntimeAuthorizationException("runtime_owner_cache_contract_invalid");
        try
        {
            return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");
        }
        catch (JsonException)
        {
            throw new RuntimeAuthorizationException("runtime_owner_contract_invalid");
        }
    }
}
