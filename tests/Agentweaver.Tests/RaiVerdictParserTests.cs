using FluentAssertions;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;

using RaiVerdict = Agentweaver.AgentRuntime.Workflow.RaiTurnExecutor.RaiVerdict;

namespace Agentweaver.Tests;

/// <summary>
/// Unit tests for the structured, sentinel-anchored Rai verdict parser
/// (<see cref="RaiTurnExecutor.TryParseVerdict"/> / <see cref="RaiTurnExecutor.TryParseSentinelVerdict"/>).
///
/// <para>
/// The reviewer is now prompted to end its response with a single machine-readable line
/// <c>VERDICT: &lt;GREEN|YELLOW|REVISE|RED&gt;</c>. That sentinel is AUTHORITATIVE: when present the
/// human prose is NEVER scanned for verdict tokens. This is the ROOT fix for GitHub #231 — the
/// previous heuristic prose scan mis-read the prompt's own legend bullet ("RED — critical
/// violation...") and section headers ("RED: none", "RED flags: none") as RED verdicts, escalating
/// a benign REVISE/GREEN to a hard RED and dead-ending whole orchestrations (2nd occurrence of the
/// same bug class — the whole-word guard only rejected mid-sentence/hyphenated prose, not a line
/// that STARTS with a token).
/// </para>
///
/// <para>
/// This is a pure string parser exercised directly (no mock/fake, no live Copilot agent) for the
/// verdict-parsing cases; the HandleAsync-level tests exercise the bounded re-ask + fail-safe-to-RED
/// safety path against deterministic scripted agents.
/// </para>
/// </summary>
public sealed class RaiVerdictParserTests
{
    // ---- Sentinel happy paths ------------------------------------------------------------

    [Theory]
    [InlineData("VERDICT: GREEN", (int)RaiVerdict.Green)]
    [InlineData("VERDICT: YELLOW", (int)RaiVerdict.Yellow)]
    [InlineData("VERDICT: REVISE", (int)RaiVerdict.Revise)]
    [InlineData("VERDICT: RED", (int)RaiVerdict.Red)]
    public void SentinelOnFinalLine_AfterExplanation_ParsesDeclaredVerdict(string sentinel, int expected)
    {
        var response =
            "I reviewed the diff for security, PII, and harmful content.\n" +
            "The change only touches a board-game scoring helper and documentation.\n" +
            sentinel;

        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeTrue();
        verdict.Should().Be((RaiVerdict)expected);
    }

    // ---- THE #231 REPRO (money test) -----------------------------------------------------

    [Fact]
    public void Issue231_LegendEchoAndRedNoneHeader_WithReviseSentinel_ParsesRevise_NotRed()
    {
        // Reproduces run 72b8b6f7: a completely benign board-game app whose reviewer echoed the
        // prompt's legend bullets and wrote a "RED: none" header, and opened with a REVISE line.
        // The old max-severity prose scan miscounted the legend "- RED — critical violation..." and
        // the "RED: none" header as RED verdicts. Under the sentinel contract the trailing
        // VERDICT: REVISE is authoritative and prose is NOT scanned.
        var response =
            "REVISE — minor cleanups before shipping.\n" +
            "\n" +
            "Verdict legend:\n" +
            "- GREEN  — no issues, safe to ship\n" +
            "- YELLOW — minor concerns, ship with caution\n" +
            "- REVISE — fixable issues found; revise before shipping\n" +
            "- RED    — critical violation that must block shipping (e.g. credentials, PII)\n" +
            "\n" +
            "RED: none — no credentials, PII, or harmful content detected.\n" +
            "RED flags: none.\n" +
            "\n" +
            "VERDICT: REVISE";

        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeTrue();
        verdict.Should().Be(
            RaiVerdict.Revise,
            "the VERDICT: sentinel is authoritative — legend bullets and 'RED: none' headers must NOT escalate a benign REVISE to RED");
    }

    [Theory]
    [InlineData("RED: none — no credentials, PII")]
    [InlineData("RED flags: none")]
    public void RedNoneHeader_WithGreenSentinel_ParsesGreen(string header)
    {
        var response = header + "\nVERDICT: GREEN";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Green);
    }

    [Fact]
    public void GreenAndYellowSectionHeaders_WithRedSentinel_ParsesRed_GenuineRedStillBlocks()
    {
        var response =
            "GREEN: n/a\n" +
            "YELLOW: a couple of style nits\n" +
            "RED: hardcoded production secret committed in appsettings.json\n" +
            "VERDICT: RED";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Red);
    }

    // ---- Multiple / ambiguous sentinels --------------------------------------------------

    [Fact]
    public void MultipleSentinelLines_LastOneWins()
    {
        var escalate =
            "VERDICT: GREEN\n" +
            "On reflection I found a leaked token further down.\n" +
            "VERDICT: RED";
        RaiTurnExecutor.ParseVerdict(escalate).Should().Be(RaiVerdict.Red);

        var downgrade =
            "VERDICT: RED\n" +
            "Actually that string is a test fixture, not a real secret.\n" +
            "VERDICT: GREEN";
        RaiTurnExecutor.ParseVerdict(downgrade).Should().Be(RaiVerdict.Green);
    }

    [Fact]
    public void AmbiguousSentinelLine_MostSevereWins()
    {
        // A single sentinel line that (defensively) names more than one token stays conservative.
        RaiTurnExecutor.ParseVerdict("VERDICT: GREEN / RED").Should().Be(RaiVerdict.Red);
    }

    // ---- Marker stripping / case-insensitivity on the sentinel ---------------------------

    [Fact]
    public void SentinelWithBulletOrBoldMarkers_StillParses()
    {
        RaiTurnExecutor.ParseVerdict("- VERDICT: RED").Should().Be(RaiVerdict.Red);
        RaiTurnExecutor.ParseVerdict("**VERDICT: REVISE**").Should().Be(RaiVerdict.Revise);
        RaiTurnExecutor.ParseVerdict("* VERDICT: GREEN").Should().Be(RaiVerdict.Green);
        RaiTurnExecutor.ParseVerdict("verdict: yellow").Should().Be(RaiVerdict.Yellow);
    }

    // ---- Emoji escalation signal (retained) ----------------------------------------------

    [Fact]
    public void RedEmojiWithoutSentinel_ParsesAsRed_GenuineRedStillBlocks()
    {
        var response = "🔴 Critical PII exposure detected in the diff.";

        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeTrue();
        verdict.Should().Be(RaiVerdict.Red);
    }

    [Fact]
    public void YellowEmojiWithoutSentinel_ParsesAsYellow()
    {
        var response = "🟡 Minor concern: a TODO references an internal hostname.";

        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeTrue();
        verdict.Should().Be(RaiVerdict.Yellow);
    }

    // ---- Regression guards: prose mentioning RED must NOT flag under the sentinel ----------

    [Fact]
    public void GreenWithProseMentionOfRed_WithSentinel_ParsesGreen_RegressionGuard()
    {
        // The exact false-positive that dead-ended orchestrations: a benign GREEN whose prose
        // mentions "RED-level" must NOT be classified RED. With the sentinel it is Green.
        var response = "GREEN — no RED-level issues found, no credentials, no PII.\nVERDICT: GREEN";

        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeTrue();
        verdict.Should().Be(RaiVerdict.Green);
    }

    [Fact]
    public void ProseMentioningRedFlags_WithGreenSentinel_ParsesGreen()
    {
        var response =
            "The change looks safe overall.\n" +
            "There are no RED flags or credential leaks anywhere in the diff.\n" +
            "VERDICT: GREEN";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Green);
    }

    [Fact]
    public void ReviseWithFeedback_AndSentinel_ParsesRevise()
    {
        var response =
            "The new endpoint lacks input validation — sanitize the path param.\n" +
            "VERDICT: REVISE";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Revise);
    }

    // ---- Bare token lines WITHOUT a sentinel are now unparseable (rely on re-ask/fail-safe) ----

    [Fact]
    public void BareRedLine_WithoutSentinel_IsUnparseable()
    {
        // Under the sentinel contract a bare "RED — ..." with no VERDICT: line is NOT authoritative:
        // TryParseVerdict returns false, and HandleAsync's bounded re-ask + fail-safe (which BLOCKS)
        // preserves the safety intent.
        var response = "RED — credentials committed in src/config.cs, must block shipping.";

        RaiTurnExecutor.TryParseVerdict(response, out _).Should().BeFalse();
    }

    [Fact]
    public void SentinelRed_StillBlocks()
    {
        var response = "Found hardcoded credentials in src/config.cs.\nVERDICT: RED";

        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeTrue();
        verdict.Should().Be(RaiVerdict.Red);
    }

    [Fact]
    public void NoSentinel_PlainBenignProse_IsUnparseable()
    {
        var response =
            "The change adds a board-game scoring helper. No credentials, no PII, no harmful content.\n" +
            "This looks safe to ship.";

        RaiTurnExecutor.TryParseVerdict(response, out _).Should().BeFalse(
            "without a VERDICT: sentinel the response is unparseable and must drive the re-ask / fail-safe");
    }

    // ---- Empty / unparseable defaults ----------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void EmptyOrWhitespace_IsMiss_DefaultsYellow(string? response)
    {
        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeFalse(
            "an unparseable verdict must be reported as a miss so the caller can re-ask / fail safe");
        verdict.Should().Be(RaiVerdict.Yellow, "the out default is a non-authoritative advisory value; the real safety decision is made in HandleAsync");

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Yellow);
    }

    [Fact]
    public void NonEmptyUnparseable_IsMiss_OutVerdictDefaultsYellow()
    {
        const string response = "I'm not sure how to rate this; the diff is empty.";

        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeFalse(
            "an unparseable verdict must be reported as a miss so the caller can re-ask / fail safe");
        verdict.Should().Be(RaiVerdict.Yellow, "no sentinel and no emoji: the out default is Yellow (non-authoritative)");

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Yellow);
    }

    [Fact]
    public void TryParseSentinelVerdict_NoSentinel_ReturnsFalse()
    {
        RaiTurnExecutor.TryParseSentinelVerdict("GREEN — looks fine, no RED-level issues", out _)
            .Should().BeFalse("a bare verdict word is not the machine-readable VERDICT: sentinel");
    }

    // ---- HandleAsync integration: happy-path verdict payload ------------------------------

    [Fact]
    public async Task HandleAsync_EmitsVerdictPayload_ToParentRunAndRaiSubStream()
    {
        var parent = Channel.CreateUnbounded<RunEvent>();
        var sub = Channel.CreateUnbounded<RunEvent>();
        var executor = BuildExecutor(
            parent, sub,
            new FakeWorkflowAgentFactory(new TestFileEditAgentRunner()));

        await executor.HandleAsync(new AgentTurnOutput(
            RunId: "rai-verdict-run",
            TreeHash: "tree",
            Diff: "diff --git a/file.txt b/file.txt\n+safe change",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agent/rai-verdict-run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false),
            context: null!,
            CancellationToken.None);

        var parentVerdict = Drain(parent.Reader).Single(e => e.Type == EventTypes.RaiVerdict);
        var subVerdict = Drain(sub.Reader).Single(e => e.Type == EventTypes.RaiVerdict);

        AssertGreenVerdictPayload(parentVerdict.Payload);
        AssertGreenVerdictPayload(subVerdict.Payload);
    }

    // ---- HandleAsync integration: bounded re-ask + fail-safe (INV-3) ----------------------

    [Fact]
    public async Task HandleAsync_UnparseableThenUnparseable_IssuesOneReask_ThenFailsSafeToRed()
    {
        var parent = Channel.CreateUnbounded<RunEvent>();
        var sub = Channel.CreateUnbounded<RunEvent>();
        var factory = new ScriptedRaiAgentFactory(
            "The board-game app looks fine. No credentials or PII.", // first turn: no sentinel
            "Still fine, shipping it.");                             // re-ask: still no sentinel
        var executor = BuildExecutor(parent, sub, factory);

        var result = await executor.HandleAsync(new AgentTurnOutput(
            RunId: "rai-failsafe-run",
            TreeHash: "tree",
            Diff: "diff --git a/game.cs b/game.cs\n+// benign board game",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agent/rai-failsafe-run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false),
            context: null!,
            CancellationToken.None);

        result.ContentSafetyFlagged.Should().BeTrue(
            "an unparseable verdict after the single bounded re-ask must fail safe to RED, never YELLOW-ship");
        factory.LastRai!.TurnCount.Should().Be(2, "exactly ONE bounded re-ask follows the initial turn");

        var events = Drain(parent.Reader);
        var raiError = events.Should().ContainSingle(e => e.Type == "run.rai_error").Subject;
        JsonProperty(raiError.Payload, "reason").Should().Be("unparseable_after_reask");
    }

    [Fact]
    public async Task HandleAsync_UnparseableThenSentinel_RecoversViaReask_DoesNotFlag()
    {
        var parent = Channel.CreateUnbounded<RunEvent>();
        var sub = Channel.CreateUnbounded<RunEvent>();
        var factory = new ScriptedRaiAgentFactory(
            "The board-game app looks fine. No credentials or PII.", // first turn: no sentinel
            "VERDICT: GREEN");                                       // re-ask: sentinel present
        var executor = BuildExecutor(parent, sub, factory);

        var result = await executor.HandleAsync(new AgentTurnOutput(
            RunId: "rai-reask-run",
            TreeHash: "tree",
            Diff: "diff --git a/game.cs b/game.cs\n+// benign board game",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agent/rai-reask-run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false),
            context: null!,
            CancellationToken.None);

        result.ContentSafetyFlagged.Should().BeFalse("the bounded re-ask recovered a GREEN sentinel");
        factory.LastRai!.TurnCount.Should().Be(2, "one re-ask was needed to recover the verdict");

        var verdict = Drain(parent.Reader).Single(e => e.Type == EventTypes.RaiVerdict);
        JsonProperty(verdict.Payload, "verdict").Should().Be("green");
    }

    // ---- HandleAsync integration: raw JSON responses must never leak into the rationale --

    [Fact]
    public async Task HandleAsync_RawJsonResponseWithSentinel_DoesNotLeakJsonAsRationale()
    {
        // Reproduces the reported bug: the reviewer's response starts with a JSON-shaped blob
        // (e.g. it echoed a structured work-plan/diff back) instead of a prose explanation, but
        // still ends with a valid VERDICT: sentinel — no re-ask is needed, so ExtractRationale runs
        // on this raw response. The naive "first non-blank line" fallback used to surface the raw
        // JSON verbatim as the human-facing rationale; it must now fall back to a safe default.
        var parent = Channel.CreateUnbounded<RunEvent>();
        var sub = Channel.CreateUnbounded<RunEvent>();
        var factory = new ScriptedRaiAgentFactory(
            "[{\"title\":\"Analyze and classify the five support tickets\",\"scope\":\"Working from the raw ticket queue\"}]\n" +
            "VERDICT: GREEN");
        var executor = BuildExecutor(parent, sub, factory);

        var result = await executor.HandleAsync(new AgentTurnOutput(
            RunId: "rai-json-leak-run",
            TreeHash: "tree",
            Diff: "diff --git a/plan.json b/plan.json\n+[{\"title\":\"Analyze tickets\"}]",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agent/rai-json-leak-run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false),
            context: null!,
            CancellationToken.None);

        result.ContentSafetyFlagged.Should().BeFalse("the sentinel parsed cleanly as GREEN");

        var verdict = Drain(parent.Reader).Single(e => e.Type == EventTypes.RaiVerdict);
        JsonProperty(verdict.Payload, "verdict").Should().Be("green");
        var rationale = JsonProperty(verdict.Payload, "rationale");
        rationale.Should().NotBeNullOrEmpty();
        rationale.Should().NotContain("{", "raw JSON must never be surfaced verbatim as a human-facing rationale");
        rationale.Should().NotContain("support tickets", "the raw echoed payload text must not leak through");
    }

    [Fact]
    public async Task HandleAsync_UnparseableJsonThenSentinel_RecoversViaReask_DoesNotLeakJsonAsRationale()
    {
        // Same JSON-leak scenario, but this time the first turn has NO sentinel at all (so the
        // bounded re-ask fires) and EmitVerdict is still passed the ORIGINAL (non-conforming, raw
        // JSON) response for rationale extraction — this must not leak the JSON either.
        var parent = Channel.CreateUnbounded<RunEvent>();
        var sub = Channel.CreateUnbounded<RunEvent>();
        var factory = new ScriptedRaiAgentFactory(
            "[{\"title\":\"Analyze and classify the five support tickets\",\"scope\":\"raw ticket queue\"}]", // no sentinel
            "VERDICT: GREEN"); // re-ask recovers the verdict
        var executor = BuildExecutor(parent, sub, factory);

        var result = await executor.HandleAsync(new AgentTurnOutput(
            RunId: "rai-json-leak-reask-run",
            TreeHash: "tree",
            Diff: "diff --git a/plan.json b/plan.json\n+[{\"title\":\"Analyze tickets\"}]",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agent/rai-json-leak-reask-run",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false),
            context: null!,
            CancellationToken.None);

        result.ContentSafetyFlagged.Should().BeFalse("the bounded re-ask recovered a GREEN sentinel");

        var verdict = Drain(parent.Reader).Single(e => e.Type == EventTypes.RaiVerdict);
        JsonProperty(verdict.Payload, "verdict").Should().Be("green");
        var rationale = JsonProperty(verdict.Payload, "rationale");
        rationale.Should().NotBeNullOrEmpty();
        rationale.Should().NotContain("{", "raw JSON must never be surfaced verbatim as a human-facing rationale");
        rationale.Should().NotContain("support tickets", "the raw echoed payload text must not leak through");
    }

    // ---- helpers -------------------------------------------------------------------------

    private static RaiTurnExecutor BuildExecutor(
        Channel<RunEvent> parent,
        Channel<RunEvent> sub,
        IWorkflowAgentFactory agentFactory) =>
        new(
            new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), new FixedGitHubCopilotCapabilityCredentialProvider()),
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            getRecordingWriter: _ => parent.Writer,
            createSubStream: (_, _) => sub.Writer,
            completeSubStream: _ => sub.Writer.TryComplete(),
            agentFactory: agentFactory);

    private static List<RunEvent> Drain(ChannelReader<RunEvent> reader)
    {
        var events = new List<RunEvent>();
        while (reader.TryRead(out var evt))
            events.Add(evt);
        return events;
    }

    private static void AssertGreenVerdictPayload(object payload)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = doc.RootElement;
        root.GetProperty("verdict").GetString().Should().Be("green");
        root.TryGetProperty("trafficLight", out _).Should().BeFalse();
        root.GetProperty("rationale").GetString().Should().Be("no issues, safe to ship.");
        root.GetProperty("runId").GetString().Should().Be("rai-verdict-run");
    }

    private static string? JsonProperty(object payload, string name)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return doc.RootElement.GetProperty(name).GetString();
    }

    /// <summary>
    /// Deterministic <see cref="IWorkflowAgentFactory"/> whose Rai agent replays a scripted queue of
    /// responses (the last one repeats once exhausted). Used to exercise the HandleAsync bounded
    /// re-ask + fail-safe path without any live Copilot agent. Only the Rai agent is produced; the
    /// other roles throw because RaiTurnExecutor never asks for them.
    /// </summary>
    private sealed class ScriptedRaiAgentFactory : IWorkflowAgentFactory
    {
        private readonly string[] _responses;

        public ScriptedRaiAgentFactory(params string[] responses) => _responses = responses;

        public ScriptedRaiAgent? LastRai { get; private set; }

        public IWorkflowTurnAgent CreateWorkerAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateRaiAgent() => LastRai = new ScriptedRaiAgent(_responses);
        public IWorkflowTurnAgent CreateRubberduckAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateBuildTestAgent() => throw new NotSupportedException();
        public IWorkflowTurnAgent CreateScribeAgent() => throw new NotSupportedException();
    }

    private sealed class ScriptedRaiAgent : IWorkflowTurnAgent
    {
        private readonly Queue<string> _responses;
        private string _last = string.Empty;

        public ScriptedRaiAgent(IEnumerable<string> responses) => _responses = new Queue<string>(responses);

        public int TurnCount { get; private set; }

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
            string? userId = null) => Task.CompletedTask;

        public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct)
        {
            TurnCount++;
            if (_responses.Count > 0)
                _last = _responses.Dequeue();
            return Task.FromResult(_last);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
