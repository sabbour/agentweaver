using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentweaver.Cli.Auth;

internal sealed record OidcMetadata(
    string Issuer,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri JsonWebKeySetEndpoint);

internal sealed record OAuthTokenResponse(
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    double ExpiresIn);

internal static class OidcProtocol
{
    private const int MaximumMetadataBytes = 64 * 1024;
    private const int MaximumTokenResponseBytes = 64 * 1024;
    private const int MaximumTokenLength = 16 * 1024;
    private const int MaximumIdTokenLength = 64 * 1024;
    private const int MaximumLifetimeSeconds = 24 * 60 * 60;

    public static async Task<OidcMetadata> DiscoverAsync(
        HttpClient client,
        string issuer,
        CancellationToken ct)
    {
        var issuerUri = new Uri(issuer, UriKind.Absolute);
        var discoveryUri = new Uri(issuerUri, ".well-known/openid-configuration");
        using var requestCancellation = CreateRequestCancellation(client, ct);
        var requestCt = requestCancellation.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, discoveryUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, requestCt).ConfigureAwait(false);
        EnsureNoRedirect(response, discoveryUri);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The configured Identity Broker discovery request failed.");

        using var document = await ReadJsonAsync(response, MaximumMetadataBytes, requestCt).ConfigureAwait(false);
        var root = document.RootElement;
        var discoveredIssuer = RequiredString(root, "issuer", 2048);
        if (!string.Equals(discoveredIssuer, issuer, StringComparison.Ordinal))
            throw new InvalidOperationException("The Identity Broker issuer did not match the configured issuer.");

        var authorization = RequiredEndpoint(root, "authorization_endpoint", issuerUri);
        var token = RequiredEndpoint(root, "token_endpoint", issuerUri);
        var keys = RequiredEndpoint(root, "jwks_uri", issuerUri);
        if (authorization.AbsolutePath != "/connect/authorize" ||
            token.AbsolutePath != "/connect/token" ||
            !SameOrigin(keys, issuerUri))
            throw new InvalidOperationException("The Identity Broker discovery document contains an unsupported endpoint.");

        return new OidcMetadata(discoveredIssuer, authorization, token, keys);
    }

    public static Uri CreateAuthorizationUri(
        OidcMetadata metadata,
        CliAuthIdentity identity,
        Uri redirectUri,
        string state,
        string nonce,
        string challenge)
    {
        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", identity.Identity.Target.ClientId),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("response_type", "code"),
            new("scope", string.Join(" ", identity.Identity.Scopes)),
            new("state", state),
            new("nonce", nonce),
            new("code_challenge", challenge),
            new("code_challenge_method", "S256"),
        };
        if (identity.Identity.ProjectId is not null)
        {
            query.Add(new("project_id", identity.Identity.ProjectId));
            query.Add(new("run_id", identity.Identity.RunId!));
        }

        var encoded = string.Join("&", query.Select(field =>
            $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(field.Value)}"));
        return new UriBuilder(metadata.AuthorizationEndpoint) { Query = encoded }.Uri;
    }

    public static Task<OAuthTokenResponse> ExchangeCodeAsync(
        HttpClient client,
        OidcMetadata metadata,
        CliAuthIdentity identity,
        Uri redirectUri,
        string code,
        string verifier,
        CancellationToken ct) =>
        ExchangeAsync(client, metadata.TokenEndpoint,
        [
            new("grant_type", "authorization_code"),
            new("client_id", identity.Identity.Target.ClientId),
            new("redirect_uri", redirectUri.AbsoluteUri),
            new("code", code),
            new("code_verifier", verifier),
        ], ct);

    public static Task<OAuthTokenResponse> RefreshAsync(
        HttpClient client,
        OidcMetadata metadata,
        CliAuthIdentity identity,
        string refreshToken,
        CancellationToken ct) =>
        ExchangeAsync(client, metadata.TokenEndpoint,
        [
            new("grant_type", "refresh_token"),
            new("client_id", identity.Identity.Target.ClientId),
            new("refresh_token", refreshToken),
        ], ct);

    public static async Task<string> ValidateIdTokenAsync(
        HttpClient client,
        OidcMetadata metadata,
        string token,
        CliIdentity identity,
        string expectedNonce,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (token.Length is 0 or > MaximumIdTokenLength)
            throw new InvalidOperationException("The Identity Broker returned an invalid identity token.");

        var parts = token.Split('.');
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
            throw new InvalidOperationException("The Identity Broker returned an invalid identity token.");

        using var header = ParseBase64Json(parts[0], 4096);
        using var claims = ParseBase64Json(parts[1], MaximumIdTokenLength);
        var algorithm = RequiredString(header.RootElement, "alg", 16);
        var keyId = RequiredString(header.RootElement, "kid", 256);
        if (algorithm != "RS256" || HasProperty(header.RootElement, "crit") ||
            HasProperty(header.RootElement, "b64"))
            throw new InvalidOperationException("The Identity Broker used an unsupported identity-token signature.");

        var signature = DecodeBase64Url(parts[2], 1024);
        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        try
        {
            if (!await VerifySignatureAsync(
                client, metadata.JsonWebKeySetEndpoint, keyId, signingInput, signature, ct)
                .ConfigureAwait(false))
                throw new InvalidOperationException("The Identity Broker identity-token signature is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingInput);
            CryptographicOperations.ZeroMemory(signature);
        }

        var root = claims.RootElement;
        if (!string.Equals(RequiredString(root, "iss", 2048), metadata.Issuer, StringComparison.Ordinal))
            throw new InvalidOperationException("The Identity Broker identity token has an unexpected issuer.");
        var actorId = RequiredString(root, "sub", 256);
        if (!string.Equals(RequiredString(root, "nonce", 256), expectedNonce, StringComparison.Ordinal))
            throw new InvalidOperationException("The Identity Broker identity token has an invalid nonce.");

        var audiences = RequiredAudiences(root);
        var clientId = identity.Target.ClientId;
        if (!audiences.Contains(clientId, StringComparer.Ordinal) ||
            audiences.Count > 1 && !string.Equals(
                OptionalString(root, "azp", 256), clientId, StringComparison.Ordinal) ||
            audiences.Count == 1 && OptionalString(root, "azp", 256) is { } authorizedParty &&
                !string.Equals(authorizedParty, clientId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Identity Broker identity token has an unexpected audience.");

        var issuedAt = RequiredUnixTime(root, "iat");
        var expiresAt = RequiredUnixTime(root, "exp");
        var nowSeconds = now.ToUnixTimeSeconds();
        if (issuedAt > nowSeconds + 60 || expiresAt <= nowSeconds)
            throw new InvalidOperationException("The Identity Broker identity token is not currently valid.");
        if (HasProperty(root, "nbf") && RequiredUnixTime(root, "nbf") > nowSeconds)
            throw new InvalidOperationException("The Identity Broker identity token is not currently valid.");

        return actorId;
    }

    private static async Task<OAuthTokenResponse> ExchangeAsync(
        HttpClient client,
        Uri endpoint,
        IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Content = new FormUrlEncodedContent(fields);

        using var requestCancellation = CreateRequestCancellation(client, ct);
        var requestCt = requestCancellation.Token;
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, requestCt).ConfigureAwait(false);
        EnsureNoRedirect(response, endpoint);
        using var document = await ReadJsonAsync(response, MaximumTokenResponseBytes, requestCt).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = OptionalString(document.RootElement, "error", 128);
            throw new InvalidOperationException(error == "invalid_grant"
                ? "The saved Broker sign-in expired or was revoked; sign in again."
                : $"The Identity Broker rejected the token exchange (HTTP {(int)response.StatusCode}).");
        }

        var root = document.RootElement;
        var accessToken = RequiredString(root, "access_token", MaximumTokenLength);
        var tokenType = RequiredString(root, "token_type", 32);
        var lifetime = RequiredNumber(root, "expires_in");
        if (!string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            lifetime <= 0 || lifetime > MaximumLifetimeSeconds)
            throw new InvalidOperationException("The Identity Broker returned an invalid token response.");

        var refreshToken = OptionalString(root, "refresh_token", MaximumTokenLength);
        var idToken = OptionalString(root, "id_token", MaximumIdTokenLength);
        if (!IsOpaqueToken(accessToken) ||
            refreshToken is not null && !IsOpaqueToken(refreshToken) ||
            idToken is not null && idToken.Any(char.IsControl))
            throw new InvalidOperationException("The Identity Broker returned an invalid token response.");

        return new OAuthTokenResponse(accessToken, refreshToken, idToken, lifetime);
    }

    private static async Task<bool> VerifySignatureAsync(
        HttpClient client,
        Uri endpoint,
        string keyId,
        byte[] signingInput,
        byte[] signature,
        CancellationToken ct)
    {
        using var requestCancellation = CreateRequestCancellation(client, ct);
        var requestCt = requestCancellation.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, requestCt).ConfigureAwait(false);
        EnsureNoRedirect(response, endpoint);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("The Identity Broker signing keys could not be retrieved.");

        using var document = await ReadJsonAsync(response, MaximumMetadataBytes, requestCt).ConfigureAwait(false);
        var keys = RequiredProperty(document.RootElement, "keys");
        if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() is 0 or > 128)
            throw new InvalidOperationException("The Identity Broker signing keys are invalid.");

        JsonElement? selected = null;
        foreach (var key in keys.EnumerateArray())
        {
            if (!string.Equals(OptionalString(key, "kid", 256), keyId, StringComparison.Ordinal))
                continue;
            if (selected is not null)
                throw new InvalidOperationException("The Identity Broker signing key identifier is ambiguous.");
            selected = key;
        }
        if (selected is not { } signingKey ||
            !string.Equals(RequiredString(signingKey, "kty", 16), "RSA", StringComparison.Ordinal) ||
            OptionalString(signingKey, "use", 16) is { } use && use != "sig" ||
            OptionalString(signingKey, "alg", 16) is { } keyAlgorithm && keyAlgorithm != "RS256")
            return false;
        if (HasProperty(signingKey, "key_ops"))
        {
            var keyOps = RequiredProperty(signingKey, "key_ops");
            if (keyOps.ValueKind != JsonValueKind.Array ||
                !keyOps.EnumerateArray().Any(item =>
                    item.ValueKind == JsonValueKind.String && item.GetString() == "verify"))
                return false;
        }

        var modulus = DecodeBase64Url(RequiredString(signingKey, "n", 2048), 1024);
        var exponent = DecodeBase64Url(RequiredString(signingKey, "e", 32), 16);
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
            return rsa.KeySize is >= 2048 and <= 8192 &&
                rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(modulus);
            CryptographicOperations.ZeroMemory(exponent);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        int maximumBytes,
        CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null ||
            !(contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
              contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The Identity Broker returned an unsupported response type.");
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            throw new InvalidOperationException("The Identity Broker response exceeded the size limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes)
                throw new InvalidOperationException("The Identity Broker response exceeded the size limit.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length),
                new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The Identity Broker returned invalid JSON.", exception);
        }
    }

    private static CancellationTokenSource CreateRequestCancellation(HttpClient client, CancellationToken ct)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (client.Timeout != Timeout.InfiniteTimeSpan)
            cancellation.CancelAfter(client.Timeout);
        return cancellation;
    }

    private static JsonDocument ParseBase64Json(string value, int maximumBytes)
    {
        var bytes = DecodeBase64Url(value, maximumBytes);
        try
        {
            return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The Identity Broker returned an invalid identity token.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static Uri RequiredEndpoint(JsonElement root, string property, Uri issuer)
    {
        var value = RequiredString(root, property, 2048);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !SameOrigin(endpoint, issuer))
            throw new InvalidOperationException("The Identity Broker discovery document contains an invalid endpoint.");
        return endpoint;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static void EnsureNoRedirect(HttpResponseMessage response, Uri expected)
    {
        if ((int)response.StatusCode is >= 300 and < 400 ||
            response.RequestMessage?.RequestUri is { } actual && actual != expected)
            throw new InvalidOperationException("The Identity Broker redirected an authentication request.");
    }

    private static JsonElement RequiredProperty(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The Identity Broker response is invalid.");
        JsonElement result = default;
        var count = 0;
        foreach (var item in element.EnumerateObject())
        {
            if (item.NameEquals(property))
            {
                result = item.Value;
                count++;
            }
        }
        if (count != 1)
            throw new InvalidOperationException($"The Identity Broker response is missing or duplicates '{property}'.");
        return result;
    }

    private static bool HasProperty(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.EnumerateObject().Any(item => item.NameEquals(property));

    private static string RequiredString(JsonElement element, string property, int maximumLength)
    {
        var value = RequiredProperty(element, property);
        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } text ||
            text.Length > maximumLength ||
            text.Any(char.IsControl))
            throw new InvalidOperationException($"The Identity Broker response field '{property}' is invalid.");
        return text;
    }

    private static string? OptionalString(JsonElement element, string property, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var count = 0;
        JsonElement value = default;
        foreach (var item in element.EnumerateObject())
        {
            if (item.NameEquals(property))
            {
                count++;
                value = item.Value;
            }
        }
        if (count == 0) return null;
        if (count != 1 || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text || text.Length > maximumLength ||
            text.Any(char.IsControl))
            throw new InvalidOperationException($"The Identity Broker response field '{property}' is invalid.");
        return text;
    }

    private static double RequiredNumber(JsonElement element, string property)
    {
        var value = RequiredProperty(element, property);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number))
            throw new InvalidOperationException($"The Identity Broker response field '{property}' is invalid.");
        return number;
    }

    private static long RequiredUnixTime(JsonElement element, string property)
    {
        var value = RequiredProperty(element, property);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
            throw new InvalidOperationException($"The identity-token field '{property}' is invalid.");
        return number;
    }

    private static List<string> RequiredAudiences(JsonElement root)
    {
        var audience = RequiredProperty(root, "aud");
        if (audience.ValueKind == JsonValueKind.String)
            return [audience.GetString() ?? throw new InvalidOperationException("The identity-token audience is invalid.")];
        if (audience.ValueKind != JsonValueKind.Array || audience.GetArrayLength() is 0 or > 8)
            throw new InvalidOperationException("The identity-token audience is invalid.");

        var values = new List<string>();
        foreach (var item in audience.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                item.GetString() is not { Length: > 0 } value ||
                value.Any(char.IsControl))
                throw new InvalidOperationException("The identity-token audience is invalid.");
            values.Add(value);
        }
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new InvalidOperationException("The identity-token audience is ambiguous.");
        return values;
    }

    private static byte[] DecodeBase64Url(string value, int maximumBytes)
    {
        if (value.Length == 0 || value.Length > maximumBytes * 2 ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new InvalidOperationException("The Identity Broker returned invalid encoded data.");
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new InvalidOperationException("The Identity Broker returned invalid encoded data."),
        };
        try
        {
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length > maximumBytes)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new InvalidOperationException("The Identity Broker returned oversized encoded data.");
            }
            return bytes;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The Identity Broker returned invalid encoded data.", exception);
        }
    }

    private static bool IsOpaqueToken(string value) =>
        value.Length is > 0 and <= MaximumTokenLength &&
        value.All(character => character is > (char)0x20 and < (char)0x7f);
}
