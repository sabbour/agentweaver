using System.Net;
using System.Text.Json;

namespace Agentweaver.Api.Auth;

public enum CopilotAppRegistrationState
{
    Ready,
    ConfigurationUnavailable,
    RegistrationUnavailable,
    RepositoryPermissionsDetected,
}

/// <summary>
/// Reads the public GitHub App registration to enforce the Copilot App's
/// zero-repository-permission boundary without accessing credentials or private keys.
/// </summary>
public sealed class CopilotAppRegistrationService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory)
{
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<CopilotAppRegistrationState> ValidateAsync(CancellationToken ct = default)
    {
        var slug = configuration["Auth:CopilotApp:Slug"];
        if (string.IsNullOrWhiteSpace(slug) || !IsConfigured())
            return CopilotAppRegistrationState.ConfigurationUnavailable;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{(configuration["Auth:CopilotApp:ApiUrl"] ?? "https://api.github.com").TrimEnd('/')}/apps/{Uri.EscapeDataString(slug)}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Agentweaver/1.0");

        try
        {
            using var response = await httpClientFactory.CreateClient("github")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return CopilotAppRegistrationState.RegistrationUnavailable;

            var payload = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("permissions", out var permissions) ||
                permissions.ValueKind != JsonValueKind.Object)
                return CopilotAppRegistrationState.RegistrationUnavailable;

            return HasOnlyMandatoryMetadataReadPermission(permissions)
                ? CopilotAppRegistrationState.Ready
                : CopilotAppRegistrationState.RepositoryPermissionsDetected;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return CopilotAppRegistrationState.RegistrationUnavailable;
        }
    }

    private bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(configuration["Auth:CopilotApp:ClientId"]) &&
        !string.IsNullOrWhiteSpace(configuration["Auth:CopilotApp:ClientSecret"]) &&
        !string.IsNullOrWhiteSpace(configuration["Auth:CopilotApp:CallbackUrl"]);

    private static bool HasOnlyMandatoryMetadataReadPermission(JsonElement permissions)
    {
        // GitHub's public /apps/{slug} endpoint omits implicit metadata: read when no extra permissions exist.
        foreach (var property in permissions.EnumerateObject())
        {
            if (!property.NameEquals("metadata") ||
                property.Value.ValueKind != JsonValueKind.String ||
                !string.Equals(property.Value.GetString(), "read", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > MaximumResponseBytes)
                throw new JsonException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
}

public sealed class CopilotAppRegistrationStartupService(
    CopilotAppRegistrationService registration,
    ILogger<CopilotAppRegistrationStartupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var state = await registration.ValidateAsync(cancellationToken).ConfigureAwait(false);
        if (state == CopilotAppRegistrationState.RepositoryPermissionsDetected)
            throw new InvalidOperationException("Copilot App registration has repository permissions.");
        if (state == CopilotAppRegistrationState.RegistrationUnavailable)
            logger.LogWarning("Copilot App registration validation is unavailable; Copilot binding remains fail-closed.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
