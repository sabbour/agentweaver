extern alias agenthost;

using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentHostRunConfiguration = agenthost::Agentweaver.AgentHost.AgentHostRunConfiguration;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using RunOptions = Agentweaver.Domain.RunOptions;

namespace Agentweaver.Tests.Runtime;

public sealed class EffectivePermissionBindingTests : IDisposable
{
    private const string RunId = "permission-binding-run";
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), $"permission-binding-{Guid.NewGuid():N}");

    public EffectivePermissionBindingTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task ReadOnlyBinding_AllowsReadAndDeniesWrite_EquallyLocallyAndAfterAgentHostTransport()
    {
        var binding = ReadOnlyBinding();
        var local = BuildAgent(binding, autoApproveTools: false);

        var runtimeState = new AgentHostRuntimeState();
        runtimeState.TryConfigure(new AgentHostRunConfiguration(
            RunId,
            "user",
            "turn-token",
            CopilotCredential: null,
            PreviewRunnerCredential: null,
            SharedWorkingDirectory: _workspace,
            EffectivePermissionBinding: binding)).Should().BeTrue();
        var remote = BuildAgent(runtimeState.EffectivePermissionBinding, autoApproveTools: false);

        foreach (var agent in new[] { local, remote })
        {
            using var governance = BuildGovernance();
            var handler = BuildHandler(agent, governance);

            (await handler(CustomTool("read_file", new { path = "README.md" }), new PermissionInvocation()))
                .Should().BeOfType<PermissionDecisionApproveOnce>();
            (await handler(CustomTool("write_file", new { path = "README.md", content = "changed" }), new PermissionInvocation()))
                .Should().NotBeOfType<PermissionDecisionApproveOnce>();
        }
    }

    [Fact]
    public async Task DeniedNetworkOperation_CannotBeOverriddenByAutoApprove()
    {
        var agent = BuildAgent(ReadOnlyBinding(), autoApproveTools: true);
        using var governance = BuildGovernance();
        var errors = new List<string>();
        var handler = agent.BuildPermissionHandler(
            governance,
            RunId,
            _workspace,
            (_, _, _) => { },
            (_, error) => errors.Add(error),
            (_, _) => { },
            CancellationToken.None);

        var decision = await handler(new PermissionRequestUrl
        {
            ToolCallId = "web-fetch-denied",
            Url = "https://example.com",
            Intention = "read documentation",
        }, new PermissionInvocation());

        decision.Should().NotBeOfType<PermissionDecisionApproveOnce>();
        errors.Should().ContainSingle()
            .Which.Should().Contain("effective permission binding");
    }

    [Fact]
    public async Task UnknownOperation_FailsClosedWithBindingProvenance()
    {
        var agent = BuildAgent(ReadOnlyBinding(), autoApproveTools: false);
        using var governance = BuildGovernance();
        var errors = new List<string>();
        var handler = agent.BuildPermissionHandler(
            governance,
            RunId,
            _workspace,
            (_, _, _) => { },
            (_, error) => errors.Add(error),
            (_, _) => { },
            CancellationToken.None);

        var decision = await handler(
            CustomTool("future_unclassified_tool", new { }),
            new PermissionInvocation());

        decision.Should().NotBeOfType<PermissionDecisionApproveOnce>();
        errors.Should().ContainSingle()
            .Which.Should().Contain(ReadOnlyBinding().BindingId);
    }

    [Fact]
    public void ParentAndRefreshIntersections_CannotWidenEarlierRestrictions()
    {
        var broad = EffectivePermissionBinding.Create(
            RunId,
            1,
            "project-policy",
            "project:test",
            SandboxPolicy.Default(_workspace));
        var narrowPolicy = SandboxPolicy.Default(_workspace) with
        {
            AllowedOperations =
            [
                EffectivePermissionOperations.Observe,
                EffectivePermissionOperations.WorkspaceRead,
            ],
        };
        var parent = EffectivePermissionBinding.Create(
            "parent-run",
            1,
            "project-policy",
            "project:test",
            narrowPolicy);
        var child = EffectivePermissionBinding.Create(
            RunId,
            1,
            "project-policy",
            "project:test",
            SandboxPolicy.Default(_workspace),
            parent);

        child.Allows(EffectivePermissionOperations.WorkspaceWrite).Should().BeFalse(
            "a child binding must be intersected with its parent");

        var refreshed = EffectivePermissionBinding.Intersect(broad, child);
        refreshed.Allows(EffectivePermissionOperations.WorkspaceWrite).Should().BeFalse(
            "a later broad policy cannot restore authority removed by the launch ceiling");
    }

    [Fact]
    public void BindingSerialization_ContainsIdentityAndVersionButNoCredentialFields()
    {
        var json = JsonSerializer.Serialize(
            ReadOnlyBinding(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().Contain("\"bindingId\"");
        json.Should().Contain("\"version\"");
        using var document = JsonDocument.Parse(json);
        EnumeratePropertyNames(document.RootElement).Should().NotContain(name =>
            name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AgentweaverApiTools_AreAllClassified()
    {
        AgentweaverApiTools.ToolNames.Should().OnlyContain(tool =>
            EffectivePermissionClassifier.Classify(tool) == EffectivePermissionOperations.AgentweaverRead
            || EffectivePermissionClassifier.Classify(tool) == EffectivePermissionOperations.AgentweaverWrite);
    }

    [Fact]
    public void TamperedBinding_FailsValidation()
    {
        var binding = ReadOnlyBinding() with
        {
            AllowedOperations =
            [
                .. ReadOnlyBinding().AllowedOperations,
                EffectivePermissionOperations.WorkspaceWrite,
            ],
        };

        var act = () => binding.Validate(RunId, 1);

        act.Should().Throw<EffectivePermissionBindingException>()
            .WithMessage("*version does not match*");
    }

    private EffectivePermissionBinding ReadOnlyBinding() =>
        EffectivePermissionBinding.Create(
            RunId,
            1,
            "test-read-only-assignment",
            "project:test",
            SandboxPolicy.Default(_workspace) with
            {
                AllowedOperations =
                [
                    EffectivePermissionOperations.Observe,
                    EffectivePermissionOperations.WorkspaceRead,
                    EffectivePermissionOperations.WorkspaceSearch,
                    EffectivePermissionOperations.AgentweaverRead,
                    EffectivePermissionOperations.HumanInteraction,
                ],
            });

    private CopilotAIAgent BuildAgent(
        EffectivePermissionBinding? binding,
        bool autoApproveTools)
    {
        var options = new InMemoryRunOptionsStore();
        options.Set(RunId, new RunOptions(AutoApproveTools: autoApproveTools));
        var factory = new GitHubCopilotClientFactory(
            new ConfigurationBuilder().Build(),
            new FixedGitHubCopilotCapabilityCredentialProvider());
        var agent = new CopilotAIAgent(
            factory,
            SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance,
            runOptions: options);
        agent.EffectivePermissionBindingForTesting = binding;
        return agent;
    }

    private Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> BuildHandler(
        CopilotAIAgent agent,
        SandboxGovernance governance) =>
        agent.BuildPermissionHandler(
            governance,
            RunId,
            _workspace,
            (_, _, _) => { },
            (_, _) => { },
            (_, _) => { },
            CancellationToken.None);

    private SandboxGovernance BuildGovernance() =>
        SandboxGovernance.Create(
            _workspace,
            RunId,
            SandboxExecutorFactory.CreatePassthrough(),
            SandboxPolicy.Default(_workspace),
            NullLogger.Instance);

    private static PermissionRequestCustomTool CustomTool(string name, object args) => new()
    {
        ToolName = name,
        ToolCallId = $"call-{name}",
        ToolDescription = name,
        Args = JsonSerializer.SerializeToElement(args),
    };

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                    yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                    yield return nested;
            }
        }
    }
}
