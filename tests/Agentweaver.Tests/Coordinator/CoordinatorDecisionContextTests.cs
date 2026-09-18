using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.Domain;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Coordinator;

public sealed class CoordinatorDecisionContextTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _services;

    public CoordinatorDecisionContextTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(options => options.UseSqlite(_connection));
        services.AddScoped<MemoryContextCompiler>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
    }

    [Fact]
    public async Task DecompositionPrompt_UsesCompilerTrustFiltersOrderingAndSingleFormatting()
    {
        const string projectId = "project-decisions";
        var now = DateTimeOffset.UtcNow;
        var agentFactory = new CapturingWorkflowAgentFactory();
        var executor = CreateExecutor(agentFactory);

        var approvedArchitectural = Decision(
            projectId, "Approved architectural", "architectural", "active",
            MemoryTrustStates.Approved, now.AddMinutes(-4));
        var approvedScope = Decision(
            projectId, "Approved scope", "scope", "active",
            MemoryTrustStates.Approved, now.AddMinutes(-2));
        var pending = Decision(
            projectId, "Pending architectural", "architectural", "active",
            MemoryTrustStates.Pending, now.AddMinutes(-3));

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.Decisions.AddRange(
                approvedArchitectural,
                approvedScope,
                pending,
                Decision(projectId, "Legacy scope", "scope", "active",
                    MemoryTrustStates.Legacy, now.AddMinutes(-5)),
                Decision(projectId, "Superseded architectural", "architectural", "superseded",
                    MemoryTrustStates.Approved, now.AddMinutes(-6)),
                Decision(projectId, "Approved operational", "operational", "active",
                    MemoryTrustStates.Approved, now.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        await DecomposeAsync(executor, projectId, "run-1");

        var firstPrompt = agentFactory.Agent.SystemPrompt;
        firstPrompt.Should().NotBeNull();
        firstPrompt.Should().Contain("BEGIN_AGENTWEAVER_UNTRUSTED_CONTEXT_JSON");
        firstPrompt.Should().Contain("END_AGENTWEAVER_UNTRUSTED_CONTEXT_JSON");

        var firstTitles = DecisionTitles(firstPrompt);
        firstTitles.Should().Equal("Approved architectural", "Approved scope");
        firstTitles.Should().OnlyHaveUniqueItems();
        firstTitles.Should().OnlyContain(title => CountOccurrences(firstPrompt!, title) == 1,
            "each approved decision must be injected into the final coordinator prompt exactly once");

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var storedPending = await db.Decisions.SingleAsync(d => d.Title == pending.Title);
            storedPending.TrustState = MemoryTrustStates.Approved;
            storedPending.ApprovedBy = "human:owner";
            storedPending.ApprovedAt = now;
            await db.SaveChangesAsync();
        }

        await DecomposeAsync(executor, projectId, "run-2");

        var secondPrompt = agentFactory.Agent.SystemPrompt;
        secondPrompt.Should().NotBeNull();
        var secondTitles = DecisionTitles(secondPrompt);
        secondTitles.Should().Equal(
            "Approved architectural", "Pending architectural", "Approved scope");
        secondTitles.Should().OnlyHaveUniqueItems();
        secondTitles.Should().OnlyContain(title => CountOccurrences(secondPrompt!, title) == 1,
            "each approved decision must be injected into the final coordinator prompt exactly once");
    }

    [Fact]
    public async Task DecompositionPrompt_OversizedApprovedDecisionRetainsValidBoundedJsonContext()
    {
        const string projectId = "project-oversized-decision";
        const string title = "Non-negotiable deployment boundary";
        const string oversizedContentPrefix = "oversized-approved-decision-content-";
        var decision = Decision(
            projectId, title, "architectural", "active",
            MemoryTrustStates.Approved, DateTimeOffset.UtcNow);
        decision.Content = oversizedContentPrefix + new string('x', 500_000);
        var agentFactory = new CapturingWorkflowAgentFactory();
        var executor = CreateExecutor(agentFactory);

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.Decisions.Add(decision);
            await db.SaveChangesAsync();
        }

        await DecomposeAsync(executor, projectId, "oversized-decision-run");

        var prompt = agentFactory.Agent.SystemPrompt;
        prompt.Should().NotBeNull();
        prompt.Should().Contain("BEGIN_AGENTWEAVER_UNTRUSTED_CONTEXT_JSON");
        prompt.Should().Contain("END_AGENTWEAVER_UNTRUSTED_CONTEXT_JSON");
        prompt.Should().NotContain(oversizedContentPrefix);
        prompt.Should().NotContain("[Context truncated to fit the decomposition model window.]");
        prompt!.Length.Should().BeLessThan(96_000 * 4);

        var titles = DecisionTitles(prompt);
        titles.Should().ContainSingle().Which.Should().Be(title);
    }

    private async Task DecomposeAsync(
        CoordinatorOrchestratorExecutor executor,
        string projectId,
        string runId)
    {
        var input = new CoordinatorDraftInput(
            runId, projectId, "Capture the coordinator decomposition prompt.", "owner", ".", null);
        var spec = new OutcomeSpec
        {
            ProjectId = projectId,
            CoordinatorRunId = runId,
            Goal = input.Goal,
            DesiredOutcome = input.Goal,
            Scope = "Capture the final prompt.",
            Assumptions = "None",
            Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        (await executor.DecomposeWithModelAsync(input, spec, null, CancellationToken.None))
            .Should().NotBeNull();
    }

    private CoordinatorOrchestratorExecutor CreateExecutor(IWorkflowAgentFactory agentFactory) => new(
        agentFactory,
        new RunStreamStore(),
        _services.GetRequiredService<IServiceScopeFactory>(),
        NullLoggerFactory.Instance,
        new FakeStoryIndependenceClassifier(),
        new FakeAssemblyGateCodeClassifier(),
        "gpt-5-mini",
        "http://localhost",
        null);

    private static Decision Decision(
        string projectId,
        string title,
        string type,
        string status,
        string trustState,
        DateTimeOffset createdAt) => new()
        {
            ProjectId = projectId,
            AgentName = "Coordinator",
            Type = type,
            Status = status,
            Title = title,
            Content = $"decision-content-{createdAt:O}",
            TrustState = trustState,
            SourceKind = MemorySourceKinds.Run,
            SourceIdentity = "run:coordinator",
            ApprovedBy = trustState == MemoryTrustStates.Approved ? "human:owner" : null,
            ApprovedAt = trustState == MemoryTrustStates.Approved ? createdAt : null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

    private static string[] DecisionTitles(string? compiled)
    {
        compiled.Should().NotBeNull();
        var lines = compiled!.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = Array.IndexOf(lines, "BEGIN_AGENTWEAVER_UNTRUSTED_CONTEXT_JSON");
        var finish = Array.IndexOf(lines, "END_AGENTWEAVER_UNTRUSTED_CONTEXT_JSON");
        using var payload = JsonDocument.Parse(string.Join('\n', lines[(start + 1)..finish]));
        return payload.RootElement.GetProperty("decisions")
            .EnumerateArray()
            .Select(decision => decision.GetProperty("Title").GetString()!)
            .ToArray();
    }

    private static int CountOccurrences(string value, string text) =>
        value.Split(text, StringSplitOptions.None).Length - 1;

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class CapturingWorkflowAgentFactory : IWorkflowAgentFactory
    {
        public CapturingWorkflowTurnAgent Agent { get; } = new();

        public IWorkflowTurnAgent CreateWorkerAgent() => Agent;
        public IWorkflowTurnAgent CreateRaiAgent() => Agent;
        public IWorkflowTurnAgent CreateRubberduckAgent() => Agent;
        public IWorkflowTurnAgent CreateBuildTestAgent() => Agent;
        public IWorkflowTurnAgent CreateScribeAgent() => Agent;
    }

    private sealed class CapturingWorkflowTurnAgent : IWorkflowTurnAgent
    {
        public string? SystemPrompt { get; private set; }

        public Task SetupAsync(
            string workingDirectory,
            string repositoryPath,
            string runId,
            string? modelId,
            string? systemPromptContext,
            ChannelWriter<RunEvent>? streamWriter,
            string? projectId,
            string? agentName,
            string? apiBaseUrl,
            string? apiKey,
            CancellationToken ct,
            string? userId = null)
        {
            SystemPrompt = systemPromptContext;
            return Task.CompletedTask;
        }

        public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct) =>
            Task.FromResult(
                """[{"story_key":"prompt-capture","title":"Capture prompt","scope":"Capture the final prompt.","role":"lead-architect","complexity":"low","phase":"planning","isolation":"shared","depends_on":[]}]""");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
