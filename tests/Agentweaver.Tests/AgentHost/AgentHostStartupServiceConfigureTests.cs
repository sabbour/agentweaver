extern alias agenthost;
using agenthost::Agentweaver.AgentHost;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using AgentHostOptions = agenthost::Agentweaver.AgentHost.AgentHostOptions;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using AgentHostStartupService = agenthost::Agentweaver.AgentHost.AgentHostStartupService;

namespace Agentweaver.Tests.AgentHost;

/// <summary>
/// Regression guard for bug #221: the per-run <c>AutoApproveTools</c> flag delivered by the API in
/// the warm-pool <c>POST /configure</c> body must seed the pod's <see cref="IRunOptionsStore"/> so
/// <c>CopilotAIAgent</c>'s HITL gate auto-approves <c>web_fetch</c> under autopilot. Before the fix
/// the pod booted a fresh store defaulting <c>AutoApproveTools=false</c>, so every request stalled
/// the 5-minute gate and auto-denied.
/// </summary>
public sealed class AgentHostStartupServiceConfigureTests : IDisposable
{
    private static readonly string[] RuntimeEnvironmentVariables =
    [
        "HOME",
        "XDG_CACHE_HOME",
        "XDG_DATA_HOME",
        "XDG_CONFIG_HOME",
        "AGENTWEAVER_SCRATCH",
        "AGENTWEAVER_SCRATCH_DIR",
    ];

    private readonly string _root = Path.Combine(
        Directory.GetCurrentDirectory(),
        ".agent-host-configure-tests",
        Guid.NewGuid().ToString("n")[..8]);

    public AgentHostStartupServiceConfigureTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ConfigureAsync_seeds_pod_run_options_with_autoApproveTools()
    {
        const string runId = "run-configure-221";
        var runOptions = new InMemoryRunOptionsStore();

        var service = new AgentHostStartupService(
            BuildAgent(runOptions),
            Options.Create(new AgentHostOptions()),
            new AgentHostRuntimeState(),
            runOptions,
            NullLogger<AgentHostStartupService>.Instance);

        // ConfigureAsync seeds the run-options store synchronously (before any await) and only THEN
        // begins SetupAsync — which needs a live Copilot client we don't have here. We therefore do
        // NOT await the returned task; we assert the synchronous store seeding and observe the task
        // so its eventual (expected) SetupAsync failure never surfaces as an unobserved exception.
        var task = service.ConfigureAsync(
            runId, userId: "sabbour", turnBearerToken: "tok",
            kvUserSecretName: null, gitHubAccessToken: null, workingDirectory: null,
            autoApproveTools: true, ct: new CancellationToken(canceled: true));
        _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

        runOptions.Get(runId).AutoApproveTools.Should().BeTrue(
            "the AutoApproveTools flag from /configure must seed the pod's IRunOptionsStore (bug #221)");
    }

    [Fact]
    public void ConfigureAsync_leaves_autoApproveTools_false_when_flag_off()
    {
        const string runId = "run-configure-221-off";
        var runOptions = new InMemoryRunOptionsStore();

        var service = new AgentHostStartupService(
            BuildAgent(runOptions),
            Options.Create(new AgentHostOptions()),
            new AgentHostRuntimeState(),
            runOptions,
            NullLogger<AgentHostStartupService>.Instance);

        var task = service.ConfigureAsync(
            runId, userId: "sabbour", turnBearerToken: "tok",
            kvUserSecretName: null, gitHubAccessToken: null, workingDirectory: null,
            autoApproveTools: false, ct: new CancellationToken(canceled: true));
        _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

        runOptions.Get(runId).AutoApproveTools.Should().BeFalse();
    }

    [Fact]
    public void ConfigureAsync_shared_mode_registers_runtime_home_for_shell()
    {
        const string runId = "run-shared-shell-home";
        var originalEnvironment = CaptureRuntimeEnvironment();
        try
        {
            var (executor, workspace, runtimeHome) = ConfigureSharedMode(runId);
            executor.RegisterTrustedWorkspace(workspace);
            var command = Command(workspace);

            var act = () => executor.BuildMountPlan(command);

            act.Should().NotThrow("Shared-mode startup must register runtime HOME before shell execution");
            act().Should().ContainSingle(mount =>
                mount.Source == runtimeHome && !mount.ReadOnly);
            executor.BuildChildEnvironment(command)["HOME"].Should().Be(runtimeHome);
        }
        finally
        {
            RestoreRuntimeEnvironment(originalEnvironment);
        }
    }

    [Fact]
    public void ConfigureAsync_shared_mode_registers_runtime_home_for_preview()
    {
        if (!OperatingSystem.IsLinux())
            return;

        const string runId = "run-shared-preview-home";
        var originalEnvironment = CaptureRuntimeEnvironment();
        try
        {
            var (executor, workspace, runtimeHome) = ConfigureSharedMode(runId);
            executor.RegisterTrustedWorkspace(workspace);

            using var process = executor.CreateProcess(
                "echo preview",
                workspace,
                new Dictionary<string, string>
                {
                    ["HOME"] = "/untrusted/home",
                    ["XDG_CACHE_HOME"] = "/untrusted/cache",
                },
                networkEnabled: true);
            var environment = ReadSetEnvironment(process.StartInfo.ArgumentList);

            environment["HOME"].Should().Be(runtimeHome);
            environment["XDG_CACHE_HOME"].Should().Be(Path.Combine(runtimeHome, ".cache"));
            environment["XDG_DATA_HOME"].Should().Be(Path.Combine(runtimeHome, ".local", "share"));
            environment["XDG_CONFIG_HOME"].Should().Be(Path.Combine(runtimeHome, ".config"));
            process.StartInfo.ArgumentList.Should().ContainInOrder(
                "--bind",
                runtimeHome,
                runtimeHome);
        }
        finally
        {
            RestoreRuntimeEnvironment(originalEnvironment);
        }
    }

    [Fact]
    public async Task ConfigureAsync_without_shared_workspace_uses_writable_pod_private_fallback()
    {
        const string runId = "run-null-shared-workspace";
        var originalEnvironment = CaptureRuntimeEnvironment();
        var protectedWorkspace = Path.Combine(_root, "protected-workspace");
        var scratch = Path.Combine(_root, "scratch-null-workspace");
        Directory.CreateDirectory(protectedWorkspace);
        Directory.CreateDirectory(scratch);
        var executor = new KataBwrapExecutor(protectedRoots: [protectedWorkspace]);
        var runOptions = new InMemoryRunOptionsStore();
        var options = Options.Create(new AgentHostOptions
        {
            WorkingDirectory = protectedWorkspace,
            RepositoryPath = protectedWorkspace,
            ExecutionScratchRoot = scratch,
            ExecutionScratchMinimumFreeBytes = 0,
        });
        var manager = new PodLocalWorkspaceManager(
            options,
            NullLogger<PodLocalWorkspaceManager>.Instance,
            executor);
        var runtimeState = new AgentHostRuntimeState();
        var service = new AgentHostStartupService(
            BuildAgent(runOptions, executor),
            options,
            runtimeState,
            runOptions,
            NullLogger<AgentHostStartupService>.Instance,
            manager);

        try
        {
            var task = service.ConfigureAsync(
                new AgentHostRunConfiguration(
                    runId,
                    UserId: "sabbour",
                    TurnBearerToken: "tok",
                    KvUserSecretName: null,
                    GitHubAccessToken: null,
                    PreviewRunnerCredential: null,
                    SharedWorkingDirectory: null),
                autoApproveTools: false,
                ct: new CancellationToken(canceled: true));
            _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

            var fallback = runtimeState.EffectiveWorkingDirectory;
            fallback.Should().NotBeNullOrWhiteSpace();
            fallback.Should().NotBe(protectedWorkspace);
            fallback.Should().StartWith(Path.Combine(scratch, "fallback-workspace"));
            File.WriteAllText(Path.Combine(fallback!, "writable.txt"), "ok");

            executor.RegisterTrustedWorkspace(fallback!);
            executor.BuildMountPlan(Command(fallback!))
                .Should().ContainSingle(mount => mount.Source == fallback && !mount.ReadOnly);
        }
        finally
        {
            await manager.CleanupAsync();
            RestoreRuntimeEnvironment(originalEnvironment);
        }
    }

    private (KataBwrapExecutor Executor, string Workspace, string RuntimeHome) ConfigureSharedMode(
        string runId)
    {
        var workspaceRoot = Path.Combine(_root, "workspace");
        var workspace = Path.Combine(workspaceRoot, runId);
        var scratch = Path.Combine(_root, "scratch");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(scratch);

        var executor = new KataBwrapExecutor(protectedRoots: [workspaceRoot]);
        var runOptions = new InMemoryRunOptionsStore();
        var options = Options.Create(new AgentHostOptions
        {
            WorkingDirectory = workspace,
            RepositoryPath = workspace,
            ExecutionScratchRoot = scratch,
            ExecutionScratchMinimumFreeBytes = 0,
        });
        var manager = new PodLocalWorkspaceManager(
            options,
            NullLogger<PodLocalWorkspaceManager>.Instance,
            executor);
        var service = new AgentHostStartupService(
            BuildAgent(runOptions, executor),
            options,
            new AgentHostRuntimeState(),
            runOptions,
            NullLogger<AgentHostStartupService>.Instance,
            manager);

        var task = service.ConfigureAsync(
            runId,
            userId: "sabbour",
            turnBearerToken: "tok",
            kvUserSecretName: null,
            gitHubAccessToken: null,
            workingDirectory: workspace,
            autoApproveTools: false,
            ct: new CancellationToken(canceled: true));
        _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

        var runtimeHome = Path.GetFullPath(Path.Combine(
            scratch,
            "runtime-home",
            PodLocalExecutionWorkspace.GetRunHash(runId)));
        return (executor, workspace, runtimeHome);
    }

    private static SandboxCommand Command(string workspace) =>
        new(
            "echo ok",
            workspace,
            null,
            new SandboxFsPolicy([workspace], [], []),
            TimeoutMs: 5000);

    private static Dictionary<string, string> ReadSetEnvironment(ICollection<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = arguments.ToArray();
        for (var index = 0; index + 2 < items.Length; index++)
        {
            if (items[index] != "--setenv")
                continue;
            values[items[index + 1]] = items[index + 2];
            index += 2;
        }
        return values;
    }

    private static Dictionary<string, string?> CaptureRuntimeEnvironment() =>
        RuntimeEnvironmentVariables.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable);

    private static void RestoreRuntimeEnvironment(
        IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var (name, value) in environment)
            Environment.SetEnvironmentVariable(name, value);
    }

    private static CopilotAIAgent BuildAgent(
        IRunOptionsStore runOptions,
        ISandboxExecutor? sandboxExecutor = null)
    {
        var config = new ConfigurationBuilder().Build();
        var factory = new GitHubCopilotClientFactory(
            config, new NullGitHubTokenStore(), new FixedInstallationScopeStub());
        return new CopilotAIAgent(
            factory,
            new FixedInstallationScopeStub(),
            sandboxExecutor ?? SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance,
            questionGate: null,
            runOptions: runOptions);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
