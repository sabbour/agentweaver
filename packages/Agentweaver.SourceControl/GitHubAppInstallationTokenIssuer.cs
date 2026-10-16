using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.SourceControl;

/// <summary>An installation token and the verified permission ceiling returned by GitHub.</summary>
public sealed record GitHubAppInstallationCredential(
    SecretCredential Credential,
    string PermissionDigest,
    bool IssueWriteGranted);

/// <summary>
/// Mints repository-scoped GitHub App installation tokens from a redeemed private key.
/// It validates the granted permission set and invalidates the supplied key after every attempt.
/// </summary>
public sealed class GitHubAppInstallationTokenIssuer
{
    private const string GitHubApiVersion = "2022-11-28";
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient _httpClient;
    private readonly long _appId;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an issuer that can call only the HTTPS api.github.com origin.
    /// </summary>
    /// <param name="httpClient">Configured GitHub API client.</param>
    /// <param name="appId">Positive GitHub App ID used as the JWT issuer.</param>
    /// <param name="timeProvider">Optional clock for JWT and token-expiry validation.</param>
    /// <exception cref="ArgumentException">The client does not target the canonical HTTPS API origin.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The App ID is not positive.</exception>
    public GitHubAppInstallationTokenIssuer(
        HttpClient httpClient,
        long appId,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is not { IsAbsoluteUri: true } address ||
            address.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(address.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            address.AbsolutePath != "/")
            throw new ArgumentException(
                "The GitHub API client must use the HTTPS api.github.com origin.", nameof(httpClient));
        if (appId <= 0)
            throw new ArgumentOutOfRangeException(nameof(appId));

        _appId = appId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Requests a token for one repository with only the selected minimum permissions.</summary>
    private async Task<HttpResponseMessage> SendTokenRequestAsync(
        long installationId,
        long repositoryId,
        string appJwt,
        bool issueWriteRequested,
        CancellationToken cancellationToken)
    {
        var permissions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contents"] = "write",
            ["pull_requests"] = "write"
        };
        if (issueWriteRequested)
            permissions["issues"] = "write";

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"app/installations/{installationId.ToString(CultureInfo.InvariantCulture)}/access_tokens");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        request.Content = JsonContent.Create(new
        {
            repository_ids = new[] { repositoryId },
            permissions
        });
        return await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates the bounded token response and returns a credential with a canonical permission digest.</summary>
    /// <exception cref="SourceControlOperationException">The response is denied, malformed, expired, or exceeds the requested permission set.</exception>
    private async Task<GitHubAppInstallationCredential> ReadCredentialAsync(
        HttpResponseMessage response,
        bool issueWriteRequested,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw CreateResponseException(response.StatusCode, issueWriteRequested);

        try
        {
            using var document = await ReadDocumentAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("token", out var tokenElement) ||
                tokenElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(tokenElement.GetString()) ||
                !root.TryGetProperty("expires_at", out var expiryElement) ||
                expiryElement.ValueKind != JsonValueKind.String ||
                !expiryElement.TryGetDateTimeOffset(out var expiresAt) ||
                !root.TryGetProperty("permissions", out var permissionsElement))
                throw new JsonException("The GitHub installation-token response was incomplete or invalid.");
            var permissions = ReadPermissions(permissionsElement, issueWriteRequested);
            if (expiresAt <= _timeProvider.GetUtcNow())
                throw new JsonException("The GitHub installation-token response was expired.");

            var issueWriteGranted =
                permissions.TryGetValue("issues", out var issuePermission) && issuePermission == "write";
            var credential = new SecretCredential(tokenElement.GetString()!, expiresAt, _timeProvider);
            if (issueWriteRequested && !issueWriteGranted)
            {
                credential.Invalidate();
                throw new SourceControlOperationException(
                    SourceControlFailureCode.CapabilityUnavailable,
                    "GitHub did not grant the requested issues:write permission.",
                    response.StatusCode);
            }

            return new GitHubAppInstallationCredential(
                credential, ComputePermissionDigest(permissions), issueWriteGranted);
        }
        catch (JsonException exception)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.RemoteOutcomeUncertain,
                "GitHub may have minted an installation token, but its response was unusable.",
                response.StatusCode,
                exception);
        }
    }

    /// <summary>
    /// Creates an App JWT, requests a repository-scoped token, and invalidates the redeemed private key.
    /// </summary>
    /// <param name="installationId">Positive GitHub App installation ID.</param>
    /// <param name="repositoryId">Positive numeric repository ID that limits the token.</param>
    /// <param name="appPrivateKey">One-use PEM private-key credential; invalidated after the attempt.</param>
    /// <param name="issueWriteRequested">Whether the optional issues:write grant is required.</param>
    /// <param name="cancellationToken">Cancels the outbound API request.</param>
    /// <returns>A token credential and digest of the permissions actually granted.</returns>
    /// <exception cref="SourceControlOperationException">The key, API response, transport result, or requested grants are invalid.</exception>
    public async Task<GitHubAppInstallationCredential> MintAsync(
        long installationId,
        long repositoryId,
        SecretCredential appPrivateKey,
        bool issueWriteRequested,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(appPrivateKey);
        if (installationId <= 0)
            throw new ArgumentOutOfRangeException(nameof(installationId));
        if (repositoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(repositoryId));

        try
        {
            var appJwt = CreateAppJwt(appPrivateKey);
            using var response = await SendTokenRequestAsync(
                installationId, repositoryId, appJwt, issueWriteRequested, cancellationToken)
                .ConfigureAwait(false);
            return await ReadCredentialAsync(
                response, issueWriteRequested, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.RemoteOutcomeUncertain,
                "The GitHub installation-token mint outcome is uncertain.",
                exception.StatusCode,
                exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.RemoteOutcomeUncertain,
                "The GitHub installation-token mint timed out and its outcome is uncertain.",
                innerException: exception);
        }
        finally
        {
            appPrivateKey.Invalidate();
        }
    }

    /// <summary>Reads a bounded JSON object from the token response.</summary>
    private static async Task<JsonDocument> ReadDocumentAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new JsonException("The GitHub installation-token response exceeded the permitted size.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new JsonException("The GitHub installation-token response exceeded the permitted size.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
        if (document.RootElement.ValueKind == JsonValueKind.Object)
            return document;
        document.Dispose();
        throw new JsonException("The GitHub installation-token response was not a JSON object.");
    }

    /// <summary>Signs a short-lived RS256 GitHub App JWT using the one-use private key.</summary>
    /// <exception cref="SourceControlOperationException">The private key cannot be imported or signed with.</exception>
    private string CreateAppJwt(SecretCredential appPrivateKey)
    {
        var now = _timeProvider.GetUtcNow();
        var issuedAt = now.ToUnixTimeSeconds() - 60;
        var expiresAt = now.AddMinutes(9).ToUnixTimeSeconds();
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            alg = "RS256",
            typ = "JWT"
        }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iat = issuedAt,
            exp = expiresAt,
            iss = _appId.ToString(CultureInfo.InvariantCulture)
        }));
        var signingInput = $"{header}.{payload}";

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(appPrivateKey.GetValue());
            var signature = rsa.SignData(
                Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return $"{signingInput}.{Base64UrlEncode(signature)}";
        }
        catch (CryptographicException exception)
        {
            throw new SourceControlOperationException(
                SourceControlFailureCode.InvalidBinding,
                "The redeemed GitHub App private key is invalid.",
                innerException: exception);
        }
    }

    /// <summary>Validates GitHub's granted permissions against the requested permission ceiling.</summary>
    private static ImmutableSortedDictionary<string, string> ReadPermissions(
        JsonElement permissionsElement,
        bool issueWriteRequested)
    {
        if (permissionsElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("The GitHub response permissions were invalid.");

        var permissions = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var permission in permissionsElement.EnumerateObject())
        {
            if (permission.Value.ValueKind != JsonValueKind.String)
                throw new JsonException("The GitHub response permissions were invalid.");
            var level = permission.Value.GetString();
            var permittedLevel = permission.Name switch
            {
                "contents" or "pull_requests" => level is "read" or "write",
                "issues" => issueWriteRequested && (level is "read" or "write"),
                "metadata" => level == "read",
                _ => false
            };
            if (!permittedLevel || permissions.ContainsKey(permission.Name))
                throw new JsonException("The GitHub response exceeded the requested permission ceiling.");
            permissions.Add(permission.Name, level!);
        }

        if (!permissions.ContainsKey("contents") || !permissions.ContainsKey("pull_requests"))
            throw new JsonException("The GitHub response omitted a requested permission.");

        return permissions.ToImmutable();
    }

    /// <summary>Hashes canonical sorted permission entries for a stable grant identity.</summary>
    private static string ComputePermissionDigest(ImmutableSortedDictionary<string, string> permissions)
    {
        var canonical = "github-app-permissions-v1\n" +
            string.Join('\n', permissions.Select(permission => $"{permission.Key}:{permission.Value}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Maps GitHub HTTP status to a stable Source Control failure category.</summary>
    private static SourceControlOperationException CreateResponseException(
        HttpStatusCode statusCode,
        bool issueWriteRequested) =>
        statusCode switch
        {
            HttpStatusCode.UnprocessableEntity when issueWriteRequested =>
                new SourceControlOperationException(
                    SourceControlFailureCode.CapabilityUnavailable,
                    "GitHub rejected the requested issues:write permission.",
                    statusCode),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new SourceControlOperationException(
                    SourceControlFailureCode.PermissionDenied,
                    "GitHub denied the GitHub App installation-token request.",
                    statusCode),
            HttpStatusCode.NotFound =>
                new SourceControlOperationException(
                    SourceControlFailureCode.NotFound,
                    "The GitHub App installation or repository was not found.",
                    statusCode),
            (HttpStatusCode)429 =>
                new SourceControlOperationException(
                    SourceControlFailureCode.RateLimited,
                    "GitHub rate-limited the installation-token request.",
                    statusCode),
            >= HttpStatusCode.InternalServerError =>
                new SourceControlOperationException(
                    SourceControlFailureCode.RemoteOutcomeUncertain,
                    "GitHub may have minted an installation token, but returned a server error.",
                    statusCode),
            _ =>
                new SourceControlOperationException(
                    SourceControlFailureCode.InvalidRequest,
                    "GitHub rejected the installation-token request.",
                    statusCode)
        };

    /// <summary>Encodes JWT bytes using unpadded base64url.</summary>
    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
