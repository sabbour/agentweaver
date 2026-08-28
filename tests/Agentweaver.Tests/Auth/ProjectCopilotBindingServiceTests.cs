using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Auth;

public sealed class ProjectCopilotBindingServiceTests
{
    [Fact]
    public async Task Begin_PinsProjectAndAllowsOnlyMandatoryMetadataReadPermission()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        await SeedProjectAsync(db, project);
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles, new InMemorySecretStore());

        var admin = new CallerContext { User = "admin", EntraObjectId = "admin", PlatformRoles = [PlatformRoles.PlatformAdmin] };
        (await service.BeginAsync(admin, HumanPrincipal(), project)).Outcome.Should().Be(CopilotBindingOutcome.ProjectOwnerRequired);

        var begin = await service.BeginAsync(Human("owner"), HumanPrincipal(), project);
        begin.Outcome.Should().Be(CopilotBindingOutcome.Success);
        var stored = await db.GitHubAuthorizations.SingleAsync();
        stored.ProjectId.Should().Be(project.ToString());
        stored.AppKind.Should().Be(GitHubAppKind.Copilot);
        stored.Purpose.Should().Be(GitHubAuthorizationPurpose.InteractiveCopilot);
        begin.AuthorizationUrl.Should().Contain("code_challenge_method=S256").And.NotContain(begin.TransactionId);
    }

    [Fact]
    public async Task McpBrowserHandoff_PinsProjectAndTransfersTheCookieOnlyOnce()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        await SeedProjectAsync(db, project);
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles, new InMemorySecretStore());

        var begin = await service.BeginMcpHandoffAsync(Human("owner"), HumanPrincipal(), project);

        begin.Outcome.Should().Be(CopilotBindingOutcome.Success);
        begin.BrowserUrl.Should().Be(
            $"https://agentweaver.test/auth/github/copilot-app/handoff/{begin.TransactionId}");
        JsonSerializer.Serialize(begin).Should().NotContain("state").And.NotContain("cookie")
            .And.NotContain("repository").And.NotContain("installation");

        var handoff = await service.TakeMcpBrowserHandoffAsync(begin.TransactionId!);
        handoff.Should().NotBeNull();
        handoff!.Value.AuthorizationUrl.Should().Contain("state=").And.NotContain(begin.TransactionId!);
        (await service.TakeMcpBrowserHandoffAsync(begin.TransactionId!)).Should().BeNull();
    }

    [Fact]
    public async Task Complete_RejectsProjectSubstitutionAndOwnerLossWithoutBinding()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        var other = ProjectId.New();
        await SeedProjectAsync(db, project, other);
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles, new InMemorySecretStore());
        var begin = await service.BeginAsync(Human("owner"), HumanPrincipal(), project);
        var state = Query(begin.AuthorizationUrl!, "state");

        (await service.CompleteAsync(Human("owner"), HumanPrincipal(), other, state, "code", begin.CallbackCookie))
            .Should().Be(CopilotBindingOutcome.AuthorizationTransactionInvalid);
        (await db.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Pending);

        roles.Remove(project, "owner");
        (await service.CompleteAsync(Human("owner"), HumanPrincipal(), project, state, "code", begin.CallbackCookie))
            .Should().Be(CopilotBindingOutcome.ProjectOwnerRequired);
        db.ChangeTracker.Clear();
        (await db.GitHubAuthorizations.SingleAsync()).Status.Should().Be(GitHubAuthorizationStatus.Failed);
        (await db.ProjectCopilotBindings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Disconnect_AllowsHumanAdminButTombstonesOnlyThatProjectsBinding()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var secrets = new InMemorySecretStore();
        var project = ProjectId.New();
        var other = ProjectId.New();
        await SeedProjectAsync(db, project, other);
        await new TwoAppPersistenceStore(db).ReplaceCopilotBindingAsync(Binding(project, "project-secret", "version-one"));
        await new TwoAppPersistenceStore(db).ReplaceCopilotBindingAsync(Binding(other, "other-secret", "version-two"));
        await secrets.SetSecretAsync("project-secret", """{"accessToken":"ghu_should_not_persist"}""");
        await secrets.SetSecretAsync("other-secret", """{"accessToken":"ghu_other"}""");
        var service = CreateService(db, roles, secrets);
        var admin = new CallerContext { User = "admin", EntraObjectId = "admin", PlatformRoles = [PlatformRoles.PlatformAdmin] };

        (await service.DisconnectAsync(admin, HumanPrincipal(), project)).Should().Be(CopilotBindingOutcome.Success);
        db.ChangeTracker.Clear();
        (await db.ProjectCopilotBindings.SingleAsync(x => x.ProjectId == project.ToString())).Status.Should().Be(GitHubBindingStatus.Revoked);
        (await db.ProjectCopilotBindings.SingleAsync(x => x.ProjectId == other.ToString())).Status.Should().Be(GitHubBindingStatus.Active);
        (await secrets.GetSecretAsync("project-secret")).Value.Should().NotContain("ghu_");
        (await secrets.GetSecretAsync("other-secret")).Value.Should().Contain("ghu_other");
    }

    [Fact]
    public async Task BindingAndAuditSerialization_RedactsProviderCredential()
    {
        await using var db = await OpenDatabaseAsync();
        var roles = new MutableRoles();
        var project = ProjectId.New();
        await SeedProjectAsync(db, project);
        roles.SetOwner(project, "owner");
        var service = CreateService(db, roles, new InMemorySecretStore(), """{"access_token":"ghu_provider","refresh_token":"refresh-secret"}""");
        var begin = await service.BeginAsync(Human("owner"), HumanPrincipal(), project);

        (await service.CompleteAsync(Human("owner"), HumanPrincipal(), project, Query(begin.AuthorizationUrl!, "state"), "code", begin.CallbackCookie))
            .Should().Be(CopilotBindingOutcome.Success);
        JsonSerializer.Serialize(new { binding = await db.ProjectCopilotBindings.SingleAsync(), audit = await db.GitHubAuditRecords.SingleAsync() })
            .Should().NotContain("ghu_").And.NotContain("refresh-secret").And.NotContain("code");
    }

    private static ProjectCopilotBindingService CreateService(MemoryDbContext db, MutableRoles roles, ISecretStore secrets, string? provider = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:CopilotApp:ClientId"] = "copilot-client", ["Auth:CopilotApp:ClientSecret"] = "copilot-secret",
            ["Auth:CopilotApp:CallbackUrl"] = "https://agentweaver.test/auth/github/copilot-app/callback",
            ["Auth:CopilotApp:Slug"] = "agentweaver-copilot",
        }).Build();
        var httpClientFactory = new StubHttpClientFactory(provider);
        return new(configuration, new TwoAppPersistenceStore(db), secrets, httpClientFactory, roles,
            new CopilotAppRegistrationService(configuration, httpClientFactory));
    }
    private static CallerContext Human(string id) => new() { User = id, EntraObjectId = id };
    private static ClaimsPrincipal HumanPrincipal() => new(new ClaimsIdentity([new Claim("oid", "owner")], "test"));
    private static ProjectCopilotBindingRecord Binding(ProjectId project, string reference, string version) => new()
    {
        Id = Guid.NewGuid().ToString("N"), ProjectId = project.ToString(), EntraObjectId = "owner",
        CredentialReference = reference, CredentialVersion = version, GrantDigest = "digest", Status = GitHubBindingStatus.Active, BoundAt = DateTimeOffset.UtcNow,
    };
    private static async Task<MemoryDbContext> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); return db;
    }
    private static async Task SeedProjectAsync(MemoryDbContext db, params ProjectId[] projects)
    {
        db.Projects.AddRange(projects.Select(project => new ProjectRecord { ProjectId = project.ToString() }));
        await db.SaveChangesAsync();
    }
    private static string Query(string url, string name) => Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&').Single(x => x.StartsWith($"{name}=", StringComparison.Ordinal)).Split('=', 2)[1]);
    private sealed class MutableRoles : IProjectRoleAssignmentStore
    {
        private readonly HashSet<(ProjectId, string)> owners = [];
        public void SetOwner(ProjectId p, string s) => owners.Add((p, s));
        public void Remove(ProjectId p, string s) => owners.Remove((p, s));
        public Task<ProjectRoleAssignment?> GetAsync(ProjectId p, string s, CancellationToken ct = default) => Task.FromResult(owners.Contains((p, s)) ? new ProjectRoleAssignment { ProjectId = p, PrincipalId = s, Role = ProjectRole.Owner, GrantedBy = "test", GrantedAt = DateTimeOffset.UtcNow } : null);
        public Task UpsertAsync(ProjectRoleAssignment a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> UpsertEnsuringOwnerInvariantAsync(ProjectRoleAssignment a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByProjectAsync(ProjectId p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectRoleAssignment>> ListByPrincipalAsync(string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(ProjectId p, string s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProjectRoleAssignmentStoreMutationResult> DeleteEnsuringOwnerInvariantAsync(ProjectId p, string s, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class StubHttpClientFactory(string? response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(response ?? """{"access_token":"ghu_token"}"""));
        private sealed class Handler(string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath.StartsWith("/apps/", StringComparison.Ordinal)
                        ? """{"permissions":{"metadata":"read"}}"""
                        : body),
                });
        }
    }
}
