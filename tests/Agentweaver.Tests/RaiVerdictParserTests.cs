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
/// Unit tests for the robust Rai verdict parser (<see cref="RaiTurnExecutor.ParseVerdict"/> /
/// <see cref="RaiTurnExecutor.TryParseVerdict"/>). The reviewer is prompted to "Issue exactly one
/// verdict on its own line: GREEN / YELLOW / REVISE / RED". The parser must read the declared
/// verdict from the verdict <em>line</em> (or unambiguous emoji), never from a loose substring
/// scan — that loose scan was a false-positive machine that classified benign GREEN responses as
/// RED (e.g. "GREEN — no RED-level issues found") and dead-ended whole orchestrations.
///
/// This is a pure string parser exercised directly (no mock/fake, no live Copilot agent), so it
/// honours constitution rule VII while giving exhaustive verdict coverage.
/// </summary>
public sealed class RaiVerdictParserTests
{
    [Fact]
    public void CleanRedLine_ParsesAsRed()
    {
        var response = "RED — credentials committed in src/config.cs, must block shipping.";

        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeTrue();
        verdict.Should().Be(RaiVerdict.Red);
    }

    [Fact]
    public void RedEmojiLine_ParsesAsRed()
    {
        var response = "🔴 Critical PII exposure detected in the diff.";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Red);
    }

    [Fact]
    public void YellowEmojiLine_ParsesAsYellow()
    {
        var response = "🟡 Minor concern: a TODO references an internal hostname.";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Yellow);
    }

    [Fact]
    public void GreenWithProseMentionOfRed_ParsesAsGreen_RegressionGuard()
    {
        // The exact false-positive that dead-ended orchestrations: a benign GREEN whose prose
        // mentions "RED-level" (hyphenated, mid-sentence) must NOT be classified RED.
        var response = "GREEN — no RED-level issues found, no credentials, no PII.";

        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeTrue();
        verdict.Should().Be(RaiVerdict.Green);
    }

    [Fact]
    public void ProseMentioningRedWithoutVerdictLine_DoesNotFlagRed()
    {
        var response =
            "The change looks safe overall.\n" +
            "There are no RED flags or credential leaks anywhere in the diff.\n" +
            "GREEN";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Green);
    }

    [Fact]
    public void ReviseLineFollowedByFeedback_ParsesAsRevise()
    {
        var response = "REVISE\nThe new endpoint lacks input validation — sanitize the path param.";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Revise);
    }

    [Fact]
    public void YellowLine_ParsesAsYellow()
    {
        var response = "YELLOW — ship with caution, add a follow-up to rotate the demo token.";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Yellow);
    }

    [Fact]
    public void VerdictOnLaterOwnLine_AfterPreamble_ParsesCorrectly()
    {
        var response =
            "I reviewed the diff for security, PII and harmful content.\n" +
            "Summary: the migration script touches only test fixtures.\n" +
            "RED — it also rewrites ~/.ssh/known_hosts, which is destructive.";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Red);
    }

    [Fact]
    public void BulletAndBoldMarkers_AreStrippedBeforeMatching()
    {
        RaiTurnExecutor.ParseVerdict("- RED — blocking issue").Should().Be(RaiVerdict.Red);
        RaiTurnExecutor.ParseVerdict("**REVISE** please fix").Should().Be(RaiVerdict.Revise);
        RaiTurnExecutor.ParseVerdict("* GREEN — looks good").Should().Be(RaiVerdict.Green);
    }

    [Fact]
    public void MultipleVerdictLines_HighestSeverityWins()
    {
        // Defensive: if the agent emits more than one verdict line, the most conservative wins.
        var response =
            "GREEN — most of the diff is fine\n" +
            "REVISE — but the auth header is logged in plaintext";

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Revise);

        var withRed =
            "YELLOW — small nit\n" +
            "RED — hardcoded production secret";
        RaiTurnExecutor.ParseVerdict(withRed).Should().Be(RaiVerdict.Red);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void EmptyOrUnparseable_FailsOpenToYellow(string? response)
    {
        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeFalse(
            "an unparseable verdict must be reported as a miss so the caller can log a warning");
        verdict.Should().Be(RaiVerdict.Yellow, "advisory safety checks default to yellow — cautious but non-blocking");

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Yellow);
    }

    [Fact]
    public void NonEmptyUnparseable_ParseVerdictReturnsYellow_OutVerdictIsGreen()
    {
        // A non-empty response with no recognizable verdict token: TryParseVerdict returns false
        // and the out parameter is Green (the default `best` value when no lines matched).
        // ParseVerdict wraps this: when TryParseVerdict returns false it always returns Yellow.
        const string response = "I'm not sure how to rate this; the diff is empty.";
        RaiTurnExecutor.TryParseVerdict(response, out var verdict).Should().BeFalse(
            "an unparseable verdict must be reported as a miss so the caller can log a warning");
        verdict.Should().Be(RaiVerdict.Green,
            "no verdict lines found: out parameter is the default 'best' value (Green)");

        RaiTurnExecutor.ParseVerdict(response).Should().Be(RaiVerdict.Yellow,
            "ParseVerdict applies the advisory Yellow default when TryParseVerdict returns false");
    }

    [Fact]
    public async Task HandleAsync_EmitsVerdictPayload_ToParentRunAndRaiSubStream()
    {
        var parent = Channel.CreateUnbounded<RunEvent>();
        var sub = Channel.CreateUnbounded<RunEvent>();
        var executor = new RaiTurnExecutor(
            new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), new NullGitHubTokenStore(), new FixedInstallationScopeStub()),
            new FixedInstallationScopeStub(),
            new PassthroughExecutor("test"),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLoggerFactory.Instance,
            getRecordingWriter: _ => parent.Writer,
            createSubStream: (_, _) => sub.Writer,
            completeSubStream: _ => sub.Writer.TryComplete(),
            agentFactory: new FakeWorkflowAgentFactory(new TestFileEditAgentRunner()));

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
}
