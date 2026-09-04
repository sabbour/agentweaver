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

    [Fact]
    public async Task Session_uses_platform_byok_before_personal_provider()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var secrets = new CountingSecretStore();
        var platform = new ByokProviderConfigurationService(secrets);
        var platformProvider = await platform.AddAsync(
            new("", "Platform", "openai", "https://platform.example.com/v1", "platform-model", "key"),
            CancellationToken.None);
        await platform.SetActiveAsync(platformProvider.Id, CancellationToken.None);
        var personal = new UserModelProviderSettingsService(db, secrets);
        await personal.SetByokAsync(
            "user",
            new("", "Personal", "openai", "https://personal.example.com/v1", "personal-model", "key"),
            CancellationToken.None);
        var resolver = new EffectiveModelProviderResolver(
            new GitHubConnectionsPersistenceStore(db), platform, secrets, personal);

        var result = await resolver.ResolveForSessionAsync("user", CancellationToken.None);

        result.Should().Be(new EffectiveModelProviderResult.Byok(
            platformProvider.Id, platformProvider.Type));
    }

    [Fact]
    public async Task Session_uses_personal_byok_when_platform_has_no_byok()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var secrets = new CountingSecretStore();
        var personal = new UserModelProviderSettingsService(db, secrets);
        var provider = await personal.SetByokAsync(
            "user",
            new("", "Personal", "openai", "https://personal.example.com/v1", "personal-model", "key"),
            CancellationToken.None);
        var resolver = new EffectiveModelProviderResolver(
            new GitHubConnectionsPersistenceStore(db),
            new ByokProviderConfigurationService(secrets),
            secrets,
            personal);

        var result = await resolver.ResolveForSessionAsync("user", CancellationToken.None);

        result.Should().Be(new EffectiveModelProviderResult.UserByok(
            provider.Id, provider.Type, "user"));
    }

    [Fact]
    public async Task Session_requires_personal_copilot_and_never_reuses_platform_copilot()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        db.PlatformDefaultCopilotBindings.Add(PlatformBinding());
        await db.SaveChangesAsync();
        var secrets = new CountingSecretStore();
        await SetCredentialAsync(secrets, PlatformCredentialReference, githubLogin: "platform-user");
        var resolver = new EffectiveModelProviderResolver(
            new GitHubConnectionsPersistenceStore(db),
            new ByokProviderConfigurationService(secrets),
            secrets,
            new UserModelProviderSettingsService(db, secrets));

        var result = await resolver.ResolveForSessionAsync("user", CancellationToken.None);

        result.Should().Be(new EffectiveModelProviderResult.Unavailable(
            EffectiveModelProviderUnavailableReason.UserProviderRequired,
            "Configure a personal model provider in Account settings to continue."));
        secrets.ReadCount(PlatformCredentialReference).Should().Be(0);
    }

    [Fact]
    public async Task Session_uses_only_the_callers_personal_copilot_binding()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        db.UserCopilotBindings.Add(new UserCopilotBindingRecord
        {
            Id = "user-binding",
            EntraObjectId = "user",
            CredentialReference = "copilot-app-user-credential",
            CredentialVersion = "version",
            GrantDigest = "digest",
            Status = GitHubBindingStatus.Active,
            BoundAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var secrets = new CountingSecretStore();
        await SetCredentialAsync(secrets, "copilot-app-user-credential", githubLogin: "personal-user");
        var resolver = new EffectiveModelProviderResolver(
            new GitHubConnectionsPersistenceStore(db),
            new ByokProviderConfigurationService(secrets),
            secrets,
            new UserModelProviderSettingsService(db, secrets));

        var result = await resolver.ResolveForSessionAsync("user", CancellationToken.None);

        result.Should().Be(new EffectiveModelProviderResult.UserGitHubCopilot(
            "user-binding", "personal-user", "user"));
        (await resolver.ResolveForSessionAsync("other-user", CancellationToken.None))
            .Should().BeOfType<EffectiveModelProviderResult.Unavailable>();
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
