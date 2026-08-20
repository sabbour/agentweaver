using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Auth;

public sealed class LinkedGitHubAccountsApiTests
{
    [Fact]
    public async Task LinkCallback_AssociatesLinkedAccountToCurrentEntraUser()
    {
        const string entraUserId = "00000000-0000-0000-0000-00000000aa01";
        using var factory = new LinkedGitHubAccountsWebApplicationFactory(request =>
        {
            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsoluteUri == "https://github.com/login/oauth/access_token")
            {
                return Json(HttpStatusCode.OK, new { access_token = "oauth-linked-token", expires_in = 3600 });
            }

            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsoluteUri == "https://api.github.com/user")
            {
                return Json(HttpStatusCode.OK, new { login = "linked-user", avatar_url = "https://avatars.example/linked-user" });
            }

            return Json(HttpStatusCode.NotFound, new { error = "unhandled" });
        });
        factory.EntitlementProbe.Set("oauth-linked-token", true);

        using var client = factory.CreateAuthenticatedClient(entraUserId, allowAutoRedirect: false, PlatformRoles.Contributor);
        var begin = await client.PostAsync("/api/auth/github-accounts/link", content: null);
        begin.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = await begin.Content.ReadFromJsonAsync<BeginGitHubAccountLinkResponse>();
        var state = ExtractState(payload!.AuthorizeUrl);

        var callback = await client.GetAsync($"/auth/github/callback?code=test-code&state={Uri.EscapeDataString(state)}");

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().Should().Contain("auth=github_linked")
            .And.Contain("login=linked-user");

        using var scope = factory.Services.CreateScope();
        var store = (IMultiIdentityGitHubTokenStore)scope.ServiceProvider.GetRequiredService<IGitHubTokenStore>();
        var linked = await store.GetLinkedIdentityAsync(entraUserId, "linked-user");
        linked.Should().NotBeNull();
        linked!.IsDefault.Should().BeTrue();
        linked.CopilotEntitled.Should().BeTrue();
        linked.CopilotEntitledCheckedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AccessibleRepos_EnumeratesAcrossAllLinkedAccounts_WithPermissionMetadata()
    {
        const string entraUserId = "00000000-0000-0000-0000-00000000aa02";
        using var factory = new LinkedGitHubAccountsWebApplicationFactory(request =>
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsoluteUri == "https://api.github.com/user/repos?sort=pushed&per_page=100&page=1&affiliation=owner,collaborator,organization_member")
            {
                var token = request.Headers.Authorization?.Parameter;
                return token switch
                {
                    "tok-alice" => Json(HttpStatusCode.OK, new[]
                    {
                        new { full_name = "octo/repo-a", description = "Repo A", @private = false, default_branch = "main", html_url = "https://github.com/octo/repo-a", permissions = new { admin = true, push = true, pull = true } }
                    }),
                    "tok-bob" => Json(HttpStatusCode.OK, new[]
                    {
                        new { full_name = "org/repo-b", description = "Repo B", @private = true, default_branch = "develop", html_url = "https://github.com/org/repo-b", permissions = new { admin = false, push = true, pull = true } }
                    }),
                    _ => Json(HttpStatusCode.NotFound, new { error = "unexpected-token" })
                };
            }

            return Json(HttpStatusCode.NotFound, new { error = "unhandled" });
        });

        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-alice", "alice"), isDefault: true, copilotEntitled: true, copilotEntitledCheckedAt: DateTimeOffset.UtcNow);
        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-bob", "bob"), copilotEntitled: false, copilotEntitledCheckedAt: DateTimeOffset.UtcNow);

        using var client = factory.CreateAuthenticatedClient(entraUserId, roles: [PlatformRoles.Contributor]);
        var response = await client.GetAsync("/api/auth/github-accounts/accessible-repos");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var repos = await response.Content.ReadFromJsonAsync<AccessibleGitHubRepositoryResponse[]>();
        repos.Should().NotBeNull();
        repos!.Should().HaveCount(2);
        repos.Should().ContainSingle(x => x.FullName == "octo/repo-a" && x.AccessibleViaLogin == "alice" && x.Permission == "admin");
        repos.Should().ContainSingle(x => x.FullName == "org/repo-b" && x.AccessibleViaLogin == "bob" && x.Permission == "write");
    }

    [Fact]
    public async Task UnlinkDefaultAccount_AutoPromotesOldestRemainingLinkedAccount()
    {
        const string entraUserId = "00000000-0000-0000-0000-00000000aa03";
        using var factory = new LinkedGitHubAccountsWebApplicationFactory(_ => Json(HttpStatusCode.NotFound, new { }));
        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-alice", "alice"), isDefault: true, copilotEntitledCheckedAt: DateTimeOffset.UtcNow);
        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-bob", "bob"), copilotEntitledCheckedAt: DateTimeOffset.UtcNow);

        using var client = factory.CreateAuthenticatedClient(entraUserId, roles: [PlatformRoles.Contributor]);
        var response = await client.DeleteAsync("/api/auth/github-accounts/alice");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UnlinkGitHubAccountResponse>();
        body!.DefaultLogin.Should().Be("bob");

        var links = await factory.TokenStore.ListLinkedIdentitiesAsync(entraUserId);
        links.Should().ContainSingle(x => x.GitHubLogin == "bob" && x.IsDefault);
        (await factory.TokenStore.GetAsync(GitHubTokenScope.ForLinkedIdentity(entraUserId, "alice"))).Status.Should().Be(GitHubTokenStatus.SignedOut);
    }

    [Fact]
    public async Task ProjectGitHubIdentityOverride_CanBeSetAndCleared()
    {
        const string entraUserId = "00000000-0000-0000-0000-00000000aa04";
        using var factory = new LinkedGitHubAccountsWebApplicationFactory(_ => Json(HttpStatusCode.NotFound, new { }));
        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-alice", "alice"), isDefault: true, copilotEntitled: true, copilotEntitledCheckedAt: DateTimeOffset.UtcNow);
        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-bob", "bob"), copilotEntitled: false, copilotEntitledCheckedAt: DateTimeOffset.UtcNow);

        using var client = factory.CreateAuthenticatedClient(entraUserId, roles: [PlatformRoles.Contributor]);
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Identity Project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var project = await create.Content.ReadFromJsonAsync<ProjectResponse>();

        var initial = await client.GetFromJsonAsync<ProjectGitHubIdentityResponse>($"/api/projects/{project!.ProjectId}/github-identity");
        initial!.EffectiveLogin.Should().Be("alice");
        initial.ResolutionSource.Should().Be("default");

        var setOverride = await client.PutAsJsonAsync(
            $"/api/projects/{project.ProjectId}/github-identity",
            new UpdateProjectGitHubIdentityRequest { GitHubLogin = "bob" });
        setOverride.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var overridden = await client.GetFromJsonAsync<ProjectGitHubIdentityResponse>($"/api/projects/{project.ProjectId}/github-identity");
        overridden!.EffectiveLogin.Should().Be("bob");
        overridden.ProjectOverrideLogin.Should().Be("bob");
        overridden.ResolutionSource.Should().Be("project_override");

        var clearOverride = await client.PutAsJsonAsync(
            $"/api/projects/{project.ProjectId}/github-identity",
            new UpdateProjectGitHubIdentityRequest { GitHubLogin = null });
        clearOverride.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var cleared = await client.GetFromJsonAsync<ProjectGitHubIdentityResponse>($"/api/projects/{project.ProjectId}/github-identity");
        cleared!.EffectiveLogin.Should().Be("alice");
        cleared.ProjectOverrideLogin.Should().BeNull();
        cleared.ResolutionSource.Should().Be("default");
    }

    [Fact]
    public async Task ProjectAuthorizedGitHubOperation_UsesSelectedLinkedIdentityToken()
    {
        const string entraUserId = "00000000-0000-0000-0000-00000000aa05";
        using var factory = new LinkedGitHubAccountsWebApplicationFactory(_ => Json(HttpStatusCode.NotFound, new { }));
        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-alice", "alice"), isDefault: true);
        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-bob", "bob"));
        await factory.TokenStore.SetAsync(GitHubTokenScope.ForUser(entraUserId), Token("tok-alice", "alice"));

        using var client = factory.CreateAuthenticatedClient(entraUserId, roles: [PlatformRoles.ProjectCreator]);
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "Selected Token Project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        var project = await create.Content.ReadFromJsonAsync<ProjectResponse>();

        (await client.PutAsJsonAsync(
            $"/api/projects/{project!.ProjectId}/github-identity",
            new UpdateProjectGitHubIdentityRequest { GitHubLogin = "bob" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var capture = await client.PostAsJsonAsync($"/api/projects/{project.ProjectId}/backlog/tasks", new
        {
            title = "Use selected identity",
        });

        capture.StatusCode.Should().Be(HttpStatusCode.Created);
        var captured = await capture.Content.ReadFromJsonAsync<JsonElement>();
        captured.GetProperty("captured_by").GetString().Should().Be("bob");
    }

    [Fact]
    public async Task BackgroundGitHubOperation_AfterRequestEnds_UsesSelectedProjectIdentityToken()
    {
        const string entraUserId = "00000000-0000-0000-0000-00000000aa10";
        using var factory = new LinkedGitHubAccountsWebApplicationFactory(_ => Json(HttpStatusCode.NotFound, new { }));
        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-alice", "alice"), isDefault: true);
        await factory.TokenStore.LinkIdentityAsync(entraUserId, Token("tok-bob", "bob"));
        await factory.TokenStore.SetAsync(GitHubTokenScope.ForUser(entraUserId), Token("tok-alice", "alice"));

        string projectId;
        string otherProjectId;
        using (var client = factory.CreateAuthenticatedClient(entraUserId, roles: [PlatformRoles.ProjectCreator]))
        {
            var create = await client.PostAsJsonAsync("/api/projects", new
            {
                name = "Background Identity Project",
                origin = "blank",
                working_directory = factory.NewWorkingDirectory(),
            });
            var project = await create.Content.ReadFromJsonAsync<ProjectResponse>();
            projectId = project!.ProjectId;

            (await client.PutAsJsonAsync(
                $"/api/projects/{projectId}/github-identity",
                new UpdateProjectGitHubIdentityRequest { GitHubLogin = "bob" }))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

            var createOther = await client.PostAsJsonAsync("/api/projects", new
            {
                name = "Default Identity Project",
                origin = "blank",
                working_directory = factory.NewWorkingDirectory(),
            });
            var otherProject = await createOther.Content.ReadFromJsonAsync<ProjectResponse>();
            otherProjectId = otherProject!.ProjectId;
        }

        using var serviceScope = factory.Services.CreateScope();
        var parsedProjectId = ProjectId.Parse(projectId);
        var projectStore = serviceScope.ServiceProvider.GetRequiredService<IProjectStore>();
        await projectStore.UpdateOriginAsync(
            parsedProjectId,
            ProjectOrigin.FromGitHub("acme/widgets"),
            DateTimeOffset.UtcNow);
        await projectStore.UpdateOriginAsync(
            ProjectId.Parse(otherProjectId),
            ProjectOrigin.FromGitHub("acme/other"),
            DateTimeOffset.UtcNow);

        var prClient = new RecordingPullRequestClient();
        var scopeProvider = serviceScope.ServiceProvider.GetRequiredService<IGitHubTokenScopeProvider>();
        var executor = new OpenPullRequestTurnExecutor(
            prClient,
            scopeProvider,
            serviceScope.ServiceProvider.GetRequiredService<IGitHubAccessTokenProvider>(),
            serviceScope.ServiceProvider.GetRequiredService<ILoggerFactory>(),
            projectStore: projectStore);
        var input = new AgentTurnOutput(
            RunId: "background-run",
            TreeHash: "tree",
            Diff: "diff --git a/file.txt b/file.txt",
            StepCount: 1,
            WorktreePath: factory.NewWorkingDirectory(),
            WorktreeBranch: "agentweaver/background-run",
            RepositoryPath: factory.NewWorkingDirectory(),
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            SubmittingUser: entraUserId,
            ProjectId: projectId);

        await executor.HandleAsync(input, context: null!, CancellationToken.None);

        prClient.AccessToken.Should().Be("tok-bob");

        await executor.HandleAsync(
            input with { RunId = "other-project-run", ProjectId = otherProjectId },
            context: null!,
            CancellationToken.None);
        prClient.AccessToken.Should().Be("tok-alice");

        var globalScope = await scopeProvider.ResolveAsync(entraUserId, projectId: null);
        (await factory.TokenStore.GetAsync(globalScope)).AccessToken.Should().Be("tok-alice");

        var automationScope = await scopeProvider.ResolveAsync("automation:event-trigger", projectId);
        automationScope.Should().Be(GitHubTokenScope.ForUser("automation:event-trigger"));
        (await factory.TokenStore.GetAsync(automationScope)).Status.Should().NotBe(
            GitHubTokenStatus.SignedIn,
            "webhook automation must fail closed rather than borrow a project member's linked identity");
    }

    [Fact]
    public async Task ProjectIdentityOverride_RejectsCrossUserAndUnlinkedLogins_AndPreservesRoleBoundaries()
    {
        const string ownerId = "00000000-0000-0000-0000-00000000aa06";
        const string viewerId = "00000000-0000-0000-0000-00000000aa07";
        const string contributorId = "00000000-0000-0000-0000-00000000aa08";
        const string otherUserId = "00000000-0000-0000-0000-00000000aa09";
        using var factory = new LinkedGitHubAccountsWebApplicationFactory(_ => Json(HttpStatusCode.NotFound, new { }));
        await factory.TokenStore.LinkIdentityAsync(ownerId, Token("tok-owner", "owner"), isDefault: true);
        await factory.TokenStore.LinkIdentityAsync(contributorId, Token("tok-contributor", "contributor"), isDefault: true);
        await factory.TokenStore.LinkIdentityAsync(otherUserId, Token("tok-other", "other-user"), isDefault: true);

        using var owner = factory.CreateAuthenticatedClient(ownerId, roles: [PlatformRoles.ProjectCreator]);
        var create = await owner.PostAsJsonAsync("/api/projects", new
        {
            name = "Role Boundary Project",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        var project = await create.Content.ReadFromJsonAsync<ProjectResponse>();
        var identityUrl = $"/api/projects/{project!.ProjectId}/github-identity";

        (await owner.PutAsJsonAsync(
            identityUrl,
            new UpdateProjectGitHubIdentityRequest { GitHubLogin = "other-user" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.PutAsJsonAsync(
            identityUrl,
            new UpdateProjectGitHubIdentityRequest { GitHubLogin = "unlinked" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var otherUser = factory.CreateAuthenticatedClient(otherUserId, roles: [PlatformRoles.Contributor]);
        (await otherUser.GetAsync(identityUrl)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await otherUser.PutAsJsonAsync(
            identityUrl,
            new UpdateProjectGitHubIdentityRequest { GitHubLogin = "other-user" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await owner.PostAsJsonAsync($"/api/projects/{project.ProjectId}/role-assignments", new
        {
            principal_id = viewerId,
            role = "Viewer",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync($"/api/projects/{project.ProjectId}/role-assignments", new
        {
            principal_id = contributorId,
            role = "Contributor",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var viewer = factory.CreateAuthenticatedClient(viewerId, roles: [PlatformRoles.Viewer]);
        (await viewer.GetAsync(identityUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PutAsJsonAsync(
            identityUrl,
            new UpdateProjectGitHubIdentityRequest { GitHubLogin = null }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var contributor = factory.CreateAuthenticatedClient(contributorId, roles: [PlatformRoles.Contributor]);
        (await contributor.PutAsJsonAsync(
            identityUrl,
            new UpdateProjectGitHubIdentityRequest { GitHubLogin = "contributor" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static GitHubToken Token(string accessToken, string login) =>
        new(accessToken, RefreshToken: null, ExpiresAt: null, Login: login, AvatarUrl: $"https://avatars.example/{login}", Scopes: []);

    private static HttpResponseMessage Json(HttpStatusCode statusCode, object body) =>
        new(statusCode) { Content = JsonContent.Create(body) };

    private static string ExtractState(string authorizeUrl)
    {
        var uri = new Uri(authorizeUrl);
        var state = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("state");
        state.Should().NotBeNullOrWhiteSpace();
        return state!;
    }

    private sealed class StubEntitlementProbe : IGitHubCopilotEntitlementProbe
    {
        private readonly Dictionary<string, bool?> _values = new(StringComparer.Ordinal);

        public void Set(string accessToken, bool? value) => _values[accessToken] = value;

        public Task<bool?> ProbeAsync(string accessToken, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(accessToken, out var value) ? value : (bool?)null);
    }

    private sealed class DispatchHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> dispatch) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(dispatch(request));
    }

    private sealed class RecordingPullRequestClient : IGitHubPullRequestClient
    {
        public string? AccessToken { get; private set; }

        public Task<GitHubPullRequestResult> CreatePullRequestAsync(
            string owner,
            string repo,
            string title,
            string? body,
            string baseBranch,
            string headBranch,
            bool draft,
            string accessToken,
            CancellationToken ct = default)
        {
            AccessToken = accessToken;
            return Task.FromResult(GitHubPullRequestResult.Ok(1, "https://github.com/acme/widgets/pull/1"));
        }

        public Task<GitHubPullRequestResult?> FindOpenPullRequestAsync(
            string owner,
            string repo,
            string baseBranch,
            string headBranch,
            string accessToken,
            CancellationToken ct = default) =>
            Task.FromResult<GitHubPullRequestResult?>(null);
    }

    private sealed class LinkedGitHubAccountsWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath;
        private readonly string _workspaceRoot;
        private readonly string _worktreesPath;
        private readonly string _checkpointsPath;
        private readonly string _coordinatorCheckpointsPath;
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RsaSecurityKey _signingKey;
        private readonly SigningCredentials _signingCredentials;
        private readonly HttpMessageHandler _handler;

        public const string TenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
        public const string ClientId = "11111111-2222-3333-4444-555555555555";
        public string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

        public InMemoryGitHubTokenStore TokenStore { get; } = new();
        public StubEntitlementProbe EntitlementProbe { get; } = new();

        public LinkedGitHubAccountsWebApplicationFactory(Func<HttpRequestMessage, HttpResponseMessage> dispatch)
        {
            var unique = Guid.NewGuid().ToString("N");
            _dbPath = Path.Combine(Path.GetTempPath(), $"agentweaver-linked-{unique}.db");
            _workspaceRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-linked-ws-{unique}");
            _worktreesPath = Path.Combine(Path.GetTempPath(), $"agentweaver-linked-wt-{unique}");
            _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-linked-cp-{unique}");
            _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-linked-ccp-{unique}");
            Directory.CreateDirectory(_workspaceRoot);

            _handler = new DispatchHttpMessageHandler(dispatch);
            _signingKey = new RsaSecurityKey(_rsa) { KeyId = $"kid-{unique}" };
            _signingCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);
        }

        public HttpClient CreateAuthenticatedClient(string objectId, bool allowAutoRedirect = true, params string[] roles)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = allowAutoRedirect });
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", CreateBearerToken(objectId, roles));
            return client;
        }

        public string NewWorkingDirectory()
        {
            var dir = Path.Combine(_workspaceRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Path"] = _dbPath,
                    ["Worktrees:BasePath"] = _worktreesPath,
                    ["Checkpoints:Path"] = _checkpointsPath,
                    ["Coordinator:Checkpoints:Path"] = _coordinatorCheckpointsPath,
                    ["Auth:Mode"] = "Entra",
                    ["Auth:Entra:TenantId"] = TenantId,
                    ["Auth:Entra:ClientId"] = ClientId,
                    ["Auth:Entra:Issuer"] = Issuer,
                    ["Auth:Entra:JwksJson"] = BuildJwksJson(),
                    ["Auth:GitHub:ClientId"] = "test-github-client-id",
                    ["Auth:GitHub:ClientSecret"] = "test-github-client-secret",
                    ["Auth:GitHub:CallbackUrl"] = "http://localhost/auth/github/callback",
                    ["Auth:GitHub:FrontendUrl"] = "http://localhost:5173",
                    ["Auth:GitHub:BaseUrl"] = "https://github.com",
                    ["Git:Author:Name"] = "Test",
                    ["Git:Author:Email"] = "test@localhost",
                    ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                    ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                    ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                    ["Providers:MicrosoftFoundry:ApiKey"] = "test-foundry-key",
                    ["Providers:MicrosoftFoundry:Endpoint"] = "https://test.openai.azure.com",
                    ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                    ["RunBounds:MaxSteps"] = "50",
                    ["RunBounds:MaxMinutes"] = "10",
                    ["Auth:ApiKey"] = "internal-test-api-key",
                });
            });

            builder.ConfigureServices(services =>
            {
                RemoveService<IGitHubTokenStore>(services);
                services.AddSingleton<IGitHubTokenStore>(TokenStore);

                RemoveService<IGitHubCopilotEntitlementProbe>(services);
                services.AddSingleton<IGitHubCopilotEntitlementProbe>(EntitlementProbe);

                RemoveService<ProjectGitInitializer>(services);
                services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();

                services.Configure<HttpClientFactoryOptions>(string.Empty, options =>
                {
                    options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = _handler);
                });
                services.Configure<HttpClientFactoryOptions>("github", options =>
                {
                    options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = _handler);
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;

            _rsa.Dispose();
            var memoryDbPath = SqliteMemoryDbPathResolver.Resolve(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _dbPath })
                .Build());
            foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", memoryDbPath, memoryDbPath + "-wal", memoryDbPath + "-shm" })
                try { File.Delete(path); } catch { }

            foreach (var dir in new[] { _workspaceRoot, _worktreesPath, _checkpointsPath, _coordinatorCheckpointsPath })
                try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private string CreateBearerToken(string objectId, params string[] roles)
        {
            var claims = new List<Claim>
            {
                new("oid", objectId),
                new("tid", TenantId),
                new("preferred_username", "entra.user@contoso.com"),
            };
            claims.AddRange(roles.Select(role => new Claim("roles", role)));

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = ClientId,
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(30),
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                IssuedAt = DateTime.UtcNow,
                SigningCredentials = _signingCredentials,
            };

            return new JsonWebTokenHandler().CreateToken(descriptor);
        }

        private string BuildJwksJson()
        {
            var parameters = _rsa.ExportParameters(false);
            return JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = SecurityAlgorithms.RsaSha256,
                        kid = _signingKey.KeyId,
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent),
                    }
                }
            });
        }

        private static void RemoveService<T>(IServiceCollection services)
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
            if (descriptor is not null)
                services.Remove(descriptor);
        }
    }

    private sealed class NoOpProjectGitInitializer(ILogger<ProjectGitInitializer> logger) : ProjectGitInitializer(logger)
    {
        public override string InitBlank(string workingDirectory, string defaultBranch)
        {
            Directory.CreateDirectory(workingDirectory);
            return defaultBranch;
        }

        public override string Clone(
            string workingDirectory,
            string sourceRepository,
            string accessToken,
            GitClonePurpose purpose)
        {
            Directory.CreateDirectory(workingDirectory);
            return "main";
        }

        public override void PushToNewRemote(string workingDirectory, string remoteUrl, string branchName, string accessToken)
        {
        }
    }
}
