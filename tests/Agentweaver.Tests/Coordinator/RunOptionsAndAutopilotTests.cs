using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Tests for the per-run Autopilot + auto-approve-tools options (Feature 008):
/// <list type="bullet">
/// <item>live toggle endpoints (POST /api/runs/{id}/auto-approve and /autopilot): 200 / 404 / 409;</item>
/// <item>both flags cascade from a coordinator run to its dispatched child;</item>
/// <item>when Autopilot is ON, a bubbled child question is auto-answered via the coordinator model
/// path (faked) and resolved on the child's <see cref="IQuestionGate"/>.</item>
/// </list>
/// </summary>
public sealed class RunOptionsAndAutopilotTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _other;

    public RunOptionsAndAutopilotTests()
    {
        _factory = new CoordinatorWebApplicationFactory();
        _owner = _factory.CreateOwnerClient();
        _other = _factory.CreateOtherClient();
    }

    public void Dispose()
    {
        _owner.Dispose();
        _other.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task AutoApproveEndpoint_ActiveRun_TogglesOption_And200()
    {
        var runId = await InsertInProgressRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/auto-approve", new { enabled = true });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var options = _factory.Services.GetRequiredService<IRunOptionsStore>();
        options.Get(runId).AutoApproveTools.Should().BeTrue();
    }

    [Fact]
    public async Task AutopilotEndpoint_ActiveRun_TogglesOption_And200()
    {
        var runId = await InsertInProgressRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsJsonAsync($"/api/runs/{runId}/autopilot", new { enabled = true });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var options = _factory.Services.GetRequiredService<IRunOptionsStore>();
        options.Get(runId).Autopilot.Should().BeTrue();
    }

    [Fact]
    public async Task ToggleEndpoints_UnknownRun_Return404()
    {
        var auto = await _owner.PostAsJsonAsync($"/api/runs/{RunId.New()}/auto-approve", new { enabled = true });
        auto.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var pilot = await _owner.PostAsJsonAsync($"/api/runs/{RunId.New()}/autopilot", new { enabled = true });
        pilot.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ToggleEndpoints_TerminalRun_Return409()
    {
        var runId = await InsertRunAsync(CoordinatorWebApplicationFactory.OwnerUser, RunStatus.Completed);

        var auto = await _owner.PostAsJsonAsync($"/api/runs/{runId}/auto-approve", new { enabled = true });
        auto.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var pilot = await _owner.PostAsJsonAsync($"/api/runs/{runId}/autopilot", new { enabled = true });
        pilot.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AutoApproveEndpoint_NonOwner_Returns403()
    {
        var runId = await InsertInProgressRunAsync(CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _other.PostAsJsonAsync($"/api/runs/{runId}/auto-approve", new { enabled = true });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void CascadeOptionsToChild_CopiesBothFlags()
    {
        var options = new InMemoryRunOptionsStore();
        options.Set("coord-1", new RunOptions(AutoApproveTools: true, Autopilot: true));
        var sut = NewDispatchService(new RunStreamStore(), options, autopilot: null);

        sut.CascadeOptionsToChild("coord-1", "child-1");

        var child = options.Get("child-1");
        child.AutoApproveTools.Should().BeTrue();
        child.Autopilot.Should().BeTrue("both flags must inherit from the coordinator at dispatch");
    }

    [Fact]
    public async Task BubbleChildQuestion_AutopilotOn_AutoAnswersViaModelPath()
    {
        const string coordinatorRunId = "coord-pilot-1";
        const string childRunId = "child-pilot-1";
        const int subtaskId = 4;
        const string requestId = "rq-pilot-1";

        var streamStore = new RunStreamStore();
        streamStore.Create(coordinatorRunId, "alice");

        var options = new InMemoryRunOptionsStore();
        options.Set(coordinatorRunId, new RunOptions(Autopilot: true));

        var gate = new InMemoryQuestionGate();
        var autopilot = new FakeAutopilot(gate, answer: "Use the existing pattern.");
        var sut = NewDispatchService(streamStore, options, autopilot);

        // Suspend on the child's question gate exactly as the ask_question tool would.
        var askTask = gate.AskAsync(childRunId, requestId, "Which pattern?", TimeSpan.FromMinutes(5), CancellationToken.None);

        var evt = new RunEvent(1, EventTypes.AgentQuestionAsked, new { requestId, question = "Which pattern?" });
        sut.BubbleChildInteraction(coordinatorRunId, subtaskId, childRunId, evt);

        var answer = await askTask.WaitAsync(TimeSpan.FromSeconds(5));
        answer.Should().Be("Use the existing pattern.",
            "Autopilot must auto-answer the bubbled child question and resolve the child gate");

        autopilot.LastChildRunId.Should().Be(childRunId);
        autopilot.LastRequestId.Should().Be(requestId);
    }

    [Fact]
    public void BubbleChildQuestion_AutopilotOff_DoesNotAutoAnswer()
    {
        const string coordinatorRunId = "coord-pilot-2";
        var streamStore = new RunStreamStore();
        streamStore.Create(coordinatorRunId, "alice");

        var options = new InMemoryRunOptionsStore(); // Autopilot OFF
        var gate = new InMemoryQuestionGate();
        var autopilot = new FakeAutopilot(gate, answer: "should not run");
        var sut = NewDispatchService(streamStore, options, autopilot);

        var evt = new RunEvent(1, EventTypes.AgentQuestionAsked, new { requestId = "rq", question = "Q?" });
        sut.BubbleChildInteraction(coordinatorRunId, 1, "child-2", evt);

        autopilot.Invoked.Should().BeFalse("Autopilot must not auto-answer when the option is OFF");
    }

    private static CoordinatorDispatchService NewDispatchService(
        RunStreamStore streamStore, IRunOptionsStore options, ICoordinatorAutopilot? autopilot) =>
        new(
            runStore: null!,
            streamStore,
            orchestrator: null!,
            worktreeManager: null!,
            steering: null!,
            assembly: null!,
            scopeFactory: null!,
            lifetime: new StubLifetime(),
            NullLogger<CoordinatorDispatchService>.Instance,
            runOptions: options,
            autopilot: autopilot);

    private sealed class FakeAutopilot : ICoordinatorAutopilot
    {
        private readonly IQuestionGate _gate;
        private readonly string _answer;
        public bool Invoked { get; private set; }
        public string? LastChildRunId { get; private set; }
        public string? LastRequestId { get; private set; }

        public FakeAutopilot(IQuestionGate gate, string answer)
        {
            _gate = gate;
            _answer = answer;
        }

        public Task TryAnswerChildQuestionAsync(
            string coordinatorRunId, string childRunId, int subtaskId, string requestId, string question, CancellationToken ct)
        {
            Invoked = true;
            LastChildRunId = childRunId;
            LastRequestId = requestId;
            _gate.Answer(childRunId, requestId, _answer);
            return Task.CompletedTask;
        }
    }

    private sealed class StubLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private Task<string> InsertInProgressRunAsync(string ownerUser)
        => InsertRunAsync(ownerUser, RunStatus.InProgress);

    private async Task<string> InsertRunAsync(string ownerUser, RunStatus status)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "options run",
            SubmittingUser = ownerUser,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "morpheus",
        };
        await runStore.InsertAsync(run, CancellationToken.None);
        return runId.ToString();
    }
}
