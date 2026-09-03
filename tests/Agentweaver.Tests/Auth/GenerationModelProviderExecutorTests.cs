using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Auth;

public sealed class GenerationModelProviderExecutorTests
{
    private const string ProjectBindingId = "project-binding";
    private const string ProjectCredentialReference = "copilot-app-project-project-version";
    private const string PlatformCredentialReference = "copilot-app-platform-default-version";

    [Fact]
    public async Task PrepareAsync_ProjectlessPlatformCopilot_IssuesPlatformScopedCapabilityWithoutProjectForeignKey()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        db.PlatformDefaultCopilotBindings.Add(PlatformBinding());
        await db.SaveChangesAsync();

        var secrets = new InMemorySecretStore();
        await SetCredentialAsync(secrets, PlatformCredentialReference, githubLogin: "platform-user");
        var executor = CreateExecutor(db, secrets);

        var plan = await executor.PrepareAsync(
            projectId: null,
            entraObjectId: "entra-user",
            ProjectModelProviderCapabilityPurpose.BlueprintGeneration,
            CancellationToken.None);

        plan.ModelSource.Should().Be(ModelSource.GitHubCopilot);
        plan.Capability.Should().NotBeNull();
        plan.Capability!.ProjectId.Should().BeNull();
        plan.Capability.EntraObjectId.Should().Be("entra-user");
        plan.Capability.Purpose.Should().Be(ProjectModelProviderCapabilityPurpose.BlueprintGeneration);

        var stored = await db.MarketplaceCopilotCapabilities.SingleAsync(
            x => x.CapabilityRef == plan.Capability.CapabilityReference);
        stored.ProjectId.Should().BeNull();
        stored.SourceBindingId.Should().Be(PlatformDefaultCopilotBindingRecord.SingletonId);
        stored.EntraObjectId.Should().Be("entra-user");
        stored.Purpose.Should().Be((int)ProjectModelProviderCapabilityPurpose.BlueprintGeneration);
    }

    [Fact]
    public async Task PrepareAsync_ProjectScopedCopilot_PersistsRealProjectId()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = await SeedProjectBindingAsync(db);

        var secrets = new InMemorySecretStore();
        await SetCredentialAsync(secrets, ProjectCredentialReference, githubLogin: "project-user");
        var executor = CreateExecutor(db, secrets);

        var plan = await executor.PrepareAsync(
            projectId,
            entraObjectId: "entra-user",
            ProjectModelProviderCapabilityPurpose.BlueprintGeneration,
            CancellationToken.None);

        plan.ModelSource.Should().Be(ModelSource.GitHubCopilot);
        plan.Capability.Should().NotBeNull();
        plan.Capability!.ProjectId.Should().Be(projectId.ToString());

        var stored = await db.MarketplaceCopilotCapabilities.SingleAsync(
            x => x.CapabilityRef == plan.Capability.CapabilityReference);
        stored.ProjectId.Should().Be(projectId.ToString());
        stored.SourceBindingId.Should().Be(ProjectBindingId);
        stored.Purpose.Should().Be((int)ProjectModelProviderCapabilityPurpose.BlueprintGeneration);
    }

    [Fact]
    public async Task PrepareAsync_ProjectWithoutBinding_FallsBackToPlatformBindingWhileKeepingRealProjectId()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId));
        db.PlatformDefaultCopilotBindings.Add(PlatformBinding());
        await db.SaveChangesAsync();

        var secrets = new InMemorySecretStore();
        await SetCredentialAsync(secrets, PlatformCredentialReference, githubLogin: "platform-user");
        var executor = CreateExecutor(db, secrets);

        var plan = await executor.PrepareAsync(
            projectId,
            entraObjectId: "entra-user",
            ProjectModelProviderCapabilityPurpose.BlueprintGeneration,
            CancellationToken.None);

        plan.ModelSource.Should().Be(ModelSource.GitHubCopilot);
        plan.Capability.Should().NotBeNull();
        plan.Capability!.ProjectId.Should().Be(projectId.ToString());

        var stored = await db.MarketplaceCopilotCapabilities.SingleAsync(
            x => x.CapabilityRef == plan.Capability.CapabilityReference);
        stored.ProjectId.Should().Be(projectId.ToString());
        stored.SourceBindingId.Should().Be(PlatformDefaultCopilotBindingRecord.SingletonId);
        stored.CredentialReference.Should().Be(PlatformCredentialReference);
    }

    private static GenerationModelProviderExecutor CreateExecutor(MemoryDbContext db, ISecretStore secrets)
    {
        var persistence = new GitHubConnectionsPersistenceStore(db);
        return new GenerationModelProviderExecutor(
            new EffectiveModelProviderResolver(
                persistence,
                new ByokProviderConfigurationService(secrets),
                secrets),
            persistence);
    }

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
}
