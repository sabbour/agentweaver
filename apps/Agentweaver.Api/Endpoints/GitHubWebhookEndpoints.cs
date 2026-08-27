using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Webhooks;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>Receives Repo App deliveries authenticated before JSON parsing or any routing.</summary>
public static class GitHubWebhookEndpoints
{
    public const string RepoAppWebhookPath = "/api/github/webhooks/repo-app";
    public const string EventNamePrefix = "github.";
    private const int DefaultBodyLimitBytes = 1_048_576;

    public static void MapGitHubWebhookEndpoints(this WebApplication app)
    {
        app.MapPost(RepoAppWebhookPath, async (
            HttpContext httpContext,
            IConfiguration configuration,
            ISecretStore secretStore,
            MemoryDbContext db,
            IProjectStore projectStore,
            WorkflowEventTriggerService triggerService,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var bodyLimit = Math.Clamp(
                configuration.GetValue<int?>("Auth:RepoApp:WebhookMaxBodyBytes") ?? DefaultBodyLimitBytes,
                1, DefaultBodyLimitBytes);
            if (httpContext.Request.ContentLength is long contentLength && contentLength > bodyLimit)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            var timeoutSeconds = Math.Clamp(
                configuration.GetValue<int?>("Auth:RepoApp:WebhookVerificationTimeoutSeconds") ?? 5, 1, 10);
            byte[] rawBody;
            IReadOnlyList<string> secrets;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                rawBody = await ReadBoundedBodyAsync(httpContext.Request.Body, bodyLimit, timeout.Token).ConfigureAwait(false);
                secrets = await ReadWebhookSecretsAsync(configuration, secretStore, timeout.Token).ConfigureAwait(false);
            }
            catch (WebhookBodyLimitExceededException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.StatusCode(StatusCodes.Status408RequestTimeout);
            }

            var deliveryId = httpContext.Request.Headers["X-GitHub-Delivery"].ToString();
            var signature = httpContext.Request.Headers["X-Hub-Signature-256"].ToString();
            // Verify both configured keys before deciding so rotation does not create a key-selection oracle.
            var signatureValid = secrets.Count != 0 &&
                secrets.Aggregate(false, (matched, secret) =>
                {
                    var thisSecretMatches = GitHubWebhookSignatureVerifier.Verify(secret, rawBody, signature);
                    return matched | thisSecretMatches;
                });
            if (!signatureValid)
            {
                logger.LogWarning("Repo App webhook signature rejected for delivery {DeliveryId}; category={ReasonCategory}",
                    SafeDeliveryId(deliveryId), "signature_invalid");
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var eventType = httpContext.Request.Headers["X-GitHub-Event"].ToString();
            if (string.IsNullOrWhiteSpace(deliveryId) || string.IsNullOrWhiteSpace(eventType))
                return Results.BadRequest(new { error = "Required GitHub delivery headers are missing." });

            GitHubWebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(rawBody);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Webhook payload is invalid." });
            }
            if (payload is null)
                return Results.BadRequest(new { error = "Webhook payload is invalid." });

            var lifecycle = new RepoAppInstallationLifecycleService(db);
            var result = await lifecycle.ProcessAsync(deliveryId, eventType, payload, ct).ConfigureAwait(false);
            if (!result.Claimed)
                return await lifecycle.IsCompletedAsync(deliveryId, ct).ConfigureAwait(false)
                    ? Results.NoContent()
                    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            if (payload.Installation?.Id is not > 0 || payload.Repository?.Id is not > 0)
            {
                if (!await CompleteDeliveryAsync(lifecycle, deliveryId, ct).ConfigureAwait(false))
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                return Results.NoContent();
            }

            var eventNames = new List<string> { $"{EventNamePrefix}{eventType}" };
            if (!string.IsNullOrWhiteSpace(payload.Action))
                eventNames.Add($"{EventNamePrefix}{eventType}.{payload.Action}");

            var firedWorkflowIds = new List<string>();
            try
            {
                foreach (var projectIdText in result.ProjectIds.Distinct(StringComparer.Ordinal))
                {
                    if (!ProjectId.TryParse(projectIdText, out var projectId))
                        continue;
                    var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
                    if (project is null || project.State != ProjectState.Active)
                        continue;
                    foreach (var eventName in eventNames)
                    {
                        var fired = await triggerService.FireEventAsync(project, eventName, $"{deliveryId}:{eventName}", payload, ct)
                            .ConfigureAwait(false);
                        firedWorkflowIds.AddRange(fired);
                    }
                }
            }
            catch (Exception)
            {
                await lifecycle.ReleaseAsync(deliveryId, CancellationToken.None).ConfigureAwait(false);
                logger.LogWarning("Repo App webhook processing failed for delivery {DeliveryId}; category={ReasonCategory}",
                    SafeDeliveryId(deliveryId), "processing_failed");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!await CompleteDeliveryAsync(lifecycle, deliveryId, ct).ConfigureAwait(false))
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            return Results.Ok(new GitHubWebhookResponse { Duplicate = false, FiredWorkflowIds = firedWorkflowIds });
        }).AllowAnonymous();
    }

    private static async Task<bool> CompleteDeliveryAsync(
        RepoAppInstallationLifecycleService lifecycle, string deliveryId, CancellationToken ct)
    {
        try
        {
            return await lifecycle.CompleteAsync(deliveryId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await lifecycle.ReleaseAsync(deliveryId, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    private static async Task<IReadOnlyList<string>> ReadWebhookSecretsAsync(
        IConfiguration configuration, ISecretStore secretStore, CancellationToken ct)
    {
        var currentName = configuration["Auth:RepoApp:WebhookSecretName"];
        var previousName = configuration["Auth:RepoApp:PreviousWebhookSecretName"];
        var previousExpires = DateTimeOffset.TryParse(configuration["Auth:RepoApp:PreviousWebhookSecretExpiresAt"],
            out var parsedExpiry) && parsedExpiry > DateTimeOffset.UtcNow;
        var names = new[] { currentName, previousExpires ? previousName : null }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        var values = await Task.WhenAll(names.Select(async name => await secretStore.GetSecretAsync(name, ct).ConfigureAwait(false)));
        return values.Where(x => x.Found && !string.IsNullOrWhiteSpace(x.Value)).Select(x => x.Value!).ToArray();
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(Stream stream, int limit, CancellationToken ct)
    {
        await using var memory = new MemoryStream(Math.Min(limit, 81920));
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + read > limit)
                throw new WebhookBodyLimitExceededException();
            await memory.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    private static string SafeDeliveryId(string deliveryId) =>
        Guid.TryParse(deliveryId, out var parsed) ? parsed.ToString("D") : "invalid";

    private sealed class WebhookBodyLimitExceededException : Exception;
}

public sealed record GitHubWebhookResponse
{
    [JsonPropertyName("duplicate")] public required bool Duplicate { get; init; }
    [JsonPropertyName("fired_workflow_ids")] public required IReadOnlyList<string> FiredWorkflowIds { get; init; }
}
