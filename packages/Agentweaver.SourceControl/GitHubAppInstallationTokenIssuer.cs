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

public sealed record GitHubAppInstallationCredential(
    SecretCredential Credential,
    string PermissionDigest);

public sealed class GitHubAppInstallationTokenIssuer
{
    private const string GitHubApiVersion = "2022-11-28";
    private readonly HttpClient _httpClient;
    private readonly long _appId;
    private readonly TimeProvider _timeProvider;

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

    public async Task<GitHubAppInstallationCredential> MintAsync(
        long installationId,
        long repositoryId,
        SecretCredential appPrivateKey,
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
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"app/installations/{installationId.ToString(CultureInfo.InvariantCulture)}/access_tokens");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
            request.Content = JsonContent.Create(new
            {
                repository_ids = new[] { repositoryId },
                permissions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["contents"] = "write",
                    ["pull_requests"] = "write"
                }
            });

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw CreateResponseException(response.StatusCode);

            try
            {
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(
                    content, cancellationToken: cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                var token = root.GetProperty("token").GetString();
                var expiresAt = root.GetProperty("expires_at").GetDateTimeOffset();
                var permissions = ReadPermissions(root.GetProperty("permissions"));
                if (string.IsNullOrEmpty(token) || expiresAt <= _timeProvider.GetUtcNow())
                    throw new JsonException("The GitHub response omitted a usable token or expiry.");

                return new GitHubAppInstallationCredential(
                    new SecretCredential(token, expiresAt, _timeProvider),
                    ComputePermissionDigest(permissions));
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

    private static ImmutableSortedDictionary<string, string> ReadPermissions(JsonElement permissionsElement)
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

    private static string ComputePermissionDigest(ImmutableSortedDictionary<string, string> permissions)
    {
        var canonical = "github-app-permissions-v1\n" +
            string.Join('\n', permissions.Select(permission => $"{permission.Key}:{permission.Value}"));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static SourceControlOperationException CreateResponseException(HttpStatusCode statusCode) =>
        statusCode switch
        {
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

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
