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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EntraProjectOwner_CanReadProjectRun_RegardlessOfSubmittingIdentity(bool linkedGitHubIdentity)
    {
        var ownerOid = linkedGitHubIdentity ? LinkedOwnerOid : UnlinkedOwnerOid;
        var projectId = await CreateProjectAsync(ownerOid);
        if (linkedGitHubIdentity)
            await LinkGitHubIdentityAsync(ownerOid, LinkedGitHubLogin);

        var runId = await InsertRunAsync(
            projectId,
            linkedGitHubIdentity ? LinkedGitHubLogin : ownerOid);

        using var owner = CreateEntraClient(ownerOid, PlatformRoles.Viewer);
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
        context.Items[GitHubTokenAuthMiddleware.CallerItemKey] = new CallerContext
        {
            User = ProjectAuthorization.InternalServiceUser,
        };

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

    private async Task LinkGitHubIdentityAsync(string entraOid, string githubLogin)
    {
        using var scope = _factory.Services.CreateScope();
        var tokenStore = scope.ServiceProvider.GetRequiredService<IGitHubTokenStore>()
            .Should().BeAssignableTo<IMultiIdentityGitHubTokenStore>().Subject;
        await tokenStore.LinkIdentityAsync(
            entraOid,
            new GitHubToken(
                $"token-{githubLogin}",
                RefreshToken: null,
                ExpiresAt: null,
                Login: githubLogin,
                AvatarUrl: null,
                Scopes: ["repo"]));
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

public sealed class LegacyRunAuthorizationTests : IClassFixture<ReviewWebApplicationFactory>
{
    private readonly ReviewWebApplicationFactory _factory;

    public LegacyRunAuthorizationTests(ReviewWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task NullProjectRun_PreservesLegacyOwnerAuthorization()
    {
        var runId = await InsertRunAsync(projectId: null, ReviewWebApplicationFactory.OtherUser);
        using var owner = CreateClient(ReviewWebApplicationFactory.OtherApiKey);
        using var nonOwner = CreateClient("unrelated-legacy-api-key");

        (await owner.GetAsync($"/api/runs/{runId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await nonOwner.GetAsync($"/api/runs/{runId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NullProjectRun_DoesNotTreatConfiguredServiceUserAsReadBypass()
    {
        var runId = await InsertRunAsync(projectId: null, ReviewWebApplicationFactory.OtherUser);
        using var internalService = CreateClient(ReviewWebApplicationFactory.OwnerApiKey);

        (await internalService.GetAsync($"/api/runs/{runId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProjectScopedRun_InGitHubLegacyMode_UsesPersistedProjectAuthorization()
    {
        var projectId = ProjectId.New();
        await _factory.Services.GetRequiredService<IProjectStore>().InsertAsync(new Project
        {
            Id = projectId,
            Name = $"Legacy run auth {Guid.NewGuid():N}",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = AppContext.BaseDirectory,
            DefaultBranch = "main",
            Owner = ReviewWebApplicationFactory.OtherUser,
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var runId = await InsertRunAsync(projectId, "legacy-submitting-user");
        using var submittingUser = CreateClient("legacy-submitting-user");
        using var projectOwner = CreateClient(ReviewWebApplicationFactory.OtherApiKey);

        (await submittingUser.GetAsync($"/api/runs/{runId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await projectOwner.GetAsync($"/api/runs/{runId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InternalService_CannotUseOrdinaryProjectRunEndpoints_InGitHubLegacyMode()
    {
        var projectId = await CreateProjectAsync(ReviewWebApplicationFactory.OtherUser);
        var runId = await InsertRunAsync(projectId, "unrelated-submitting-user");
        foreach (var apiKey in new[]
                 {
                     ReviewWebApplicationFactory.InternalServiceApiKey,
                     ReviewWebApplicationFactory.OwnerApiKey,
                 })
        {
            using var internalService = CreateClient(apiKey);
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
            };

            foreach (var request in requests)
            {
                using (request)
                using (var response = await internalService.SendAsync(request))
                    response.StatusCode.Should().Be(HttpStatusCode.Forbidden, request.RequestUri!.ToString());
            }
        }
        (await GetRunAsync(runId)).ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task InternalService_CanInvokeExplicitProjectRunPreviewCallback_InGitHubLegacyMode()
    {
        var projectId = await CreateProjectAsync(ReviewWebApplicationFactory.OtherUser);
        var runId = await InsertRunAsync(projectId, "unrelated-submitting-user");
        _factory.Services.GetRequiredService<IRunOptionsStore>().SetAutoApproveTools(runId, true);
        foreach (var apiKey in new[]
                 {
                     ReviewWebApplicationFactory.InternalServiceApiKey,
                     ReviewWebApplicationFactory.OwnerApiKey,
                 })
        {
            using var internalService = CreateClient(apiKey);
            var response = await internalService.PostAsJsonAsync(
                $"/api/runs/{runId}/sandbox/preview",
                new { target_port = 3000 });

            response.StatusCode.Should().Be(
                HttpStatusCode.Conflict,
                "the callback must pass authorization before the fixture reports that no sandbox pod is registered");
        }
    }

    private HttpClient CreateClient(string apiKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    private async Task<ProjectId> CreateProjectAsync(string owner)
    {
        var projectId = ProjectId.New();
        var now = DateTimeOffset.UtcNow;
        await _factory.Services.GetRequiredService<IProjectStore>().InsertAsync(new Project
        {
            Id = projectId,
            Name = $"Legacy run auth {Guid.NewGuid():N}",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = AppContext.BaseDirectory,
            DefaultBranch = "main",
            Owner = owner,
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
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
            Task = "legacy run authorization",
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
