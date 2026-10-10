using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class ProjectsRunSelectionSkillContentTests
{
    private const string Issuer = "https://issuer.test/";
    private const string ActorId = "11111111-1111-1111-1111-111111111111";
    private const string ProjectId = "project-1";
    private const string RunId = "run-1";
    private const string AgentId = "agent-1";
    private const string SkillPath = "/api/projects/project-1/runs/run-1/agents/agent-1/skills";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ReadsExactOrderedPinsAndBytesUsingTheOriginalBearerAndTenant()
    {
        var first = Skill("skill-a");
        var second = Skill("skill-b");
        var selection = Selection(
            Assignment(second, 1), Assignment(first, 0), Assignment(Skill("other-agent"), 0, "agent-2"));
        var handler = new OwnerHandler(selection, [first, second]);
        using var http = new HttpClient(handler);

        var result = await Client(http).ReadAcceptedRunSkillsAsync(
            Context(), Registration(selection), CancellationToken.None);

        Assert.Equal(new[] { first.SkillId, second.SkillId }, result.Select(skill => skill.SkillId));
        Assert.Equal(first.Instructions, result[0].Instructions);
        Assert.Equal(first.Resources[0].Content.ToArray(), result[0].Resources[0].Content.ToArray());
        Assert.Equal(
            new[] { "/api/authorization/context", "/api/projects/project-1/runs/run-1/selection",
                "/api/authorization/context", SkillPath, "/api/authorization/context" },
            handler.Paths);
        Assert.Equal(3, handler.AuthorizationReads);
        Assert.All(handler.Headers, headers =>
        {
            Assert.Equal("Bearer owner-test", headers.Bearer);
            Assert.Equal("tenant-1", headers.Tenant);
        });
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("project-revision")]
    [InlineData("configuration-revision")]
    [InlineData("platform-revision")]
    [InlineData("context-revision")]
    [InlineData("selection-hash")]
    [InlineData("agent")]
    public async Task RejectsRegistrationDriftBeforeReadingSkillBytes(string change)
    {
        var skill = Skill("skill-a");
        var selection = Selection(Assignment(skill, 0));
        var registration = Registration(selection);
        var binding = registration.Binding;
        binding = change switch
        {
            "actor" => binding with { ActorId = "22222222-2222-2222-2222-222222222222" },
            "tenant" => binding with { TenantId = "tenant-2" },
            "project-revision" => binding with { ProjectRevision = 2 },
            "configuration-revision" => binding with { ProjectConfigurationRevision = 3 },
            "platform-revision" => binding with { PlatformRuntimeRevision = 4 },
            "context-revision" => binding with { ContextRevision = "context-2" },
            "selection-hash" => binding with { AcceptedSelectionHash = new string('a', 64) },
            "agent" => binding with { AgentId = "agent-2" },
            _ => throw new ArgumentException("Unknown test change.", nameof(change))
        };
        var handler = new OwnerHandler(selection, [skill]);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadAcceptedRunSkillsAsync(
                Context(), registration with { Binding = binding }, CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);
        Assert.DoesNotContain(SkillPath, handler.Paths);
    }

    [Theory]
    [InlineData("order")]
    [InlineData("missing")]
    [InlineData("revision")]
    [InlineData("content")]
    [InlineData("resource")]
    public async Task RejectsChangedOrIncompleteOwnerContent(string change)
    {
        var first = Skill("skill-a");
        var second = Skill("skill-b");
        var selection = Selection(Assignment(first, 0), Assignment(second, 1));
        ImmutableArray<SkillRuntimeContentV1> returned = change switch
        {
            "order" => [second, first],
            "missing" => [first],
            "revision" => [first with { Revision = 2 }, second],
            "content" => [Skill("skill-a", "Changed instructions."), second],
            "resource" => [first with
            {
                Resources = [first.Resources[0] with { Sha256 = new string('a', 64) }]
            }, second],
            _ => throw new ArgumentException("Unknown test change.", nameof(change))
        };
        var handler = new OwnerHandler(selection, returned);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadAcceptedRunSkillsAsync(Context(), Registration(selection), CancellationToken.None));

        Assert.Equal("runtime_skill_content_invalid", error.Code);
        Assert.Equal(StatusCodes.Status502BadGateway, error.StatusCode);
    }

    [Fact]
    public async Task HistoricalEnabledUnpinnedSkillsCannotReachTheContentOwner()
    {
        var selection = Selection(new { skillId = "legacy", enabled = true, order = 0 });
        var handler = new OwnerHandler(selection, []);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadAcceptedRunSkillsAsync(Context(), Registration(selection), CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, error.StatusCode);
        Assert.DoesNotContain(SkillPath, handler.Paths);
    }

    [Fact]
    public async Task RevocationDuringTheContentWaitPreventsRelease()
    {
        var skill = Skill("skill-a");
        var selection = Selection(Assignment(skill, 0));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new OwnerHandler(selection, [skill])
        {
            BeforeSkills = async token =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
        };
        using var http = new HttpClient(handler);
        var read = Client(http).ReadAcceptedRunSkillsAsync(
            Context(), Registration(selection), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        handler.Allowed = false;
        release.SetResult();

        var error = await Assert.ThrowsAsync<CoordinationException>(() => read);

        Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);
        Assert.Equal(3, handler.AuthorizationReads);
        Assert.Equal("/api/authorization/context", handler.Paths[^1]);
    }

    [Fact]
    public async Task EmptyAssignmentStillRequiresTheOwnerReadAndFreshAuthority()
    {
        var selection = Selection(new { skillId = "disabled", enabled = false, order = 0 });
        var handler = new OwnerHandler(selection, []);
        using var http = new HttpClient(handler);

        var result = await Client(http).ReadAcceptedRunSkillsAsync(
            Context(), Registration(selection), CancellationToken.None);

        Assert.Empty(result);
        Assert.Contains(SkillPath, handler.Paths);
        Assert.Equal(3, handler.AuthorizationReads);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("{}")]
    [InlineData("not-json")]
    public async Task MalformedOwnerContentUsesTheExplicitUpstreamError(string content)
    {
        var skill = Skill("skill-a");
        var selection = Selection(Assignment(skill, 0));
        var handler = new OwnerHandler(selection, [skill]) { RawSkills = content };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadAcceptedRunSkillsAsync(Context(), Registration(selection), CancellationToken.None));

        Assert.Equal("runtime_skill_content_invalid", error.Code);
        Assert.Equal(StatusCodes.Status502BadGateway, error.StatusCode);
    }

    [Fact]
    public async Task OwnerContentWithoutNoStoreIsRejected()
    {
        var skill = Skill("skill-a");
        var selection = Selection(Assignment(skill, 0));
        var handler = new OwnerHandler(selection, [skill]) { SkillNoStore = false };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadAcceptedRunSkillsAsync(Context(), Registration(selection), CancellationToken.None));

        Assert.Equal("runtime_skill_content_invalid", error.Code);
    }

    [Theory]
    [InlineData(403, 403)]
    [InlineData(404, 409)]
    [InlineData(409, 409)]
    [InlineData(503, 502)]
    public async Task OwnerFailuresCannotReturnContent(int ownerStatus, int expectedStatus)
    {
        var skill = Skill("skill-a");
        var selection = Selection(Assignment(skill, 0));
        var handler = new OwnerHandler(selection, [skill]) { SkillStatus = (HttpStatusCode)ownerStatus };
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadAcceptedRunSkillsAsync(Context(), Registration(selection), CancellationToken.None));

        Assert.Equal(expectedStatus, error.StatusCode);
    }

    [Theory]
    [InlineData("revision", 409)]
    [InlineData("order", 502)]
    public async Task MalformedNumericPinsAreRejectedBeforeTheContentRead(string field, int expectedStatus)
    {
        var skill = Skill("skill-a");
        var assignment = new Dictionary<string, object>
        {
            ["skillId"] = skill.SkillId,
            ["enabled"] = true,
            ["order"] = 0,
            ["revision"] = 1,
            ["contentDigest"] = skill.ContentDigest,
            ["agentIds"] = new[] { AgentId }
        };
        assignment[field] = "not-a-number";
        var selection = Selection(assignment);
        var handler = new OwnerHandler(selection, [skill]);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<CoordinationException>(() =>
            Client(http).ReadAcceptedRunSkillsAsync(Context(), Registration(selection), CancellationToken.None));

        Assert.Equal(expectedStatus, error.StatusCode);
        Assert.DoesNotContain(SkillPath, handler.Paths);
    }

    private static ProjectsRunSelectionClient Client(HttpClient http) =>
        new(http, new OrchestratorOptions(
            Issuer, "orchestrator-api", "https://projects.test/", "projects-api", string.Empty, string.Empty),
            new NoRemoteSnapshots());

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer owner-test";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", ActorId),
            new Claim("aud", "projects-api"),
            new Claim("project_id", ProjectId),
            new Claim("run_id", RunId),
            new Claim("scope", "api.read projects.orchestrator")
        ], "test"));
        return context;
    }

    private static RuntimeRegistration Registration(string selection) =>
        new(Guid.NewGuid(), 1, new RuntimeBinding(
            Issuer, ActorId, "tenant-1", ProjectId, RunId, "session-1", AgentId, "turn-1",
            1, 2, 3, "context-1",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selection))),
            4, "environment-1", "placement-1", 1, "profile-1",
            new Uri("https://agent.test/configure"), new Uri("https://agent.test/observe"))
        {
            EnvironmentCurrentFencingGeneration = 1,
            EnvironmentProviderFencingGeneration = 1
        }, RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddHours(1));

    private static object Assignment(SkillRuntimeContentV1 skill, int order, string agentId = AgentId) =>
        new { skill.SkillId, enabled = true, order, skill.Revision, skill.ContentDigest, agentIds = new[] { agentId } };

    private static string Selection(params object[] skills) =>
        JsonSerializer.Serialize(new
        {
            projectId = ProjectId,
            runId = RunId,
            projectRevision = 1,
            projectConfigurationRevision = 2,
            platformRuntimeRevision = 3,
            contextRevision = "context-1",
            projectConfiguration = new { casting = new[] { new { agentId = AgentId } }, skills }
        }, JsonOptions);

    private static SkillRuntimeContentV1 Skill(string id, string instructions = "Exact pinned instructions.")
    {
        var bytes = ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("Exact resource bytes."));
        ImmutableArray<SkillRuntimeContentResourceV1> resources =
            [new("guide.txt", bytes, Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan())))];
        return new(id, 1, "PinnedSkill", "Pinned skill description.", instructions,
            SkillRuntimeContentContract.ComputeContentDigest(
                "PinnedSkill", "Pinned skill description.", instructions, resources), resources);
    }

    private sealed class NoRemoteSnapshots : IReviewedRemoteToolSnapshotResolver
    {
        public Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
            ReviewedRemoteToolSnapshotReference reference, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The test selection has no remote snapshots.");
    }

    private sealed class OwnerHandler(string selection, ImmutableArray<SkillRuntimeContentV1> skills)
        : HttpMessageHandler
    {
        public bool Allowed { get; set; } = true;
        public int AuthorizationReads { get; private set; }
        public List<string> Paths { get; } = [];
        public List<(string? Bearer, string? Tenant)> Headers { get; } = [];
        public Func<CancellationToken, Task>? BeforeSkills { get; init; }
        public string? RawSkills { get; init; }
        public bool SkillNoStore { get; init; } = true;
        public HttpStatusCode SkillStatus { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            Headers.Add((request.Headers.Authorization?.ToString(),
                request.Headers.GetValues("X-Agentweaver-Tenant").Single()));
            string json;
            if (path == "/api/authorization/context")
            {
                AuthorizationReads++;
                json = JsonSerializer.Serialize(new ProjectsAuthorizationContext(
                    1, Issuer, ActorId, "tenant-1", 1, ProjectId, RunId,
                    [new ProjectsAuthority("project", ProjectId, Allowed
                        ? [new ProjectsPermissionGrant("readRunSelection", 1)]
                        : [])]), JsonOptions);
            }
            else if (path == SkillPath)
            {
                if (BeforeSkills is not null)
                    await BeforeSkills(cancellationToken);
                json = RawSkills ?? JsonSerializer.Serialize(skills, JsonOptions);
            }
            else if (path == "/api/projects/project-1/runs/run-1/selection")
                json = selection;
            else
                throw new InvalidOperationException("Unexpected owner route.");
            return new HttpResponseMessage(path == SkillPath ? SkillStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
                Headers = { CacheControl = new CacheControlHeaderValue
                    { NoStore = path != SkillPath || SkillNoStore } }
            };
        }
    }
}
