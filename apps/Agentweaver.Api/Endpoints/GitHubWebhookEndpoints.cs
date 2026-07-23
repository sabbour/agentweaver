using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Webhooks;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Inbound GitHub webhook receiver. Each project has its own endpoint and HMAC secret, so the
/// project and secret are resolved before the untrusted body is parsed. Trigger names are
/// <c>github.&lt;event&gt;</c> and, when present, <c>github.&lt;event&gt;.&lt;action&gt;</c>.
/// </summary>
public static class GitHubWebhookEndpoints
{
    public const string EventNamePrefix = "github.";

    public static void MapGitHubWebhookEndpoints(this WebApplication app)
    {
        // This route is deliberately anonymous: GitHub cannot supply an Agentweaver bearer token.
        // GitHubTokenAuthMiddleware and GitHubOrgAuthorizationMiddleware exempt only this exact
        // project-scoped webhook shape; HMAC verification below is the route's authentication.
        app.MapPost("/api/projects/{id}/webhooks/github", async (
            HttpContext httpContext,
            string id,
            IProjectStore projectStore,
            ISecretStore secretStore,
            WorkflowEventTriggerService triggerService,
            ILogger<GitHubWebhookPayload> logger,
            CancellationToken ct) =>
        {
            if (!ProjectId.TryParse(id, out var projectId))
                return Results.BadRequest(new { error = "Invalid project id." });

            var project = await projectStore.GetAsync(projectId, ct).ConfigureAwait(false);
            if (project is null)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(project.WebhookSecret))
            {
                logger.LogError("GitHub webhook received for project {ProjectId} without a configured secret.", projectId);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            var secretResult = await secretStore.GetSecretAsync(project.WebhookSecret, ct).ConfigureAwait(false);
            if (!secretResult.Found || string.IsNullOrWhiteSpace(secretResult.Value))
            {
                logger.LogError("GitHub webhook secret is unavailable for project {ProjectId}.", projectId);
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            using var bodyStream = new MemoryStream();
            await httpContext.Request.Body.CopyToAsync(bodyStream, ct).ConfigureAwait(false);
            var rawBody = bodyStream.ToArray();
            var signatureHeader = httpContext.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!GitHubWebhookSignatureVerifier.Verify(secretResult.Value, rawBody, signatureHeader))
            {
                logger.LogWarning("GitHub webhook signature verification failed for project {ProjectId}.", projectId);
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            if (project.State != ProjectState.Active)
                return Results.NoContent();

            var eventType = httpContext.Request.Headers["X-GitHub-Event"].ToString();
            if (string.IsNullOrWhiteSpace(eventType))
                return Results.BadRequest(new { error = "X-GitHub-Event header is required." });

            GitHubWebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(rawBody);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "GitHub webhook payload for event {EventType} was not valid JSON.", eventType);
                return Results.BadRequest(new { error = "Payload is not valid JSON." });
            }

            var repoFullName = payload?.Repository?.FullName;
            if (string.IsNullOrWhiteSpace(repoFullName)
                || project.Origin.Kind != ProjectOriginKind.FromGitHub
                || !string.Equals(project.Origin.SourceRepository, repoFullName, StringComparison.OrdinalIgnoreCase))
                return Results.NoContent();

            var eventNames = new List<string> { $"{EventNamePrefix}{eventType}" };
            if (!string.IsNullOrWhiteSpace(payload!.Action))
                eventNames.Add($"{EventNamePrefix}{eventType}.{payload.Action}");

            var deliveryId = httpContext.Request.Headers["X-GitHub-Delivery"].ToString();
            var firedWorkflowIds = new List<string>();
            foreach (var eventName in eventNames)
            {
                var dedupeKey = string.IsNullOrWhiteSpace(deliveryId) ? null : $"{deliveryId}:{eventName}";
                var fired = await triggerService.FireEventAsync(project, eventName, dedupeKey, ct).ConfigureAwait(false);
                firedWorkflowIds.AddRange(fired);
            }

            return Results.Ok(new GitHubWebhookResponse
            {
                ProjectId = project.Id.ToString(),
                FiredWorkflowIds = firedWorkflowIds,
            });
        });
    }
}

public sealed record GitHubWebhookResponse
{
    [JsonPropertyName("project_id")] public required string ProjectId { get; init; }
    [JsonPropertyName("fired_workflow_ids")] public required IReadOnlyList<string> FiredWorkflowIds { get; init; }
}
