using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Agentweaver.Api.Webhooks;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;

namespace Agentweaver.Api.Endpoints;

/// <summary>
/// Inbound GitHub webhook receiver (issue #53 follow-up): the first REAL external event source wired
/// to the existing event-trigger mechanism (<see cref="WorkflowEventTriggerService.FireEventAsync"/>).
/// The manual <c>POST /api/projects/{id}/workflow-events</c> endpoint (<see cref="WorkflowTriggerEndpoints"/>)
/// remains available for testing/bespoke integrations; this endpoint is the automatic GitHub path.
///
/// <para><b>Routing:</b> GitHub does not know about Agentweaver project ids, so a single webhook
/// endpoint fans a delivery out to every ACTIVE project whose <c>Origin.SourceRepository</c> ("owner/repo")
/// matches the payload's <c>repository.full_name</c> (case-insensitive). For each matching project, the
/// GitHub event type (<c>X-GitHub-Event</c> header, e.g. "push", "pull_request", "issues") is mapped to
/// one or two event trigger names so workflow authors can subscribe at whatever granularity they need:
/// <list type="bullet">
/// <item><c>github.&lt;event&gt;</c> — always fired (e.g. <c>github.issues</c>, <c>github.push</c>).</item>
/// <item><c>github.&lt;event&gt;.&lt;action&gt;</c> — fired in addition when the payload has an
/// <c>action</c> field (e.g. <c>github.issues.opened</c>, <c>github.pull_request.opened</c>). <c>push</c>
/// has no action field, so only the coarse name fires for pushes.</item>
/// </list>
/// A workflow's <c>trigger.event_name</c> (see <see cref="Agentweaver.Api.Workflows.WorkflowTrigger"/>)
/// simply names whichever of these it wants to react to — no schema change was needed.</para>
///
/// <para><b>Security:</b> every delivery's <c>X-Hub-Signature-256</c> HMAC-SHA256 signature is verified
/// against the configured <see cref="GitHubWebhookOptions.Secret"/> (see
/// <see cref="GitHubWebhookSignatureVerifier"/>) BEFORE the body is parsed or trusted in any way.
/// Missing/invalid signatures are rejected (401); an unconfigured secret fails closed (500) rather than
/// silently accepting unverifiable payloads. Unmatched projects/triggers are NOT an error — GitHub
/// fires webhooks for events no workflow cares about constantly, so those cases return 200/204.</para>
/// </summary>
public static class GitHubWebhookEndpoints
{
    /// <summary>Event trigger names are namespaced under this prefix to avoid colliding with future
    /// non-GitHub event sources that might reuse plain event names.</summary>
    public const string EventNamePrefix = "github.";

    public static void MapGitHubWebhookEndpoints(this WebApplication app)
    {
        // POST /api/webhooks/github — GitHub webhook receiver (issue #53 follow-up).
        // Exempt from the bearer-token/org-authorization middleware (see GitHubTokenAuthMiddleware and
        // GitHubOrgAuthorizationMiddleware "/api/webhooks" exemptions): GitHub's webhook delivery has no
        // Agentweaver bearer token to present. The HMAC signature check below IS this endpoint's auth.
        app.MapPost("/api/webhooks/github", async (
            HttpContext httpContext,
            IOptions<GitHubWebhookOptions> options,
            IProjectStore projectStore,
            WorkflowEventTriggerService triggerService,
            ILogger<GitHubWebhookPayload> logger,
            CancellationToken ct) =>
        {
            var secret = options.Value.Secret;
            if (string.IsNullOrWhiteSpace(secret))
            {
                logger.LogError(
                    "GitHub webhook received but GitHubWebhook:Secret is not configured; rejecting " +
                    "(fail-closed) instead of accepting an unverifiable delivery.");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            using var bodyStream = new MemoryStream();
            await httpContext.Request.Body.CopyToAsync(bodyStream, ct).ConfigureAwait(false);
            var rawBody = bodyStream.ToArray();

            var signatureHeader = httpContext.Request.Headers["X-Hub-Signature-256"].ToString();
            if (!GitHubWebhookSignatureVerifier.Verify(secret, rawBody, signatureHeader))
            {
                logger.LogWarning("GitHub webhook signature verification failed; rejecting delivery.");
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            var eventType = httpContext.Request.Headers["X-GitHub-Event"].ToString();
            if (string.IsNullOrWhiteSpace(eventType))
                return Results.BadRequest(new { error = "X-GitHub-Event header is required." });

            var deliveryId = httpContext.Request.Headers["X-GitHub-Delivery"].ToString();

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
            if (string.IsNullOrWhiteSpace(repoFullName))
            {
                // No repository to match against (e.g. a "ping" test delivery GitHub sends when the
                // webhook is first configured). No trigger can possibly match; this is not an error,
                // just nothing to do.
                return Results.NoContent();
            }

            var allProjects = await projectStore.ListAsync(ct).ConfigureAwait(false);
            var matchingProjects = allProjects
                .Where(p => p.State == ProjectState.Active)
                .Where(p => p.Origin.Kind == ProjectOriginKind.FromGitHub)
                .Where(p => string.Equals(p.Origin.SourceRepository, repoFullName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchingProjects.Count == 0)
                return Results.NoContent();

            var eventNames = new List<string> { $"{EventNamePrefix}{eventType}" };
            if (!string.IsNullOrWhiteSpace(payload!.Action))
                eventNames.Add($"{EventNamePrefix}{eventType}.{payload.Action}");

            var results = new List<GitHubWebhookProjectFireResult>();
            foreach (var project in matchingProjects)
            {
                var firedForProject = new List<string>();
                foreach (var eventName in eventNames)
                {
                    var dedupeKey = string.IsNullOrWhiteSpace(deliveryId) ? null : $"{deliveryId}:{eventName}";
                    var fired = await triggerService
                        .FireEventAsync(project, eventName, dedupeKey, ct)
                        .ConfigureAwait(false);
                    firedForProject.AddRange(fired);
                }

                results.Add(new GitHubWebhookProjectFireResult
                {
                    ProjectId = project.Id.ToString(),
                    FiredWorkflowIds = firedForProject,
                });
            }

            return Results.Ok(new GitHubWebhookResponse { Results = results });
        });
    }
}

/// <summary>Per-project outcome of routing one GitHub webhook delivery (issue #53 follow-up).</summary>
public sealed record GitHubWebhookProjectFireResult
{
    [JsonPropertyName("project_id")] public required string ProjectId { get; init; }
    [JsonPropertyName("fired_workflow_ids")] public required IReadOnlyList<string> FiredWorkflowIds { get; init; }
}

/// <summary>Response body after routing a GitHub webhook delivery to every matching project
/// (issue #53 follow-up).</summary>
public sealed record GitHubWebhookResponse
{
    [JsonPropertyName("results")] public required IReadOnlyList<GitHubWebhookProjectFireResult> Results { get; init; }
}
