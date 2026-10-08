using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed record GitHubAppInstallationBindingMetadata(
    string ConnectionId,
    long ConnectionRevision,
    long InstallationId,
    long RepositoryId,
    string RepositoryFullName,
    string DefaultBranch,
    bool IsPrivate,
    string PermissionDigest,
    string SelectionHash);

internal sealed class SourceControlSecretRedemptionOptions
{
    public SourceControlSecretRedemptionOptions(
        string issuer,
        string orchestratorAudience,
        string? brokerAudience,
        string? ownerBaseAddress)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) ||
            issuerUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrWhiteSpace(issuerUri.UserInfo) ||
            !string.IsNullOrEmpty(issuerUri.Query) ||
            !string.IsNullOrEmpty(issuerUri.Fragment))
            throw new ArgumentException("Secret-redemption issuer must be an absolute HTTPS URI.", nameof(issuer));
        Issuer = issuerUri.AbsoluteUri;
        OrchestratorAudience = RequireAudience(orchestratorAudience, nameof(orchestratorAudience));
        if (string.IsNullOrWhiteSpace(brokerAudience) &&
            string.IsNullOrWhiteSpace(ownerBaseAddress))
            return;
        BrokerAudience = RequireAudience(brokerAudience, nameof(brokerAudience));
        if (!Uri.TryCreate(ownerBaseAddress, UriKind.Absolute, out var ownerUri) ||
            ownerUri.Scheme != Uri.UriSchemeHttps ||
            ownerUri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(ownerUri.Query) ||
            !string.IsNullOrEmpty(ownerUri.Fragment) ||
            !string.IsNullOrEmpty(ownerUri.UserInfo))
            throw new ArgumentException(
                "Secret-redemption owner must be one absolute HTTPS origin.", nameof(ownerBaseAddress));
        OwnerBaseAddress = ownerUri;
    }

    public string Issuer { get; }
    public string OrchestratorAudience { get; }
    public string? BrokerAudience { get; }
    public Uri? OwnerBaseAddress { get; }
    public bool IsConfigured => BrokerAudience is not null && OwnerBaseAddress is not null;

    private static string RequireAudience(string? value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value!;
    }
}

internal sealed class SourceControlSecretRedemptionClient(
    HttpClient httpClient,
    SourceControlSecretRedemptionOptions options,
    TimeProvider timeProvider)
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<T> WithCredentialAsync<T>(
        HttpContext context,
        SourceControlAcceptedRunBinding acceptedRun,
        SourceControlCredentialReference credentialReference,
        Func<SecretCredential, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(acceptedRun);
        ArgumentNullException.ThrowIfNull(credentialReference);
        ArgumentNullException.ThrowIfNull(operation);

        ValidateCaller(context, acceptedRun);
        var credential = await RedeemAsync(
            context, acceptedRun, credentialReference, cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateCaller(context, acceptedRun);
            return await operation(credential, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    public Task<T> WithCredentialAsync<T>(
        HttpContext context,
        SourceControlRepositoryPin pin,
        SourceControlCredentialReference? credentialReference,
        Func<SecretCredential, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pin);
        ArgumentNullException.ThrowIfNull(operation);
        if (credentialReference is not null)
            return WithCredentialAsync(
                context, pin.AcceptedRun, credentialReference, operation, cancellationToken);
        if (pin.GitHubAppBinding is not { } binding)
            throw new CoordinationException(
                "source_control_secret_reference_missing", StatusCodes.Status409Conflict);

        return WithGitHubAppCredentialAsync(
            context,
            pin.AcceptedRun,
            binding,
            pin.ProviderRepositoryId,
            pin.Repository.FullName,
            (credential, _, token) => operation(credential, token),
            cancellationToken);
    }

    public Task<T> WithGitHubAppSelectionCredentialAsync<T>(
        HttpContext context,
        SourceControlAcceptedRunBinding acceptedRun,
        string connectionId,
        string selectionCode,
        string expectedRepositoryFullName,
        Func<SecretCredential, GitHubAppInstallationBindingMetadata, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        WithGitHubAppCredentialAsync(
            context,
            acceptedRun,
            connectionId,
            selectionCode,
            binding: null,
            repositoryId: null,
            expectedRepositoryFullName,
            operation,
            cancellationToken);

    public Task<T> WithGitHubAppCredentialAsync<T>(
        HttpContext context,
        SourceControlAcceptedRunBinding acceptedRun,
        SourceControlGitHubAppBinding binding,
        long repositoryId,
        string expectedRepositoryFullName,
        Func<SecretCredential, GitHubAppInstallationBindingMetadata, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (repositoryId <= 0)
            throw new ArgumentOutOfRangeException(nameof(repositoryId));
        return WithGitHubAppCredentialAsync(
            context,
            acceptedRun,
            binding.IdentityConnectionId,
            selectionCode: null,
            binding,
            repositoryId,
            expectedRepositoryFullName,
            operation,
            cancellationToken);
    }

    private async Task<T> WithGitHubAppCredentialAsync<T>(
        HttpContext context,
        SourceControlAcceptedRunBinding acceptedRun,
        string connectionId,
        string? selectionCode,
        SourceControlGitHubAppBinding? binding,
        long? repositoryId,
        string expectedRepositoryFullName,
        Func<SecretCredential, GitHubAppInstallationBindingMetadata, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(acceptedRun);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepositoryFullName);
        ArgumentNullException.ThrowIfNull(operation);
        ValidateCaller(context, acceptedRun);
        var selectionHash = binding?.IdentityRepositorySelectionHash ??
            HashSelectionCode(selectionCode ?? string.Empty);
        var input = new GitHubAppInstallationTokenInput(
            selectionCode,
            binding?.IdentityRepositorySelectionHash,
            connectionId,
            binding?.IdentityConnectionRevision,
            binding?.InstallationId,
            repositoryId,
            binding?.PermissionDigest,
            expectedRepositoryFullName);
        var bearer = CoordinationIdentity.RequireBearer(context);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(options.OwnerBaseAddress!, "/internal/source-control/github-app/installations/token"));
        request.Headers.Authorization = bearer;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = System.Net.Http.Json.JsonContent.Create(input);

        SecretCredential? credential = null;
        try
        {
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException(
                    "source_control_secret_redemption_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new CoordinationException(
                    "source_control_installation_token_unavailable", StatusCodes.Status502BadGateway);

            var result = await ReadAppTokenResponseAsync(response.Content, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var now = timeProvider.GetUtcNow();
            if (result is null ||
                string.IsNullOrWhiteSpace(result.AccessToken) ||
                result.ExpiresAt.Offset != TimeSpan.Zero ||
                result.ExpiresAt <= now ||
                result.ConnectionId != connectionId ||
                result.ConnectionRevision < 1 ||
                result.InstallationId <= 0 ||
                result.RepositoryId <= 0 ||
                !string.Equals(result.RepositoryFullName, expectedRepositoryFullName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.DefaultBranch) ||
                !IsSha256Hash(result.PermissionDigest) ||
                !string.Equals(result.SelectionHash, selectionHash, StringComparison.Ordinal) ||
                binding is not null &&
                    (result.ConnectionRevision != binding.IdentityConnectionRevision ||
                     result.InstallationId != binding.InstallationId ||
                     result.RepositoryId != repositoryId ||
                     !string.Equals(result.PermissionDigest, binding.PermissionDigest, StringComparison.Ordinal)))
                throw new CoordinationException(
                    "source_control_installation_token_contract_invalid", StatusCodes.Status502BadGateway);

            credential = new SecretCredential(result.AccessToken, result.ExpiresAt, timeProvider);
            var metadata = new GitHubAppInstallationBindingMetadata(
                result.ConnectionId,
                result.ConnectionRevision,
                result.InstallationId,
                result.RepositoryId,
                result.RepositoryFullName,
                result.DefaultBranch,
                result.IsPrivate,
                result.PermissionDigest,
                result.SelectionHash);
            ValidateCaller(context, acceptedRun);
            return await operation(credential, metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationException(
                "source_control_installation_token_unavailable", StatusCodes.Status502BadGateway, exception);
        }
        catch (IOException exception)
        {
            throw new CoordinationException(
                "source_control_installation_token_unavailable", StatusCodes.Status502BadGateway, exception);
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "source_control_installation_token_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
        finally
        {
            credential?.Invalidate();
        }
    }

    private async Task<SecretCredential> RedeemAsync(
        HttpContext context,
        SourceControlAcceptedRunBinding acceptedRun,
        SourceControlCredentialReference credentialReference,
        CancellationToken cancellationToken)
    {
        var secretRequest = new SecretRedemptionRequest(
            credentialReference.Secret,
            credentialReference.Purpose,
            acceptedRun.RunId);
        var bearer = CoordinationIdentity.RequireBearer(context);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri(options.OwnerBaseAddress!, "/secrets/redeem"));
        request.Headers.Authorization = bearer;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = System.Net.Http.Json.JsonContent.Create(new SecretRedemptionInput(
            secretRequest.Secret.Id,
            secretRequest.Secret.Version,
            secretRequest.Purpose,
            secretRequest.RunId));

        try
        {
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new CoordinationException(
                    "source_control_secret_redemption_denied", StatusCodes.Status403Forbidden);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new CoordinationException(
                    "source_control_secret_redemption_unavailable", StatusCodes.Status502BadGateway);

            var result = await ReadResponseAsync(response.Content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null ||
                result.SecretId != secretRequest.Secret.Id ||
                result.SecretVersion != secretRequest.Secret.Version ||
                string.IsNullOrEmpty(result.Value) ||
                result.ExpiresAt.Offset != TimeSpan.Zero ||
                result.ExpiresAt <= timeProvider.GetUtcNow())
                throw new CoordinationException(
                    "source_control_secret_redemption_contract_invalid", StatusCodes.Status502BadGateway);
            return new SecretCredential(result.Value, result.ExpiresAt, timeProvider);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationException(
                "source_control_secret_redemption_unavailable", StatusCodes.Status502BadGateway, exception);
        }
        catch (IOException exception)
        {
            throw new CoordinationException(
                "source_control_secret_redemption_unavailable", StatusCodes.Status502BadGateway, exception);
        }
        catch (JsonException exception)
        {
            throw new CoordinationException(
                "source_control_secret_redemption_contract_invalid", StatusCodes.Status502BadGateway, exception);
        }
    }

    private static async Task<SecretRedemptionResult?> ReadResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new JsonException("Secret-redemption response exceeded the permitted size.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new JsonException("Secret-redemption response exceeded the permitted size.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Deserialize<SecretRedemptionResult>(
            buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonOptions);
    }

    private static async Task<GitHubAppInstallationTokenResponse?> ReadAppTokenResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new JsonException("Installation-token response exceeded the permitted size.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new JsonException("Installation-token response exceeded the permitted size.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return JsonSerializer.Deserialize<GitHubAppInstallationTokenResponse>(
            buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonOptions);
    }

    private static string HashSelectionCode(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(value)));

    private static bool IsSha256Hash(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void ValidateCaller(
        HttpContext context,
        SourceControlAcceptedRunBinding acceptedRun)
    {
        if (!options.IsConfigured)
            throw new CoordinationException(
                "source_control_secret_redemption_unconfigured", StatusCodes.Status503ServiceUnavailable);
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        CoordinationIdentity.RequireScopes(context.User);
        var scope = CoordinationIdentity.RequireRunScope(context.User);
        if (actor.Issuer != acceptedRun.Issuer ||
            actor.Subject != acceptedRun.Subject ||
            scope.ProjectId != acceptedRun.ProjectId ||
            scope.RunId != acceptedRun.RunId ||
            !CoordinationIdentity.HasAudience(context.User, options.OrchestratorAudience) ||
            !CoordinationIdentity.HasAudience(context.User, options.BrokerAudience!))
            throw new CoordinationException(
                "source_control_secret_redemption_caller_mismatch", StatusCodes.Status403Forbidden);
    }

    private sealed record SecretRedemptionInput(
        string SecretId,
        string SecretVersion,
        string Purpose,
        string RunId);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record SecretRedemptionResult(
        string SecretId,
        string SecretVersion,
        DateTimeOffset ExpiresAt,
        string Value);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record GitHubAppInstallationTokenResponse(
        string AccessToken,
        DateTimeOffset ExpiresAt,
        string ConnectionId,
        long ConnectionRevision,
        long InstallationId,
        long RepositoryId,
        string RepositoryFullName,
        string DefaultBranch,
        bool IsPrivate,
        string PermissionDigest,
        string SelectionHash);

    private sealed record GitHubAppInstallationTokenInput(
        string? SelectionCode,
        string? SelectionHash,
        string? ConnectionId,
        long? ConnectionRevision,
        long? InstallationId,
        long? RepositoryId,
        string? PermissionDigest,
        string ExpectedRepositoryFullName);
}
