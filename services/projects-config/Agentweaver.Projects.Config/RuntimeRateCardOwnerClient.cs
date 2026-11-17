using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Projects.Config;

internal interface IRuntimeRateCardOwnerClient
{
    Task<bool> VerifyAsync(string id, string version, CancellationToken cancellationToken);
}

internal sealed class RuntimeRateCardOwnerClient : IRuntimeRateCardOwnerClient
{
    internal const string Route = "/internal/runtime-rate-cards";
    internal const int MaximumResponseBytes = 256 * 1024;
    internal static readonly TimeSpan OwnerTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;
    private readonly PlatformComponentCollectorOptions _options;
    private readonly Func<CancellationToken, Task<string?>> _readToken;
    private readonly TimeSpan _timeout;

    public RuntimeRateCardOwnerClient(HttpClient httpClient, PlatformComponentCollectorOptions options)
        : this(httpClient, options, cancellationToken => ReadTokenAsync(options.TokenFile, cancellationToken), OwnerTimeout)
    {
    }

    internal RuntimeRateCardOwnerClient(
        HttpClient httpClient,
        PlatformComponentCollectorOptions options,
        Func<CancellationToken, Task<string?>> readToken,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(readToken);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _httpClient = httpClient;
        _options = options;
        _readToken = readToken;
        _timeout = timeout;
    }

    public async Task<bool> VerifyAsync(
        string id,
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            RuntimeRateCardReadContract.ValidateIdentity(id, version);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (!_options.Owners.TryGetValue(PlatformComponentOwners.EventsAndSessions, out var ownerAddress) ||
            _options.TokenFile is null)
            return false;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var token = (await _readToken(timeout.Token).ConfigureAwait(false))?.Trim();
            if (string.IsNullOrEmpty(token))
                return false;

            var path = $"{Route}/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ownerAddress, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK ||
                response.Headers.ETag is not { IsWeak: false } etag ||
                response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return false;

            var payload = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var rateCard = RuntimeRateCardReadContract.Deserialize(payload);
            if (rateCard is null)
                return false;
            RuntimeRateCardReadContract.Validate(rateCard, id, version);
            var canonical = Encoding.UTF8.GetBytes(RuntimeRateCardReadContract.SerializeCanonical(rateCard));
            if (!payload.AsSpan().SequenceEqual(canonical))
                return false;

            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            return string.Equals(etag.Tag, $"\"{hash}\"", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or JsonException or
                InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (bounded.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException("The Events rate-card response is too large.");
            bounded.Write(buffer, 0, read);
        }
        return bounded.ToArray();
    }

    private static async Task<string?> ReadTokenAsync(
        string? tokenFile,
        CancellationToken cancellationToken)
    {
        if (tokenFile is null)
            return null;
        return await File.ReadAllTextAsync(tokenFile, cancellationToken).ConfigureAwait(false);
    }
}
