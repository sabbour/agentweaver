using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Auth;

public sealed class EffectiveModelProviderResolverTests
{
    [Fact]
    public async Task Valid_project_credential_wins_and_is_read_once()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = await SeedProjectBindingAsync(db);
        var secrets = new CountingSecretStore();
        await SetCredentialAsync(secrets, ProjectCredentialReference, githubLogin: "project-user");
        var resolver = CreateResolver(db, secrets);

        var result = await resolver.ResolveAsync(projectId, CancellationToken.None);

        result.Should().Be(new EffectiveModelProviderResult.ProjectGitHubCopilot(
            ProjectBindingId,
            "project-user"));
        secrets.ReadCount(ProjectCredentialReference).Should().Be(1);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"status":"revoked","accessToken":"token"}""")]
    [InlineData("""{"status":"signed-in","accessToken":""}""")]
    public async Task Malformed_revoked_or_empty_project_credential_is_unavailable(string credential)
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = await SeedProjectBindingAsync(db);
        var secrets = new CountingSecretStore();
        await secrets.SetSecretAsync(ProjectCredentialReference, credential);
        var resolver = CreateResolver(db, secrets);

        var result = await resolver.ResolveAsync(projectId, CancellationToken.None);

        result.Should().BeOfType<EffectiveModelProviderResult.Unavailable>();
    }

    [Fact]
    public async Task Expired_project_credential_is_unavailable()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = await SeedProjectBindingAsync(db);
        var secrets = new CountingSecretStore();
        await SetCredentialAsync(
            secrets,
            ProjectCredentialReference,
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var resolver = CreateResolver(db, secrets);

        var result = await resolver.ResolveAsync(projectId, CancellationToken.None);

        result.Should().BeOfType<EffectiveModelProviderResult.Unavailable>();
    }

    [Fact]
    public async Task Missing_active_project_credential_stops_byok_and_platform_fallback()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = await SeedProjectBindingAsync(db);
        db.PlatformDefaultCopilotBindings.Add(PlatformBinding());
        await db.SaveChangesAsync();

        var secrets = new CountingSecretStore();
        await SetCredentialAsync(secrets, PlatformCredentialReference, githubLogin: "platform-user");
        var byok = new ByokProviderConfigurationService(secrets);
        var provider = await byok.AddAsync(
            new ByokProviderConfiguration(
                string.Empty,
                "Deployment provider",
                "openai",
                "https://api.example.com/v1",
                "model",
                "key"),
            CancellationToken.None);
        await byok.SetActiveAsync(provider.Id, CancellationToken.None);
        var resolver = new EffectiveModelProviderResolver(
            new GitHubConnectionsPersistenceStore(db),
            byok,
            secrets);

        var result = await resolver.ResolveAsync(projectId, CancellationToken.None);

        result.Should().Be(new EffectiveModelProviderResult.Unavailable(
            EffectiveModelProviderUnavailableReason.ProjectBindingRequiresReauthorization,
            "The project's active GitHub Copilot binding credential is unavailable. Reconnect the project's GitHub Copilot App."));
        secrets.ReadCount(ProjectCredentialReference).Should().Be(1);
        secrets.ReadCount(PlatformCredentialReference).Should().Be(0);
    }

    [Fact]
    public async Task Redeemable_project_binding_wins_over_active_byok()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = await SeedProjectBindingAsync(db);
        var secrets = new CountingSecretStore();
        await SetCredentialAsync(secrets, ProjectCredentialReference, githubLogin: "project-user");
        var byok = new ByokProviderConfigurationService(secrets);
        var provider = await byok.AddAsync(new ByokProviderConfiguration(
            string.Empty, "Deployment provider", "openai", "https://api.example.com/v1", "model", "key"),
            CancellationToken.None);
        await byok.SetActiveAsync(provider.Id, CancellationToken.None);
        var resolver = new EffectiveModelProviderResolver(
            new GitHubConnectionsPersistenceStore(db), byok, secrets);

        var result = await resolver.ResolveAsync(projectId, CancellationToken.None);

        result.Should().Be(new EffectiveModelProviderResult.ProjectGitHubCopilot(
            ProjectBindingId, "project-user"));
    }

    [Fact]
    public async Task Project_without_binding_inherits_active_byok()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId));
        await db.SaveChangesAsync();

        var secrets = new CountingSecretStore();
        var byok = new ByokProviderConfigurationService(secrets);
        var provider = await byok.AddAsync(
            new ByokProviderConfiguration(
                string.Empty,
                "Deployment provider",
                "openai",
                "https://api.example.com/v1",
                "model",
                "key"),
            CancellationToken.None);
        await byok.SetActiveAsync(provider.Id, CancellationToken.None);
        var resolver = new EffectiveModelProviderResolver(
            new GitHubConnectionsPersistenceStore(db),
            byok,
            secrets);

        var result = await resolver.ResolveAsync(projectId, CancellationToken.None);

        result.Should().Be(new EffectiveModelProviderResult.Byok(provider.Id, provider.Type));
    }

    [Fact]
    public async Task Project_without_binding_inherits_redeemable_platform_copilot()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId));
        db.PlatformDefaultCopilotBindings.Add(PlatformBinding());
        await db.SaveChangesAsync();

        var secrets = new CountingSecretStore();
        await SetCredentialAsync(secrets, PlatformCredentialReference, githubLogin: "platform-user");
        var resolver = CreateResolver(db, secrets);

        var result = await resolver.ResolveAsync(projectId, CancellationToken.None);

        result.Should().Be(new EffectiveModelProviderResult.PlatformGitHubCopilot(
            PlatformDefaultCopilotBindingRecord.SingletonId,
            "platform-user"));
    }

    private const string ProjectBindingId = "project-binding";
    private const string ProjectCredentialReference = "copilot-app-project-project-version";
    private const string PlatformCredentialReference = "copilot-app-platform-default-version";

    private static EffectiveModelProviderResolver CreateResolver(MemoryDbContext db, ISecretStore secrets) =>
        new(
            new GitHubConnectionsPersistenceStore(db),
            new ByokProviderConfigurationService(secrets),
            secrets);

    private static async Task<ProjectId> SeedProjectBindingAsync(MemoryDbContext db)
    {
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId));
        db.ProjectCopilotBindings.Add(new ProjectCopilotBindingRecord
        {
            Id = ProjectBindingId,
            ProjectId = projectId.ToString(),
            EntraObjectId = "owner",
            CredentialReference = ProjectCredentialReference,
            CredentialVersion = "version",
            GrantDigest = "digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return projectId;
    }

    private static PlatformDefaultCopilotBindingRecord PlatformBinding() => new()
    {
        Id = PlatformDefaultCopilotBindingRecord.SingletonId,
        EntraObjectId = "platform-admin",
        CredentialReference = PlatformCredentialReference,
        CredentialVersion = "version",
        GrantDigest = "digest",
        Status = GitHubBindingStatus.Active,
        BoundAt = DateTimeOffset.UtcNow,
    };

    private static ProjectRecord Project(ProjectId projectId) => new()
    {
        ProjectId = projectId.ToString(),
        Name = "Project",
        OriginKind = "blank",
        WorkingDirectory = "C:\\project",
        Owner = "owner",
        DefaultProvider = "github_copilot",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static Task SetCredentialAsync(
        ISecretStore secrets,
        string reference,
        DateTimeOffset? expiresAt = null,
        string? githubLogin = null) =>
        secrets.SetSecretAsync(
            reference,
            JsonSerializer.Serialize(new
            {
                status = "signed-in",
                accessToken = "token",
                expiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
                githubLogin,
            }));

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MemoryDbContext(Options(connection));
        await db.Database.EnsureCreatedAsync();
        return connection;
    }

    private static DbContextOptions<MemoryDbContext> Options(SqliteConnection connection) =>
        new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;

    private sealed class CountingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);

        public async Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default)
        {
            _reads[key] = ReadCount(key) + 1;
            return await _inner.GetSecretAsync(key, ct);
        }

        public Task<string> SetSecretAsync(
            string key,
            string value,
            string? etag = null,
            CancellationToken ct = default) =>
            _inner.SetSecretAsync(key, value, etag, ct);

        public Task DeleteSecretAsync(string key, CancellationToken ct = default) =>
            _inner.DeleteSecretAsync(key, ct);

        internal int ReadCount(string key) => _reads.GetValueOrDefault(key);
    }
}
