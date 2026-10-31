using System.Reflection;
using System.Threading.Channels;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Casting;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Memory;

public sealed class AddressedTurnAgentTests
{
    private const string ProjectKey = "aaaaaaaa-aaaa-4aaa-aaaa-aaaaaaaaaaaa";
    private const string Source = "bbbbbbbb-bbbb-4bbb-bbbb-bbbbbbbbbbbb";
    private const string Target = "cccccccc-cccc-4ccc-cccc-cccccccccccc";

    [Fact]
    public async Task Boundary_ClaimsPresentsAndPersistsOnce_WithoutAcknowledging()
    {
        await using var fixture = await Fixture.CreateAsync();
        var message = await fixture.SendAsync();
        var inner = new RecordingAgent();
        await using var agent = AddressedTurnAgent.Wrap(inner, fixture.ScopeFactory);
        await SetupAsync(agent);

        await agent.RunTurnAsync("original task", false, default);
        inner.Tasks.Should().ContainSingle().Which.Should().Contain($"[message:{message.Id}")
            .And.Contain("Please check this");
        (await fixture.GetAsync(message.Id))!.Status.Should().Be(AddressedMessageStates.Delivered);
        (await fixture.GetAsync(message.Id))!.AcknowledgedAt.Should().BeNull();

        await agent.RunTurnAsync("next turn", true, default);
        inner.Tasks[1].Should().Be("next turn");
    }

    [Fact]
    public async Task FailedTurn_LeaseReclaim_ReplaysSameLogicalDeliveryId()
    {
        await using var fixture = await Fixture.CreateAsync();
        var message = await fixture.SendAsync();
        var first = new RecordingAgent { Fail = true };
        await using (var agent = AddressedTurnAgent.Wrap(first, fixture.ScopeFactory))
        {
            await SetupAsync(agent);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                agent.RunTurnAsync("initial", false, default));
        }

        (await fixture.GetAsync(message.Id))!.Status.Should().Be(AddressedMessageStates.Claimed);
        await fixture.UpdateAsync(db => db.AddressedMessages.Where(m => m.Id == message.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.ClaimedUntil, DateTimeOffset.UtcNow.AddSeconds(-1))));
        var replay = new RecordingAgent();
        await using (var agent = AddressedTurnAgent.Wrap(replay, fixture.ScopeFactory))
        {
            await SetupAsync(agent);
            await agent.RunTurnAsync("recovered", true, default);
        }
        first.Tasks[0].Should().Contain($"[message:{message.Id}");
        replay.Tasks[0].Should().Contain($"[message:{message.Id}");
        (await fixture.GetAsync(message.Id))!.Status.Should().Be(AddressedMessageStates.Delivered);
    }

    [Fact]
    public async Task Recovery_WaitsForPreviousClaim_InsteadOfSkippingAndEndingTurn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var message = await fixture.SendAsync();
        await fixture.UpdateAsync(async db =>
        {
            await db.AddressedMessages.Where(m => m.Id == message.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, AddressedMessageStates.Claimed)
                    .SetProperty(m => m.ClaimOwner, "interrupted-worker")
                    .SetProperty(m => m.ClaimedUntil, DateTimeOffset.UtcNow.AddSeconds(1))
                    .SetProperty(m => m.Fence, 1));
            return 1;
        });
        var inner = new RecordingAgent();
        await using var agent = AddressedTurnAgent.Wrap(inner, fixture.ScopeFactory);
        await SetupAsync(agent);
        await agent.RunTurnAsync("recovered", true, default);
        inner.Tasks.Should().ContainSingle().Which.Should().Contain($"[message:{message.Id}");
        (await fixture.GetAsync(message.Id))!.Status.Should().Be(AddressedMessageStates.Delivered);
    }

    [Fact]
    public async Task RetiredMember_CannotReceivePendingMessage()
    {
        await using var fixture = await Fixture.CreateAsync();
        var message = await fixture.SendAsync();
        var team = Path.Combine(fixture.WorkingDirectory, ".squad", "team.md");
        File.WriteAllText(team, File.ReadAllText(team).Replace("Alpha", "RetiredAlpha"));
        var inner = new RecordingAgent();
        await using var agent = AddressedTurnAgent.Wrap(inner, fixture.ScopeFactory);
        await SetupAsync(agent);
        await agent.RunTurnAsync("original", false, default);
        inner.Tasks.Should().ContainSingle().Which.Should().Be("original");
        (await fixture.GetAsync(message.Id))!.Status.Should().Be(AddressedMessageStates.Undeliverable);
    }

    [Fact]
    public async Task WrappingRemoteAgent_PreservesPreparedWritebackContract()
    {
        await using var fixture = await Fixture.CreateAsync();
        var inner = new RecordingWritebackAgent();
        await using var wrapped = AddressedTurnAgent.Wrap(inner, fixture.ScopeFactory);
        wrapped.Should().BeAssignableTo<IPreparedWritebackSource>();
        ((IPreparedWritebackSource)wrapped).TakePreparedWritebackEnvelope().Status
            .Should().Be(PreparedWritebackEnvelopeStatus.Missing);
    }

    private static Task SetupAsync(IWorkflowTurnAgent agent) =>
        agent.SetupAsync(".", ".", Target, null, null, null, ProjectKey, "Alpha",
            null, null, default);

    private class RecordingAgent : IWorkflowTurnAgent
    {
        public bool Fail { get; init; }
        public List<string> Tasks { get; } = [];
        public Task SetupAsync(string workingDirectory, string repositoryPath, string runId,
            string? modelId, string? systemPromptContext, ChannelWriter<RunEvent>? streamWriter,
            string? projectId, string? agentName, string? apiBaseUrl, string? apiKey,
            CancellationToken ct, string? userId = null) => Task.CompletedTask;
        public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct)
        {
            Tasks.Add(task);
            return Fail ? Task.FromException<string>(new InvalidOperationException("model failed"))
                : Task.FromResult("done");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingWritebackAgent : RecordingAgent, IPreparedWritebackSource
    {
        public PreparedWritebackEnvelope TakePreparedWritebackEnvelope() =>
            new(PreparedWritebackEnvelopeStatus.Missing);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ServiceProvider Services { get; }
        public IServiceScopeFactory ScopeFactory => Services.GetRequiredService<IServiceScopeFactory>();
        public string WorkingDirectory { get; }

        private Fixture(SqliteConnection connection, ServiceProvider services, string workingDirectory)
        {
            _connection = connection;
            Services = services;
            WorkingDirectory = workingDirectory;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "addressed-turn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            SquadTestFixtureHelper.CreateMinimalSquad(path);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<MemoryDbContext>().UseSqlite(connection).Options;
            await using (var db = new MemoryDbContext(options))
                await db.Database.EnsureCreatedAsync();
            var runs = DispatchProxy.Create<IRunStore, RunProxy>();
            ((RunProxy)runs).Runs = new()
            {
                [Source] = NewRun(Source, "Tank"),
                [Target] = NewRun(Target, "Alpha"),
            };
            var projectStore = DispatchProxy.Create<IProjectStore, ProjectProxy>();
            ((ProjectProxy)projectStore).Project = new Project
            {
                Id = ProjectId.Parse(ProjectKey), Name = "test", Origin = ProjectOrigin.Blank(),
                WorkingDirectory = path, DefaultBranch = "main", Owner = "test",
                ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
                State = ProjectState.Active, CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var services = new ServiceCollection()
                .AddDbContext<MemoryDbContext>(o => o.UseSqlite(connection))
                .AddSingleton(runs)
                .AddSingleton(projectStore)
                .AddScoped<AddressedMessageService>()
                .BuildServiceProvider();
            return new Fixture(connection, services, path);
        }

        public async Task<AddressedMessage> SendAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AddressedMessageService>()
                .SendAsync(ProjectKey, new VerifiedAuthor("Tank", "run", $"run:{Source}", Source, false),
                    new SendAddressedMessage("Alpha", Target, "Please check this", "send-1"),
                    _ => true, default);
        }

        public async Task<AddressedMessage?> GetAsync(string id)
        {
            await using var scope = Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AddressedMessageService>()
                .GetAsync(ProjectKey, id, default);
        }

        public async Task UpdateAsync(Func<MemoryDbContext, Task<int>> action)
        {
            await using var scope = Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<MemoryDbContext>());
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await _connection.DisposeAsync();
            Directory.Delete(WorkingDirectory, true);
        }

        private static Run NewRun(string id, string agent) => new()
        {
            Id = RunId.Parse(id), ProjectId = ProjectId.Parse(ProjectKey),
            AgentName = agent, RepositoryPath = ".", OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot, Task = "test",
            SubmittingUser = "test", Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
    }

    public class RunProxy : DispatchProxy
    {
        public Dictionary<string, Run> Runs { get; set; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IRunStore.GetAsync)
                ? Task.FromResult(Runs.GetValueOrDefault(args![0]!.ToString()!))
                : throw new NotSupportedException(method?.Name);
    }

    public class ProjectProxy : DispatchProxy
    {
        public Project? Project { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IProjectStore.GetAsync)
                ? Task.FromResult(Project)
                : throw new NotSupportedException(method?.Name);
    }
}
