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

            // GitHub always sends the canonical "owner/repo" in repository.full_name, but
            // Project.Origin.SourceRepository is stored inconsistently depending on how the project
            // was created: the "import from GitHub" path (ProjectService.CreateFromGitHubAsync) stores
            // the full HTTPS clone URL, while the "create a new repo" connect path stores "owner/repo".
            // Normalise BOTH sides to canonical "owner/repo" before comparing so a real delivery is
            // matched regardless of which creation path produced the project (issue: event-triggered
            // workflows never fired for URL-form projects).
            var repoFullName = NormalizeRepoFullName(payload?.Repository?.FullName);
            var projectRepo = NormalizeRepoFullName(project.Origin.SourceRepository);
            if (repoFullName is null
                || project.Origin.Kind != ProjectOriginKind.FromGitHub
                || projectRepo is null
                || !string.Equals(projectRepo, repoFullName, StringComparison.Ordinal))
                return Results.NoContent();

            var eventNames = new List<string> { $"{EventNamePrefix}{eventType}" };
            if (!string.IsNullOrWhiteSpace(payload!.Action))
                eventNames.Add($"{EventNamePrefix}{eventType}.{payload.Action}");

            var deliveryId = httpContext.Request.Headers["X-GitHub-Delivery"].ToString();
            var firedWorkflowIds = new List<string>();
            foreach (var eventName in eventNames)
            {
                var dedupeKey = string.IsNullOrWhiteSpace(deliveryId) ? null : $"{deliveryId}:{eventName}";
                var fired = await triggerService.FireEventAsync(project, eventName, dedupeKey, payload, ct).ConfigureAwait(false);
                firedWorkflowIds.AddRange(fired);
            }

            return Results.Ok(new GitHubWebhookResponse
            {
                ProjectId = project.Id.ToString(),
                FiredWorkflowIds = firedWorkflowIds,
            });
        });
    }

    /// <summary>
    /// Reduces a repository reference to canonical lowercase <c>owner/repo</c>. Accepts either the
    /// contract form (<c>owner/repo</c>) or a full HTTPS clone URL
    /// (e.g. <c>https://github.com/owner/repo(.git)</c>): the "import from GitHub" project-creation
    /// path stores the URL form in <see cref="ProjectOrigin.SourceRepository"/>, while GitHub webhook
    /// payloads always send <c>owner/repo</c>. Returns <c>null</c> when a usable owner/repo pair
    /// cannot be extracted, so an unparseable value never accidentally matches.
    /// </summary>
    internal static string? NormalizeRepoFullName(string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return null;
        var value = repo.Trim();

        // Strip scheme + host when a URL form is supplied, keeping just the path.
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            value = uri.AbsolutePath;

        value = value.Trim('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2) return null;

        // The last two path segments are always owner/repo (covers both "owner/repo" and a URL path).
        var owner = segments[^2];
        var name = segments[^1];
        if (owner.Length == 0 || name.Length == 0) return null;
        return $"{owner}/{name}".ToLowerInvariant();
    }
}

public sealed record GitHubWebhookResponse
{
    [JsonPropertyName("project_id")] public required string ProjectId { get; init; }
    [JsonPropertyName("fired_workflow_ids")] public required IReadOnlyList<string> FiredWorkflowIds { get; init; }
}
