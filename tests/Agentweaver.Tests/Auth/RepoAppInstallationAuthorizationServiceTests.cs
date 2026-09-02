using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Api.Webhooks;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Auth;

/// <summary>
/// Proves the previously-missing production path exists end to end: a project owner begins a
/// project-pinned GitHub App installation, GitHub's Setup URL redirect (installation_id/state, no
/// bearer header) is completed against the persisted transaction, the connected repository's numeric
/// ID is resolved from the live installation, and <see cref="RepoAppInstallationLifecycleService.BindAsync"/>
/// is actually called — creating the <see cref="GitHubInstallationRecord"/> and
/// <see cref="GitHubRepositoryGrantRecord"/> that unattended runs require.
/// </summary>
public sealed class RepoAppInstallationAuthorizationServiceTests
{
    [Fact]
    public async Task InstallCallbackToBind_BindsTheConnectedRepositoryEndToEnd()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        await SeedProjectRecordAsync(db, project);
        var projectStore = new FakeProjectStore();
        projectStore.Seed(GitHubOriginProject(project, "owner/repository"));
        roles.SetOwner(project, "owner");
        var handler = new RecordingHandler(
            """{"token":"ghs_list_token","expires_at":"2030-01-01T00:00:00Z"}""",
            """{"repositories":[{"id":99,"full_name":"owner/repository"}]}""",
            """{"id":72,"repository_selection":"selected","account":{"login":"owner"},"permissions":{"contents":"read"}}""",
            """{"token":"ghs_metadata_token","expires_at":"2030-01-01T00:00:00Z"}""",
            """{"id":99,"full_name":"owner/repository"}""");
        var service = CreateService(db, projectStore, roles, handler);

        var begin = await service.BeginAsync(Human("owner"), HumanPrincipal(), project);
        begin.Outcome.Should().Be(RepoAppInstallationAuthorizationOutcome.Success);
        begin.InstallationUrl.Should().StartWith("https://github.com/apps/agentweaver-repo/installations/new?state=");
        var state = Query(begin.InstallationUrl!, "state");
        var stored = await db.GitHubAuthorizations.SingleAsync();
        stored.ProjectId.Should().Be(project.ToString());
        stored.AppKind.Should().Be(GitHubAppKind.Repo);
        stored.Purpose.Should().Be(GitHubAuthorizationPurpose.UnattendedRepositoryInstallation);

        var result = await service.CompleteBrowserCallbackAsync(
            browserSessionId: null, browserEntraObjectId: null,
            installationId: 72, setupAction: "install", state: state, callbackCookie: begin.CallbackCookie);

        result.Outcome.Should().Be(RepoAppInstallationAuthorizationOutcome.Success);
        result.ProjectId.Should().Be(project.ToString());
        var installation = await db.GitHubInstallations.SingleAsync();
        installation.InstallationId.Should().Be(72);
        installation.ProjectId.Should().Be(project.ToString());
        var grant = await db.GitHubRepositoryGrants.SingleAsync();
        grant.RepositoryId.Should().Be(99);
        grant.ProjectId.Should().Be(project.ToString());
        grant.FullNameDisplay.Should().Be("owner/repository");
        db.ChangeTracker.Clear();
        (await db.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Completed);

        service.GetCallbackRedirect(result.Outcome, result.ProjectId)
            .Should().Be($"https://agentweaver.test/projects/{project}/settings?section=unattended&repo_app_install=success");
    }

    [Fact]
    public async Task Callback_RejectsReplayAndCrossProjectStateSubstitution()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        await SeedProjectRecordAsync(db, project);
        var projectStore = new FakeProjectStore();
        projectStore.Seed(GitHubOriginProject(project, "owner/repository"));
        roles.SetOwner(project, "owner");
        var handler = new RecordingHandler(
            """{"token":"ghs_list_token","expires_at":"2030-01-01T00:00:00Z"}""",
            """{"repositories":[{"id":99,"full_name":"owner/repository"}]}""",
            """{"id":72,"repository_selection":"selected","account":{"login":"owner"},"permissions":{"contents":"read"}}""",
            """{"token":"ghs_metadata_token","expires_at":"2030-01-01T00:00:00Z"}""",
            """{"id":99,"full_name":"owner/repository"}""");
        var service = CreateService(db, projectStore, roles, handler);
        var begin = await service.BeginAsync(Human("owner"), HumanPrincipal(), project);
        var state = Query(begin.InstallationUrl!, "state");

        (await service.CompleteBrowserCallbackAsync(null, null, 72, "install", state, "wrong-cookie")).Outcome
            .Should().Be(RepoAppInstallationAuthorizationOutcome.AuthorizationTransactionInvalid);

        var first = await service.CompleteBrowserCallbackAsync(null, null, 72, "install", state, begin.CallbackCookie);
        first.Outcome.Should().Be(RepoAppInstallationAuthorizationOutcome.Success);

        var replay = await service.CompleteBrowserCallbackAsync(null, null, 72, "install", state, begin.CallbackCookie);
        replay.Outcome.Should().Be(RepoAppInstallationAuthorizationOutcome.AuthorizationTransactionConsumed);
    }

    [Fact]
    public async Task Begin_RequiresExplicitProjectOwnership()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        var projectStore = new FakeProjectStore();
        projectStore.Seed(GitHubOriginProject(project, "owner/repository"));
        var service = CreateService(db, projectStore, roles, new RecordingHandler());

        var admin = new CallerContext { User = "admin", EntraObjectId = "admin", PlatformRoles = [PlatformRoles.PlatformAdmin] };
        (await service.BeginAsync(admin, HumanPrincipal(), project)).Outcome
            .Should().Be(RepoAppInstallationAuthorizationOutcome.ProjectOwnerRequired);
        (await db.GitHubAuthorizations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Begin_RequiresAConnectedRepository()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        var projectStore = new FakeProjectStore();
        projectStore.Seed(BlankProject(project));
        roles.SetOwner(project, "owner");
        var service = CreateService(db, projectStore, roles, new RecordingHandler());

        (await service.BeginAsync(Human("owner"), HumanPrincipal(), project)).Outcome
            .Should().Be(RepoAppInstallationAuthorizationOutcome.RepositoryNotConnected);
    }

    [Fact]
    public async Task Callback_TreatsAnOrgApprovalRequestAsPendingWithoutConsumingTheTransaction()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        await SeedProjectRecordAsync(db, project);
        var projectStore = new FakeProjectStore();
        projectStore.Seed(GitHubOriginProject(project, "owner/repository"));
        roles.SetOwner(project, "owner");
        var service = CreateService(db, projectStore, roles, new RecordingHandler());
        var begin = await service.BeginAsync(Human("owner"), HumanPrincipal(), project);
        var state = Query(begin.InstallationUrl!, "state");

        var result = await service.CompleteBrowserCallbackAsync(
            null, null, installationId: null, setupAction: "request", state, begin.CallbackCookie);

        result.Outcome.Should().Be(RepoAppInstallationAuthorizationOutcome.InstallationRequestPending);
        (await db.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Pending);
        (await db.GitHubInstallations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Callback_ReportsWhenTheInstallationDoesNotGrantTheConnectedRepository()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        await SeedProjectRecordAsync(db, project);
        var projectStore = new FakeProjectStore();
        projectStore.Seed(GitHubOriginProject(project, "owner/repository"));
        roles.SetOwner(project, "owner");
        var handler = new RecordingHandler(
            """{"token":"ghs_list_token","expires_at":"2030-01-01T00:00:00Z"}""",
            """{"repositories":[{"id":5,"full_name":"other/repository"}]}""");
        var service = CreateService(db, projectStore, roles, handler);
        var begin = await service.BeginAsync(Human("owner"), HumanPrincipal(), project);
        var state = Query(begin.InstallationUrl!, "state");

        var result = await service.CompleteBrowserCallbackAsync(null, null, 72, "install", state, begin.CallbackCookie);

        result.Outcome.Should().Be(RepoAppInstallationAuthorizationOutcome.RepositoryNotFoundInInstallation);
        (await db.GitHubInstallations.CountAsync()).Should().Be(0);
        db.ChangeTracker.Clear();
        (await db.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Failed);
    }

    private static RepoAppInstallationAuthorizationService CreateService(
        MemoryDbContext db,
        FakeProjectStore projectStore,
        MutableRoles roles,
        HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:RepoApp:Slug"] = "agentweaver-repo",
            ["Auth:RepoApp:BaseUrl"] = "https://github.com",
            ["Auth:RepoApp:FrontendUrl"] = "https://agentweaver.test",
            ["Auth:RepoApp:AppId"] = "123",
            ["Auth:RepoApp:PrivateKeySecretName"] = "repo-app-pem",
            ["Auth:RepoApp:ApiUrl"] = "https://api.github.test",
        }).Build();
        var secrets = new InMemorySecretStore();
        using var rsa = RSA.Create(2048);
        secrets.SetSecretAsync("repo-app-pem", rsa.ExportRSAPrivateKeyPem()).GetAwaiter().GetResult();
        var tokenService = new RepoAppInstallationTokenService(
            configuration, db, secrets, new StubHttpClientFactory(handler));
        return new(configuration, new GitHubConnectionsPersistenceStore(db), projectStore, roles, tokenService, db,
            NullLogger<RepoAppInstallationAuthorizationService>.Instance);
    }

    private static CallerContext Human(string id) => new() { User = id, EntraObjectId = id };
    private static ClaimsPrincipal HumanPrincipal() => new(new ClaimsIdentity([new Claim("oid", "owner")], "test"));

    private static Project GitHubOriginProject(ProjectId id, string sourceRepository) => new()
    {
        Id = id,
        Name = "Project",
        Origin = ProjectOrigin.FromGitHub(sourceRepository),
        WorkingDirectory = "C:\\project",
        DefaultBranch = "main",
        Owner = "owner",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Project BlankProject(ProjectId id) => new()
    {
        Id = id,
        Name = "Project",
        Origin = ProjectOrigin.Blank(),
        WorkingDirectory = "C:\\project",
        DefaultBranch = "main",
        Owner = "owner",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task SeedProjectRecordAsync(MemoryDbContext db, ProjectId project)
    {
        db.Projects.Add(new ProjectRecord { ProjectId = project.ToString() });
        await db.SaveChangesAsync();
    }

    private static string Query(string url, string name) => Uri.UnescapeDataString(
        new Uri(url).Query.TrimStart('?').Split('&').Single(x => x.StartsWith($"{name}=", StringComparison.Ordinal)).Split('=', 2)[1]);

    private sealed class MutableRoles : IProjectRoleAssignmentStore
    {
        private readonly HashSet<(ProjectId, string)> owners = [];
        public void SetOwner(ProjectId p, string s) => owners.Add((p, s));
        public Task<ProjectRoleAssignment?> GetAsync(ProjectId p, string s, CancellationToken ct = default) =>
            Task.FromResult(owners.Contains((p, s))
                ? new ProjectRoleAssignment { ProjectId = p, PrincipalId = s, Role = ProjectRole.Owner, GrantedBy = "test", GrantedAt = DateTimeOffset.UtcNow }
                : null);
        public Task UpsertAsync(ProjectRoleAssignment a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> UpsertEnsuringOwnerInvariantAsync(ProjectRoleAssignment a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByProjectAsync(ProjectId p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByPrincipalAsync(string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(ProjectId p, string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> DeleteEnsuringOwnerInvariantAsync(ProjectId p, string s, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Minimal in-memory <see cref="IProjectStore"/> double; only <see cref="GetAsync"/> is exercised.</summary>
    private sealed class FakeProjectStore : IProjectStore
    {
        private readonly Dictionary<ProjectId, Project> _projects = new();

        public void Seed(Project project) => _projects[project.Id] = project;

        public Task InsertAsync(Project project, CancellationToken ct = default)
        {
            _projects[project.Id] = project;
            return Task.CompletedTask;
        }

        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) =>
            Task.FromResult(_projects.TryGetValue(id, out var project) ? project : null);

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateOriginAsync(ProjectId id, ProjectOrigin origin, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateGenerationModelSettingsAsync(
            ProjectId id,
            string? blueprintGenerationModel,
            string? workflowGenerationModel,
            string? outcomeSpecGenerationModel,
            DateTimeOffset updatedAt,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(ProjectId id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdatePickupSettingsAsync(
            ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IProjectTeamMutationLease?> TryBeginTeamMutationAsync(ProjectId id, long expectedRevision, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(params string[] payloads) : HttpMessageHandler
    {
        private readonly Queue<string> _payloads = new(payloads);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _payloads.Count > 0 ? _payloads.Dequeue() : "{}", Encoding.UTF8, "application/json"),
            });
    }
}
