using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Webhooks;

/// <summary>
/// HTTP integration tests for the project-scoped GitHub webhook receiver. They prove that a project
/// is selected before its secret is verified, and exercise the real workflow trigger path.
/// </summary>
public sealed class GitHubWebhookEndpointsTests : IClassFixture<GitHubWebhookWebApplicationFactory>
{
    private readonly GitHubWebhookWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private const string WebhookSecret = "webhook-test-secret-99999";

    public GitHubWebhookEndpointsTests(GitHubWebhookWebApplicationFactory factory)
    {
        _factory = factory;
        // GitHub deliveries authenticate by HMAC rather than an Agentweaver bearer token.
        _client = factory.CreateClient();
    }

    /// <summary>Unique per-test repo name: the class fixture's project store is shared across all
    /// tests in this class, so a fixed repo name would let projects seeded by other tests also match
    /// a delivery's repository and pollute the "results" array.</summary>
    private static string NewRepoFullName() => $"acme/demo-repo-{Guid.NewGuid():N}";

    private const string IssueOpenedTriggerYaml = """
        id: on-issue-opened
        name: On Issue Opened
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "Triage the new issue."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done

        trigger:
          type: event
          event_name: github.issues.opened
        """;

    private const string PushTriggerYaml = """
        id: on-push
        name: On Push
        start: work
        nodes:
          - id: work
            type: prompt
            label: Work
            role: backend-engineer
            prompt: "React to the push."
          - id: done
            type: terminal
            label: Done
            role: plumbing
        edges:
          - from: work
            to: done

        trigger:
          type: event
          event_name: github.push
        """;

    private async Task<(ProjectId ProjectId, string WorkingDirectory, string RepoFullName)> SeedProjectAsync(
        string workflowYaml, string? repoFullName = null)
    {
        repoFullName ??= NewRepoFullName();
        var workingDir = _factory.NewWorkingDirectory();
        Directory.CreateDirectory(Path.Combine(workingDir, ".agentweaver", "workflows"));
        await File.WriteAllTextAsync(
            Path.Combine(workingDir, ".agentweaver", "workflows", "trigger.yaml"), workflowYaml);

        var project = MakeProject() with
        {
            WorkingDirectory = workingDir,
            Origin = ProjectOrigin.FromGitHub(repoFullName),
            WebhookSecret = $"github-webhook:{Guid.NewGuid():N}",
        };

        using var scope = _factory.Services.CreateScope();
        var projectStore = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var secretStore = scope.ServiceProvider.GetRequiredService<ISecretStore>();
        await secretStore.SetSecretAsync(project.WebhookSecret, WebhookSecret);
        await projectStore.InsertAsync(project);

        return (project.Id, workingDir, repoFullName);
    }

    private async Task<IReadOnlyList<BacklogTask>> ListBacklogAsync(ProjectId projectId)
    {
        using var scope = _factory.Services.CreateScope();
        var backlogStore = scope.ServiceProvider.GetRequiredService<IBacklogTaskStore>();
        return await backlogStore.ListByProjectAsync(projectId);
    }

    private static HttpRequestMessage BuildRequest(
        ProjectId projectId, string eventType, byte[] body, string? signature, string? deliveryId = "delivery-1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/webhooks/github")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-GitHub-Event", eventType);
        if (deliveryId is not null) request.Headers.Add("X-GitHub-Delivery", deliveryId);
        if (signature is not null) request.Headers.Add("X-Hub-Signature-256", signature);
        return request;
    }

    private static string Sign(byte[] body) => Sign(WebhookSecret, body);

    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private static byte[] IssuesPayload(string repoFullName, string action = "opened") =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            action,
            repository = new { full_name = repoFullName },
        }));

    private static byte[] PushPayload(string repoFullName) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            repository = new { full_name = repoFullName },
        }));

    // ── Signature verification ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MissingSignatureHeader_Returns401()
    {
        var (projectId, _, repo) = await SeedProjectAsync(IssueOpenedTriggerYaml);
        var body = IssuesPayload(repo);
        var response = await _client.SendAsync(BuildRequest(projectId, "issues", body, signature: null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InvalidSignature_Returns401()
    {
        var (projectId, _, repo) = await SeedProjectAsync(IssueOpenedTriggerYaml);
        var body = IssuesPayload(repo);
        var response = await _client.SendAsync(BuildRequest(projectId, "issues", body, signature: "sha256=" + new string('0', 64)));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongSecretSignature_Returns401()
    {
        var (projectId, _, repo) = await SeedProjectAsync(IssueOpenedTriggerYaml);
        var body = IssuesPayload(repo);
        var wrongSignature = Sign("not-the-configured-secret", body);
        var response = await _client.SendAsync(BuildRequest(projectId, "issues", body, wrongSignature));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ValidSignature_MissingEventTypeHeader_Returns400()
    {
        var (projectId, _, repo) = await SeedProjectAsync(IssueOpenedTriggerYaml);
        var body = IssuesPayload(repo);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/webhooks/github")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Hub-Signature-256", Sign(body));
        // Deliberately omit X-GitHub-Event.

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Event-type routing + trigger firing ─────────────────────────────────────────────────────

    [Fact]
    public async Task IssuesOpened_MatchingProjectAndTrigger_FiresWorkflow()
    {
        var (projectId, _, repoFullName) = await SeedProjectAsync(IssueOpenedTriggerYaml);
        var body = IssuesPayload(repoFullName, action: "opened");
        var response = await _client.SendAsync(BuildRequest(projectId, "issues", body, Sign(body)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("project_id").GetString().Should().Be(projectId.ToString());
        json.GetProperty("fired_workflow_ids").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("on-issue-opened");

        var tasks = await ListBacklogAsync(projectId);
        tasks.Should().ContainSingle();
        tasks.Single().WorkflowOverrideId.Should().Be("on-issue-opened");
    }

    [Fact]
    public async Task IssuesClosed_ActionSpecificTrigger_DoesNotFireOpenedWorkflow()
    {
        var (projectId, _, repoFullName) = await SeedProjectAsync(IssueOpenedTriggerYaml);
        var body = IssuesPayload(repoFullName, action: "closed");
        var response = await _client.SendAsync(BuildRequest(projectId, "issues", body, Sign(body)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ListBacklogAsync(projectId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Push_MatchingProjectAndTrigger_FiresWorkflow()
    {
        var (projectId, _, repoFullName) = await SeedProjectAsync(PushTriggerYaml);
        var body = PushPayload(repoFullName);
        var response = await _client.SendAsync(BuildRequest(projectId, "push", body, Sign(body)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tasks = await ListBacklogAsync(projectId);
        tasks.Should().ContainSingle();
        tasks.Single().WorkflowOverrideId.Should().Be("on-push");
    }

    [Fact]
    public async Task RetriedDelivery_SameDeliveryId_DoesNotDoubleFire()
    {
        var (projectId, _, repoFullName) = await SeedProjectAsync(PushTriggerYaml);
        var body = PushPayload(repoFullName);

        var first = await _client.SendAsync(BuildRequest(projectId, "push", body, Sign(body), deliveryId: "delivery-abc"));
        var second = await _client.SendAsync(BuildRequest(projectId, "push", body, Sign(body), deliveryId: "delivery-abc"));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListBacklogAsync(projectId)).Should().ContainSingle(
            because: "a retried GitHub delivery (same X-GitHub-Delivery id) must not double-fire the workflow");
    }

    // ── Graceful handling of unmatched projects/triggers ────────────────────────────────────────

    [Fact]
    public async Task NoRepositoryInPayload_ReturnsNoContent()
    {
        var (projectId, _, _) = await SeedProjectAsync(IssueOpenedTriggerYaml);
        var body = Encoding.UTF8.GetBytes("""{"zen":"Keep it logically awesome."}""");
        var response = await _client.SendAsync(BuildRequest(projectId, "ping", body, Sign(body)));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task NoProjectMatchesRepository_ReturnsNoContent()
    {
        var (projectId, _, _) = await SeedProjectAsync(IssueOpenedTriggerYaml);
        var body = IssuesPayload("some-org/unrelated-repo-" + Guid.NewGuid().ToString("N"));
        var response = await _client.SendAsync(BuildRequest(projectId, "issues", body, Sign(body)));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task MatchingProject_NoMatchingTrigger_ReturnsOkWithEmptyFiredList()
    {
        var (projectId, _, repoFullName) = await SeedProjectAsync(IssueOpenedTriggerYaml);
        // "pull_request" has no trigger declared by the seeded workflow.
        var prBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            action = "opened",
            repository = new { full_name = repoFullName },
        }));
        var response = await _client.SendAsync(BuildRequest(projectId, "pull_request", prBody, Sign(prBody)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("fired_workflow_ids").EnumerateArray().Should().BeEmpty();
        (await ListBacklogAsync(projectId)).Should().BeEmpty();
    }
}
