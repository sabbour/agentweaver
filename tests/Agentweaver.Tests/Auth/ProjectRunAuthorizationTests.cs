using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Auth;

public sealed class ProjectRunAuthorizationTests : IClassFixture<EntraWebApplicationFactory>
{
    private const string LinkedOwnerOid = "10000000-0000-0000-0000-000000000001";
    private const string UnlinkedOwnerOid = "10000000-0000-0000-0000-000000000002";
    private const string ViewerOid = "10000000-0000-0000-0000-000000000003";
    private const string ContributorOid = "10000000-0000-0000-0000-000000000004";
    private const string OtherProjectOwnerOid = "10000000-0000-0000-0000-000000000005";
    private const string VictimOwnerOid = "10000000-0000-0000-0000-000000000006";
    private const string LinkedGitHubLogin = "linked-run-owner";

    private readonly EntraWebApplicationFactory _factory;

    public ProjectRunAuthorizationTests(EntraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ProjectOwner_CanReadBackgroundRun_RegardlessOfSubmittingIdentity()
    {
        var projectId = await CreateProjectAsync(UnlinkedOwnerOid);
        var runId = await InsertRunAsync(projectId, UnlinkedOwnerOid);

        using var owner = CreateEntraClient(UnlinkedOwnerOid, PlatformRoles.Viewer);
        var response = await owner.GetAsync($"/api/runs/{runId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OwnerOfDifferentProject_IsForbidden_EvenWhenSubmittingIdentityMatches()
    {
        await CreateProjectAsync(OtherProjectOwnerOid);
        var victimProjectId = await CreateProjectAsync(VictimOwnerOid);
        var runId = await InsertRunAsync(victimProjectId, OtherProjectOwnerOid);

        using var otherProjectOwner = CreateEntraClient(OtherProjectOwnerOid, PlatformRoles.Viewer);
        var response = await otherProjectOwner.GetAsync($"/api/runs/{runId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("", HttpStatusCode.OK)]
    [InlineData("/events", HttpStatusCode.OK)]
    [InlineData("/graph", HttpStatusCode.OK)]
    [InlineData("/history", HttpStatusCode.Conflict)]
    [InlineData("/stream", HttpStatusCode.OK)]
    [InlineData("/files", HttpStatusCode.OK)]
    [InlineData("/workspace", HttpStatusCode.OK)]
    [InlineData("/children", HttpStatusCode.OK)]
    [InlineData("/sandbox/port-forward", HttpStatusCode.OK)]
    public async Task ProjectViewer_CanInspectRunAcrossReadEndpoints(string suffix, HttpStatusCode expectedStatus)
    {
        var projectId = await CreateProjectAsync(LinkedOwnerOid, (ViewerOid, ProjectRole.Viewer));
        var runId = await InsertRunAsync(projectId, LinkedGitHubLogin);
        _factory.Services.GetRequiredService<RunStreamStore>()
            .Create(runId, LinkedGitHubLogin)
            .MarkCompleted();

        using var viewer = CreateEntraClient(ViewerOid, PlatformRoles.Viewer);
        var response = await viewer.GetAsync($"/api/runs/{runId}{suffix}");

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task ProjectViewer_CannotArchiveRun()
    {
        var projectId = await CreateProjectAsync(LinkedOwnerOid, (ViewerOid, ProjectRole.Viewer));
        var runId = await InsertRunAsync(projectId, LinkedGitHubLogin);

        using var viewer = CreateEntraClient(ViewerOid, PlatformRoles.Viewer);
        var response = await viewer.PostAsync($"/api/runs/{runId}/archive", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetRunAsync(runId)).ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task ProjectContributor_CanArchiveRun()
    {
        var projectId = await CreateProjectAsync(
            LinkedOwnerOid,
            (ContributorOid, ProjectRole.Contributor));
        var runId = await InsertRunAsync(projectId, LinkedGitHubLogin);

        using var contributor = CreateEntraClient(ContributorOid, PlatformRoles.Contributor);
        var response = await contributor.PostAsync($"/api/runs/{runId}/archive", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetRunAsync(runId)).ArchivedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task InternalService_CannotUseOrdinaryProjectRunEndpoints()
    {
        var projectId = await CreateProjectAsync(VictimOwnerOid);
        var runId = await InsertRunAsync(projectId, "unrelated-submitting-user");
        using var internalService = _factory.CreateClient();
        internalService.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "internal-test-api-key");

        var requests = new[]
        {
            new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{runId}"),
            new HttpRequestMessage(HttpMethod.Get, $"/api/runs/{runId}/events"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/archive"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/cancel"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/retry"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/sandbox/port-forward")
            {
                Content = JsonContent.Create(new { targetPort = 3000 }),
            },
            new HttpRequestMessage(HttpMethod.Post, $"/api/runs/{runId}/sandbox/preview-approvals/request-id/retry"),
        };

        foreach (var request in requests)
        {
            using (request)
            using (var response = await internalService.SendAsync(request))
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden, request.RequestUri!.ToString());
        }
        (await GetRunAsync(runId)).ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task InternalService_CanInvokeExplicitProjectRunPreviewCallback()
    {
        var projectId = await CreateProjectAsync(VictimOwnerOid);
        var runId = await InsertRunAsync(projectId, "unrelated-submitting-user");
        _factory.Services.GetRequiredService<IRunOptionsStore>().SetAutoApproveTools(runId, true);
        using var internalService = _factory.CreateClient();
        internalService.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "internal-test-api-key");

        var response = await internalService.PostAsJsonAsync(
            $"/api/runs/{runId}/sandbox/preview",
            new { target_port = 3000 });

        response.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "the callback must pass authorization before the fixture reports that no sandbox pod is registered");
    }

    [Fact]
    public async Task InternalService_DoesNotBypassNullProjectRunOwnership()
    {
        var runId = await InsertRunAsync(projectId: null, "unrelated-submitting-user");
        using var internalService = _factory.CreateClient();
        internalService.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "internal-test-api-key");

        var response = await internalService.GetAsync($"/api/runs/{runId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NullProjectRun_InternalServiceRequiresExplicitCallbackOptIn()
    {
        var runId = await InsertRunAsync(projectId: null, "unrelated-submitting-user");
        var context = new DefaultHttpContext
        {
            RequestServices = _factory.Services,
        };
        context.User = CallerContextClaimsAdapter.ToPrincipal(
            new CallerContext { User = ProjectAuthorization.InternalServiceUser },
            AgentweaverAuthenticationSchemes.InternalServiceKey,
            isInternalService: true);

        var result = await EndpointHelpers.RequireRunAccessAsync(
            context,
            await GetRunAsync(runId),
            ProjectRole.Contributor,
            CancellationToken.None,
            allowInternalService: true);

        result.Should().BeNull();
    }

    [Fact]
    public async Task NullProjectRun_PreservesSubmittingPrincipalOwnership()
    {
        var runId = await InsertRunAsync(projectId: null, UnlinkedOwnerOid);
        using var owner = CreateEntraClient(UnlinkedOwnerOid, PlatformRoles.Viewer);

        var response = await owner.GetAsync($"/api/runs/{runId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MissingPersistedProject_DoesNotFallBackToSubmittingIdentity()
    {
        var runId = await InsertRunAsync(ProjectId.New(), UnlinkedOwnerOid);
        using var caller = CreateEntraClient(UnlinkedOwnerOid, PlatformRoles.Viewer);

        var response = await caller.GetAsync($"/api/runs/{runId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private HttpClient CreateEntraClient(string objectId, params string[] platformRoles) =>
        _factory.CreateAuthenticatedClientForObjectId(objectId, platformRoles);

    private async Task<ProjectId> CreateProjectAsync(
        string ownerOid,
        params (string PrincipalId, ProjectRole Role)[] additionalAssignments)
    {
        var projectId = ProjectId.New();
        using var scope = _factory.Services.CreateScope();
        var projectStore = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        var assignments = scope.ServiceProvider.GetRequiredService<IProjectRoleAssignmentStore>();
        var now = DateTimeOffset.UtcNow;

        await projectStore.InsertAsync(new Project
        {
            Id = projectId,
            Name = $"Run auth {Guid.NewGuid():N}",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = _factory.NewWorkingDirectory(),
            DefaultBranch = "main",
            Owner = ownerOid,
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await assignments.UpsertAsync(new ProjectRoleAssignment
        {
            ProjectId = projectId,
            PrincipalId = ownerOid,
            Role = ProjectRole.Owner,
            GrantedBy = ownerOid,
            GrantedAt = now,
        });

        foreach (var (principalId, role) in additionalAssignments)
        {
            await assignments.UpsertAsync(new ProjectRoleAssignment
            {
                ProjectId = projectId,
                PrincipalId = principalId,
                Role = role,
                GrantedBy = ownerOid,
                GrantedAt = now,
            });
        }

        return projectId;
    }

    private async Task<string> InsertRunAsync(ProjectId? projectId, string submittingUser)
    {
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = "unused",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "project run authorization",
            SubmittingUser = submittingUser,
            Status = RunStatus.Pending,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            AgentName = "Coordinator",
        };
        await _factory.Services.GetRequiredService<IRunStore>().InsertAsync(run);
        return run.Id.ToString();
    }

    private async Task<Run> GetRunAsync(string runId) =>
        (await _factory.Services.GetRequiredService<IRunStore>().GetAsync(RunId.Parse(runId)))!;
}
