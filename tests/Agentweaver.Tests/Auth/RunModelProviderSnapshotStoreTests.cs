using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Auth;

public sealed class RunModelProviderSnapshotStoreTests
{
    [Fact]
    public async Task FinalAssemblyRevision_UsesAcceptedByokConfigurationAfterProviderIsReconfigured()
    {
        await using var fixture = await Fixture.CreateAsync();
        var snapshots = fixture.CreateStore();
        var run = Run();
        var accepted = new ByokProviderConfiguration(
            "accepted", "Accepted", "azure", "https://accepted.example.test", "gpt-5", "not-a-real-key");
        var acceptedProvider = new EffectiveModelProviderResult.Byok(
            accepted.Id, accepted.Type, accepted.ExecutionFingerprint());

        await snapshots.CaptureAsync(run, acceptedProvider, accepted, CancellationToken.None);

        var revision = await fixture.CreateStore().TryGetAsync(run, CancellationToken.None);

        revision.Should().NotBeNull();
        revision!.Provider.Should().BeOfType<EffectiveModelProviderResult.Byok>();
        revision.ByokProviderConfiguration.Should().Be(accepted);
        revision.ByokProviderFingerprint.Should().Be(accepted.ExecutionFingerprint());
    }

    [Fact]
    public async Task ConcurrentReplicaInitialCapture_ReturnsTheDatabaseOwnerSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run() with { ModelSource = ModelSource.GitHubCopilot };
        var first = new EffectiveModelProviderResult.PlatformGitHubCopilot("first", null, "v1");
        var second = new EffectiveModelProviderResult.PlatformGitHubCopilot("second", null, "v2");

        var results = await Task.WhenAll(
            fixture.CreateStore().CaptureAsync(run, first, null, CancellationToken.None),
            fixture.CreateStore().CaptureAsync(run, second, null, CancellationToken.None));

        results.Select(result => result.Provider.ProviderKey()).Distinct().Should().ContainSingle();
        (await fixture.CreateStore().TryGetAsync(run, CancellationToken.None))!.Provider.ProviderKey()
            .Should().Be(results[0].Provider.ProviderKey());

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await db.RunModelProviderSnapshotOwners.SingleAsync();
        owner.RunId.Should().Be(run.Id.ToString());
        owner.SecretReference.Should().NotContain("first").And.NotContain("second");
        owner.SecretReference.Should().NotContain("v1").And.NotContain("v2");
    }

    [Fact]
    public async Task RepeatedCapture_ReusesOwnerWithoutWritingAnotherCandidateSecret()
    {
        var secrets = new CountingSecretStore();
        await using var fixture = await Fixture.CreateAsync(secrets);
        var run = Run() with { ModelSource = ModelSource.GitHubCopilot };
        var accepted = new EffectiveModelProviderResult.PlatformGitHubCopilot("accepted", null, "v1");

        var first = await fixture.CreateStore()
            .CaptureWithOwnershipAsync(run, accepted, null, CancellationToken.None);
        var repeated = await fixture.CreateStore()
            .CaptureWithOwnershipAsync(
                run,
                new EffectiveModelProviderResult.PlatformGitHubCopilot("later", null, "v2"),
                null,
                CancellationToken.None);

        first.OwnedSecretReference.Should().NotBeNull();
        repeated.OwnedSecretReference.Should().BeNull();
        repeated.Boundary.Provider.ProviderKey().Should().Be(accepted.ProviderKey());
        secrets.SetCount.Should().Be(1);
        secrets.DeleteCount.Should().Be(0);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.RunModelProviderSnapshotOwners.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CapabilityPreparationFailure_ReleaseRemovesWinningOwnerAndPrivateSecret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run();
        var capture = await fixture.CreateStore().CaptureWithOwnershipAsync(
            run,
            new EffectiveModelProviderResult.Byok("provider", "azure", "fingerprint"),
            new ByokProviderConfiguration(
                "provider", "Azure", "azure", "https://example.test", "gpt-5", "private-key"),
            CancellationToken.None);

        await fixture.CreateStore().ReleaseAsync(capture, CancellationToken.None);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.RunModelProviderSnapshotOwners.AnyAsync()).Should().BeFalse();
        (await fixture.Secrets.GetSecretAsync(capture.OwnedSecretReference!)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentLosingCapture_ReleaseCannotDeleteTheDatabaseWinner()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run() with { ModelSource = ModelSource.GitHubCopilot };
        var results = await Task.WhenAll(
            fixture.CreateStore().CaptureWithOwnershipAsync(
                run, new EffectiveModelProviderResult.PlatformGitHubCopilot("first", null, "v1"), null, CancellationToken.None),
            fixture.CreateStore().CaptureWithOwnershipAsync(
                run, new EffectiveModelProviderResult.PlatformGitHubCopilot("second", null, "v2"), null, CancellationToken.None));
        var loser = results.Single(result => result.OwnedSecretReference is null);

        await fixture.CreateStore().ReleaseAsync(loser, CancellationToken.None);

        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await db.RunModelProviderSnapshotOwners.SingleAsync();
        (await fixture.Secrets.GetSecretAsync(owner.SecretReference)).Found.Should().BeTrue();
    }

    [Fact]
    public async Task RunInsertionFailure_ReleaseFailurePreservesOwnerForExplicitRetry()
    {
        await using var fixture = await Fixture.CreateAsync(new DeleteFailingSecretStore());
        var store = fixture.CreateStore();
        var capture = await store.CaptureWithOwnershipAsync(
            Run(),
            new EffectiveModelProviderResult.PlatformGitHubCopilot("accepted", null, "v1"),
            null,
            CancellationToken.None);

        Func<Task> release = () => store.ReleaseAsync(capture, CancellationToken.None);

        await release.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*secret cleanup failed*");
        await using var scope = fixture.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.RunModelProviderSnapshotOwners.AnyAsync()).Should().BeTrue(
            "a failed secret deletion must leave its durable owner for reconciliation");
    }

    [Fact]
    public async Task RestartAndRetryCanReplayOriginalSnapshotAfterProviderToggle()
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = Run();
        var accepted = new ByokProviderConfiguration(
            "accepted", "Accepted", "openai", "https://accepted.example.test", "gpt-5", "not-a-real-key");
        var acceptedProvider = new EffectiveModelProviderResult.Byok(
            accepted.Id, accepted.Type, accepted.ExecutionFingerprint());
        await fixture.CreateStore().CaptureAsync(original, acceptedProvider, accepted, CancellationToken.None);

        var restarted = await fixture.CreateStore().TryGetAsync(original, CancellationToken.None);
        var retry = Run() with { RetriedFrom = original.Id.ToString() };
        await fixture.CreateStore().CaptureAsync(
            retry, restarted!.Provider, restarted.ByokProviderConfiguration, CancellationToken.None);

        (await fixture.CreateStore().TryGetAsync(retry, CancellationToken.None))!.ByokProviderConfiguration
            .Should().Be(accepted);
    }

    [Fact]
    public async Task MissingOwnedSecret_FailsClosed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run();
        var accepted = new EffectiveModelProviderResult.PlatformGitHubCopilot("accepted", null, "v1");
        await fixture.CreateStore().CaptureAsync(run, accepted, null, CancellationToken.None);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var owner = await db.RunModelProviderSnapshotOwners.SingleAsync();
            await fixture.Secrets.DeleteSecretAsync(owner.SecretReference);
        }

        var action = () => fixture.CreateStore().TryGetAsync(run, CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentProviderException>();
        exception.Which.ErrorCode.Should().Be("model_provider_snapshot_unavailable");
    }

    [Fact]
    public async Task RetryRefresh_ReplacesOnlyAnUnreadableCopilotSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run() with { ModelSource = ModelSource.GitHubCopilot };
        var provider = new EffectiveModelProviderResult.PlatformGitHubCopilot("accepted", null, "v1");
        await fixture.CreateStore().CaptureAsync(run, provider, null, CancellationToken.None);

        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var owner = await db.RunModelProviderSnapshotOwners.SingleAsync();
            await fixture.Secrets.DeleteSecretAsync(owner.SecretReference);
        }

        var refreshed = await fixture.CreateStore()
            .RefreshUnavailableCopilotSnapshotForRetryAsync(run, provider, CancellationToken.None);

        refreshed.Provider.Should().BeEquivalentTo(provider);
        (await fixture.CreateStore().TryGetAsync(run, CancellationToken.None))!.Provider
            .Should().BeEquivalentTo(provider);
    }

    [Fact]
    public async Task ByokSnapshot_CannotBeReadThroughACopilotRun()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run();
        await CaptureValidByokSnapshotAsync(fixture, run);

        var action = () => fixture.CreateStore().TryGetAsync(
            run with { ModelSource = ModelSource.GitHubCopilot }, CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentProviderException>();
        exception.Which.ErrorCode.Should().Be("model_provider_changed");
    }

    [Fact]
    public async Task CopilotSnapshot_CannotBeReadThroughAByokRun()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run() with { ModelSource = ModelSource.GitHubCopilot };
        await fixture.CreateStore().CaptureAsync(
            run,
            new EffectiveModelProviderResult.PlatformGitHubCopilot("accepted", null, "v1"),
            null,
            CancellationToken.None);

        var action = () => fixture.CreateStore().TryGetAsync(
            run with { ModelSource = ModelSource.Byok }, CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentProviderException>();
        exception.Which.ErrorCode.Should().Be("model_provider_changed");
    }

    [Theory]
    [InlineData("""{"Version":1,"ProviderKind":"byok","ProviderId":"provider","ProviderType":"azure","ByokConfiguration":{}}""")]
    [InlineData("""{"Version":1,"ProviderKind":"byok","ProviderId":"provider","ProviderType":"azure","ByokConfiguration":{"Id":"provider","Name":"Azure","Type":"azure","BaseUrl":"https://example.test","Model":"gpt-5","ApiKey":""}}""")]
    [InlineData("""{"Version":1,"ProviderKind":"byok","ProviderId":"different-provider","ProviderType":"azure","ByokConfiguration":{"Id":"provider","Name":"Azure","Type":"azure","BaseUrl":"https://example.test","Model":"gpt-5","ApiKey":"secret"}}""")]
    [InlineData("""{"Version":1,"ProviderKind":"byok","ProviderId":"provider","ProviderType":"openai","ByokConfiguration":{"Id":"provider","Name":"Azure","Type":"azure","BaseUrl":"https://example.test","Model":"gpt-5","ApiKey":"secret"}}""")]
    [InlineData("""{"Version":2,"ProviderKind":"byok","ProviderId":"provider","ProviderType":"azure","ByokConfiguration":{"Id":"provider","Name":"Azure","Type":"azure","BaseUrl":"https://example.test","Model":"gpt-5","ApiKey":"secret"}}""")]
    [InlineData("""{"Version":1,"ProviderKind":"unknown","ProviderId":"provider"}""")]
    [InlineData("""{"Version":1,"ProviderKind":"byok","ProviderId":"provider","ProviderType":"azure","ByokConfiguration":{"Id":"provider","Name":"Azure","Type":"azure","BaseUrl":"https://example.test","Model":"gpt-5","ApiKey":"secret","Unexpected":"value"}}""")]
    public async Task MalformedByokWinnerSecret_FailsClosedBeforeItCanReachAnExecutor(string malformedSnapshot)
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run();
        await CaptureValidByokSnapshotAsync(fixture, run);
        await fixture.ReplaceWinnerSecretAsync(run, malformedSnapshot);

        var action = () => fixture.CreateStore().TryGetAsync(run, CancellationToken.None);

        var exception = await action.Should().ThrowAsync<AgentProviderException>();
        exception.Which.ErrorCode.Should().Be("model_provider_snapshot_unavailable");
        exception.Which.Message.Should().NotContain("secret");
    }

    [Fact]
    public async Task CorruptWinnerSecret_FailsClosedBeforeItCanReachAnExecutor()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run();
        await CaptureValidByokSnapshotAsync(fixture, run);
        await fixture.ReplaceWinnerSecretAsync(run, "{ corrupt snapshot");

        var action = () => fixture.CreateStore().TryGetAsync(run, CancellationToken.None);

        await action.Should().ThrowAsync<AgentProviderException>();
    }

    [Fact]
    public async Task ImpossibleWinnerSecretReference_FailsClosedWithoutReadingTheSecret()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = Run();
        await CaptureValidByokSnapshotAsync(fixture, run);
        await fixture.ReplaceWinnerReferenceAsync(run, "not-a-run-provider-snapshot");

        var action = () => fixture.CreateStore().TryGetAsync(run, CancellationToken.None);

        await action.Should().ThrowAsync<AgentProviderException>();
    }

    private static async Task CaptureValidByokSnapshotAsync(Fixture fixture, Run run)
    {
        var configuration = new ByokProviderConfiguration(
            "provider", "Azure", "azure", "https://example.test", "gpt-5", "secret");
        await fixture.CreateStore().CaptureAsync(
            run,
            new EffectiveModelProviderResult.Byok(
                configuration.Id, configuration.Type, configuration.ExecutionFingerprint()),
            configuration,
            CancellationToken.None);
    }

    private static Run Run() => new()
    {
        Id = RunId.New(),
        RepositoryPath = "repo",
        OriginatingBranch = "dev",
        ModelSource = ModelSource.Byok,
        Task = "final assembly revision",
        SubmittingUser = "test",
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
        ProjectId = ProjectId.New(),
        AgentName = "Coordinator",
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(SqliteConnection connection, ServiceProvider services, ISecretStore secrets)
        {
            _connection = connection;
            Services = services;
            Secrets = secrets;
        }

        public ISecretStore Secrets { get; }
        public ServiceProvider Services { get; }

        public RunModelProviderSnapshotStore CreateStore() =>
            new(Secrets, Services.GetRequiredService<IServiceScopeFactory>());

        public async Task ReplaceWinnerSecretAsync(Run run, string value)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var owner = await db.RunModelProviderSnapshotOwners.SingleAsync(x => x.RunId == run.Id.ToString());
            await Secrets.SetSecretAsync(owner.SecretReference, value);
        }

        public async Task ReplaceWinnerReferenceAsync(Run run, string secretReference)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var owner = await db.RunModelProviderSnapshotOwners.SingleAsync(x => x.RunId == run.Id.ToString());
            owner.SecretReference = secretReference;
            await db.SaveChangesAsync();
        }

        public static async Task<Fixture> CreateAsync(ISecretStore? secretStore = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<MemoryDbContext>(options => options.UseSqlite(connection));
            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                await db.Database.EnsureCreatedAsync();
            }
            return new Fixture(connection, provider, secretStore ?? new InMemorySecretStore());
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
        }

    }

    private sealed class DeleteFailingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();

        public Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default) =>
            _inner.GetSecretAsync(key, ct);

        public Task<string> SetSecretAsync(
            string key, string value, string? etag = null, CancellationToken ct = default) =>
            _inner.SetSecretAsync(key, value, etag, ct);

        public Task DeleteSecretAsync(string key, CancellationToken ct = default) =>
            throw new InvalidOperationException("secret cleanup failed");
    }

    private sealed class CountingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();

        public int SetCount { get; private set; }
        public int DeleteCount { get; private set; }

        public Task<SecretGetResult> GetSecretAsync(string key, CancellationToken ct = default) =>
            _inner.GetSecretAsync(key, ct);

        public Task<string> SetSecretAsync(
            string key, string value, string? etag = null, CancellationToken ct = default)
        {
            SetCount++;
            return _inner.SetSecretAsync(key, value, etag, ct);
        }

        public Task DeleteSecretAsync(string key, CancellationToken ct = default)
        {
            DeleteCount++;
            return _inner.DeleteSecretAsync(key, ct);
        }
    }
}
