using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Api.Skills;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task PrepareAsync_RecordsRedactedDurableProviderProvenanceBeforeReturning()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = await SeedProjectBindingAsync(db);

        var secrets = new InMemorySecretStore();
        await SetCredentialAsync(secrets, ProjectCredentialReference, githubLogin: "private-login");
        var events = new CapturingRunEventStream();
        var executor = CreateExecutor(db, secrets, events);

        _ = await executor.PrepareAsync(
            projectId,
            entraObjectId: "entra-user",
            ProjectModelProviderCapabilityPurpose.SkillGeneration,
            CancellationToken.None);

        events.Appends.Should().ContainSingle();
        var append = events.Appends.Single();
        append.RunId.Should().StartWith("ai-operation-");
        append.Event.Type.Should().Be(EventTypes.RunModelProviderResolved);
        var json = JsonSerializer.Serialize(append.Event.Payload);
        json.Should().NotContain(ProjectBindingId);
        json.Should().NotContain("private-login");
        using var payload = JsonDocument.Parse(json);
        payload.RootElement.GetProperty("operation").GetString().Should().Be("SkillGeneration");
        payload.RootElement.GetProperty("projectId").GetString().Should().Be(projectId.ToString());
        var provenance = EffectiveModelProviderProvenance.TryReadContract(append.Event.Payload);
        provenance.Should().NotBeNull();
        provenance!.ProviderKind.Should().Be("project_github_copilot");
        provenance.ProviderKey.Should().NotBeNullOrWhiteSpace();
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

    [Fact]
    public async Task MarketplaceCapabilityIssuer_RecordsProvenanceBeforeReturningCapability()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = await SeedProjectBindingAsync(db);
        var secrets = new InMemorySecretStore();
        await SetCredentialAsync(secrets, ProjectCredentialReference, githubLogin: "private-login");
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var resolver = new EffectiveModelProviderResolver(
            persistence,
            new ByokProviderConfigurationService(secrets),
            secrets);
        var plans = new AiExecutionPlanService(
            resolver,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiExecution:ProviderKeySigningKey"] = "test-provider-signing-key",
                })
                .Build());
        var accessor = new AiExecutionPlanAccessor();
        var caller = new CallerContext
        {
            User = "entra-user",
            EntraObjectId = "entra-user",
        };
        AiOperationCatalog.TryGet("marketplace_catalog_classification", out var operation)
            .Should().BeTrue();
        var accepted = await plans.PrepareAsync(operation, projectId, caller, CancellationToken.None);
        using var activation = accessor.Push(accepted);
        var events = new CapturingRunEventStream();
        using var services = new ServiceCollection()
            .AddSingleton(persistence)
            .AddSingleton(plans)
            .BuildServiceProvider();
        var issuer = new MarketplaceCopilotCapabilityIssuer(
            services.GetRequiredService<IServiceScopeFactory>(),
            accessor,
            events);

        var capability = await issuer.TryIssueAsync(projectId, caller, CancellationToken.None);

        capability.Should().NotBeNullOrWhiteSpace();
        events.Appends.Should().ContainSingle();
        events.Appends.Single().Event.Type.Should().Be(EventTypes.RunModelProviderResolved);
        var provenance = EffectiveModelProviderProvenance.TryReadContract(
            events.Appends.Single().Event.Payload);
        provenance.Should().NotBeNull();
        provenance!.ProviderKind.Should().Be("project_github_copilot");
        provenance.ProviderKey.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Matches_RejectsSameIdByokConfigurationEditedAfterPreflight()
    {
        var prepared = new ByokProviderConfiguration(
            "provider-1",
            "Azure",
            "azure",
            "https://original.example.test",
            "gpt-5",
            "secret");
        var expected = new EffectiveModelProviderResult.Byok(
            prepared.Id,
            prepared.Type,
            prepared.ExecutionFingerprint());
        var edited = prepared with { BaseUrl = "https://replacement.example.test" };

        GenerationModelProviderExecutor.Matches(prepared, expected).Should().BeTrue();
        GenerationModelProviderExecutor.Matches(edited, expected).Should().BeFalse();
    }

    [Fact]
    public async Task InvocationGuard_RejectsCopilotCredentialDriftAfterPreparation()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = await SeedProjectBindingAsync(db);
        var secrets = new InMemorySecretStore();
        await SetCredentialAsync(secrets, ProjectCredentialReference, githubLogin: "project-user");
        var persistence = new GitHubConnectionsPersistenceStore(db);
        var byok = new ByokProviderConfigurationService(secrets);
        var resolver = new EffectiveModelProviderResolver(persistence, byok, secrets);
        var plans = CreatePlans(resolver);
        var accessor = new AiExecutionPlanAccessor();
        AiOperationCatalog.TryGet("blueprint_generation", out var operation).Should().BeTrue();
        var accepted = await plans.PrepareAsync(
            operation,
            projectId,
            new CallerContext { User = "entra-user", EntraObjectId = "entra-user" },
            CancellationToken.None);
        using var activation = accessor.Push(accepted);
        var executor = new GenerationModelProviderExecutor(
            resolver,
            persistence,
            byok,
            accessor,
            plans);
        var generation = await executor.PrepareAsync(
            projectId,
            "entra-user",
            ProjectModelProviderCapabilityPurpose.BlueprintGeneration,
            CancellationToken.None);

        (await db.ProjectCopilotBindings.SingleAsync()).CredentialVersion = "replacement-version";
        await db.SaveChangesAsync();

        var act = () => generation.ModelInvocationGuard!.ValidateAsync(
            "non-run-blueprint",
            CancellationToken.None);
        (await act.Should().ThrowAsync<AiExecutionPlanException>())
            .Which.ErrorCode.Should().Be("model_provider_changed");
    }

    [Fact]
    public async Task InvocationGuard_PreservesFrozenByokConfigurationWhenProviderIsUnchanged()
    {
        await using var connection = await OpenDatabaseAsync();
        await using var db = new MemoryDbContext(Options(connection));
        var projectId = ProjectId.New();
        db.Projects.Add(Project(projectId));
        await db.SaveChangesAsync();
        var secrets = new InMemorySecretStore();
        var byok = new ByokProviderConfigurationService(secrets);
        var provider = await byok.AddAsync(new ByokProviderConfiguration(
            string.Empty,
            "Azure",
            "azure",
            "https://provider.example.test",
            "gpt-5",
            "secret"), CancellationToken.None);
        await byok.SetActiveAsync(provider.Id, CancellationToken.None);
        var persistence = new GitHubConnectionsPersistenceStore(db, byokSettings: byok);
        var resolver = new EffectiveModelProviderResolver(persistence, byok, secrets);
        var plans = CreatePlans(resolver);
        var accessor = new AiExecutionPlanAccessor();
        AiOperationCatalog.TryGet("blueprint_generation", out var operation).Should().BeTrue();
        var accepted = await plans.PrepareAsync(
            operation,
            projectId,
            new CallerContext { User = "entra-user", EntraObjectId = "entra-user" },
            CancellationToken.None);
        using var activation = accessor.Push(accepted);
        var executor = new GenerationModelProviderExecutor(
            resolver,
            persistence,
            byok,
            accessor,
            plans);

        var generation = await executor.PrepareAsync(
            projectId,
            "entra-user",
            ProjectModelProviderCapabilityPurpose.BlueprintGeneration,
            CancellationToken.None);
        var frozen = generation.ByokProviderConfiguration;
        await generation.ModelInvocationGuard!.ValidateAsync(
            "non-run-blueprint",
            CancellationToken.None);

        generation.ModelSource.Should().Be(ModelSource.Byok);
        accessor.FrozenByokConfiguration.Should().BeSameAs(frozen);
        generation.ByokProviderConfiguration.Should().BeSameAs(frozen);
    }

    private static GenerationModelProviderExecutor CreateExecutor(
        MemoryDbContext db,
        ISecretStore secrets,
        IRunEventStream? eventStream = null)
    {
        var persistence = new GitHubConnectionsPersistenceStore(db);
        return new GenerationModelProviderExecutor(
            new EffectiveModelProviderResolver(
                persistence,
                new ByokProviderConfigurationService(secrets),
                secrets),
            persistence,
            eventStream: eventStream);
    }

    private static AiExecutionPlanService CreatePlans(EffectiveModelProviderResolver resolver) =>
        new(
            resolver,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AiExecution:ProviderKeySigningKey"] = "test-provider-signing-key",
                })
                .Build());

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

    private sealed class CapturingRunEventStream : IRunEventStream
    {
        public List<(string RunId, RunEvent Event)> Appends { get; } = [];

        public ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
        {
            Appends.Add((runId, evt));
            return ValueTask.FromResult(Appends.Count);
        }

        public async IAsyncEnumerable<RunEvent> SubscribeAsync(
            string runId,
            int fromSequence = 0,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask CompleteAsync(string runId, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
