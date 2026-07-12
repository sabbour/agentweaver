extern alias agenthost;

using System.Diagnostics;
using System.Text.Json;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using AgentHostConfigurationException = agenthost::Agentweaver.AgentHost.AgentHostConfigurationException;
using AgentHostOptions = agenthost::Agentweaver.AgentHost.AgentHostOptions;
using AgentHostRunConfiguration = agenthost::Agentweaver.AgentHost.AgentHostRunConfiguration;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using ConfigureRequest = agenthost::ConfigureRequest;
using PodLocalWorkspaceManager = agenthost::Agentweaver.AgentHost.PodLocalWorkspaceManager;
using PodLocalWorkspaceSpec = agenthost::Agentweaver.AgentHost.PodLocalWorkspaceSpec;

namespace Agentweaver.Tests.AgentHost;

public sealed class PodLocalWorkspaceManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        ".pod-local-workspace-tests",
        Guid.NewGuid().ToString("n"));

    public PodLocalWorkspaceManagerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ConfigureRequest_defaults_to_shared_existing_behavior_when_workspace_fields_are_omitted()
    {
        var request = JsonSerializer.Deserialize<ConfigureRequest>(
            """{"runId":"run-1","workingDirectory":"/workspace/run-1"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        request.Should().NotBeNull();
        var configuration = request!.ToRunConfiguration();
        configuration.Purpose.Should().Be(AgentHostPurpose.Default);
        configuration.WorkspaceMode.Should().Be(ExecutionWorkspaceMode.Shared);
        configuration.SharedWorkingDirectory.Should().Be("/workspace/run-1");
    }

    [Fact]
    public void ConfigureRequest_parses_generalized_local_workspace_contract()
    {
        var request = JsonSerializer.Deserialize<ConfigureRequest>(
            """
            {
              "runId": "run-1",
              "purpose": "AssemblyBuildTest",
              "sharedWorkingDirectory": "/workspace/reviewer",
              "sourceRepositoryPath": "/workspace/repo",
              "sourceRef": "agentweaver/integration/run-1",
              "baseCommitSha": "1111111111111111111111111111111111111111",
              "expectedTreeHash": "2222222222222222222222222222222222222222",
              "workspaceMode": "LocalReadOnly",
              "scratchRoot": "/local-workspace"
            }
            """,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        request.Should().NotBeNull();
        var configuration = request!.ToRunConfiguration();
        configuration.Purpose.Should().Be(AgentHostPurpose.AssemblyBuildTest);
        configuration.WorkspaceMode.Should().Be(ExecutionWorkspaceMode.LocalReadOnly);
        configuration.SourceRef.Should().Be("agentweaver/integration/run-1");
        configuration.BaseCommitSha.Should().Be("1111111111111111111111111111111111111111");
        configuration.ScratchRoot.Should().Be("/local-workspace");
    }

    [Fact]
    public void RuntimeState_exposes_effective_pod_local_workspace()
    {
        var state = new AgentHostRuntimeState();
        var configuration = Configuration(
            sharedWorkingDirectory: "/workspace/reviewer",
            workspaceMode: ExecutionWorkspaceMode.LocalReadOnly);

        state.TryConfigure(configuration).Should().BeTrue();
        state.SetEffectiveWorkingDirectory("/local-workspace/run/tree");

        state.SharedWorkingDirectory.Should().Be("/workspace/reviewer");
        state.EffectiveWorkingDirectory.Should().Be("/local-workspace/run/tree");
        state.WorkspaceMode.Should().Be(ExecutionWorkspaceMode.LocalReadOnly);
    }

    [Fact]
    public void ValidateConfiguration_leaves_implementation_turn_defined_but_unwired()
    {
        var configuration = Configuration(
            sharedWorkingDirectory: "/workspace/child",
            workspaceMode: ExecutionWorkspaceMode.LocalWritable) with
        {
            Purpose = AgentHostPurpose.ImplementationTurn,
        };

        var act = () => PodLocalWorkspaceManager.ValidateConfiguration(configuration);

        act.Should().Throw<AgentHostConfigurationException>()
            .Which.Reason.Should().Be("implementation_turn_not_enabled");
    }

    [Fact]
    public async Task PrepareAsync_rejects_base_commit_mismatch_with_typed_reason()
    {
        var repository = CreateRepository();
        var treeHash = Git(repository, "rev-parse", "integration^{tree}");

        var act = () => Manager().PrepareAsync(
            Spec(repository, new string('0', 40), treeHash),
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AgentHostConfigurationException>();
        exception.Which.Reason.Should().Be("workspace_base_commit_mismatch");
    }

    [Fact]
    public async Task PrepareAsync_checks_out_verified_commit_detached_on_execution_scratch()
    {
        var repository = CreateRepository();
        var commitSha = Git(repository, "rev-parse", "integration");
        var treeHash = Git(repository, "rev-parse", "integration^{tree}");
        var manager = Manager();
        var cacheVariables = new[]
        {
            "npm_config_cache",
            "YARN_CACHE_FOLDER",
            "PNPM_HOME",
            "PNPM_STORE_DIR",
            "npm_config_store_dir",
            "XDG_CACHE_HOME",
        };
        var originalValues = cacheVariables.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable);

        try
        {
            var prepared = await manager.PrepareAsync(
                Spec(repository, commitSha, treeHash),
                CancellationToken.None);

            Git(prepared.WorkspacePath, "rev-parse", "HEAD").Should().Be(commitSha);
            Git(prepared.WorkspacePath, "rev-parse", "HEAD^{tree}").Should().Be(treeHash);
            Git(prepared.WorkspacePath, "branch", "--show-current").Should().BeEmpty(
                "the local workspace must not use git worktree administration");
            File.Exists(Path.Combine(prepared.WorkspacePath, "package.json")).Should().BeTrue();
            prepared.CacheRoot.Should().Be(Path.Combine(prepared.WorkspacePath, ".agentweaver-cache"));
            foreach (var variable in cacheVariables)
            {
                var configuredPath = Environment.GetEnvironmentVariable(variable);
                configuredPath.Should().NotBeNullOrWhiteSpace();
                Path.GetFullPath(Path.Combine(prepared.WorkspacePath, configuredPath!))
                    .Should().StartWith(prepared.CacheRoot);
            }
        }
        finally
        {
            await manager.CleanupAsync();
            foreach (var (name, value) in originalValues)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task PrepareAsync_rejects_tree_mismatch_with_typed_reason()
    {
        var repository = CreateRepository();
        var commitSha = Git(repository, "rev-parse", "integration");

        var act = () => Manager().PrepareAsync(
            Spec(repository, commitSha, new string('f', 40)),
            CancellationToken.None);

        var exception = await act.Should().ThrowAsync<AgentHostConfigurationException>();
        exception.Which.Reason.Should().Be("workspace_tree_mismatch");
    }

    [Fact]
    public async Task PrepareWritebackAsync_rejects_read_only_workspace()
    {
        var repository = CreateRepository();
        var commitSha = Git(repository, "rev-parse", "integration");
        var treeHash = Git(repository, "rev-parse", "integration^{tree}");
        var manager = Manager();
        await manager.PrepareAsync(Spec(repository, commitSha, treeHash), CancellationToken.None);

        var act = () => manager.PrepareWritebackAsync();

        var exception = await act.Should().ThrowAsync<AgentHostConfigurationException>();
        exception.Which.Reason.Should().Be("workspace_writeback_read_only");
        await manager.CleanupAsync();
    }

    [Fact]
    public async Task PrepareWritebackAsync_exposes_writable_finalization_seam_without_writing_back()
    {
        var repository = CreateRepository();
        var commitSha = Git(repository, "rev-parse", "integration");
        var treeHash = Git(repository, "rev-parse", "integration^{tree}");
        var manager = Manager();
        await manager.PrepareAsync(
            Spec(repository, commitSha, treeHash, ExecutionWorkspaceMode.LocalWritable),
            CancellationToken.None);

        var writeback = await manager.PrepareWritebackAsync();

        writeback.SourceRef.Should().Be("integration");
        writeback.BaseCommitSha.Should().Be(commitSha);
        await manager.CleanupAsync();
    }

    private PodLocalWorkspaceManager Manager() =>
        new(
            Options.Create(new AgentHostOptions
            {
                ExecutionScratchRoot = Path.Combine(_root, "scratch"),
                ExecutionScratchMinimumFreeBytes = 0,
            }),
            NullLogger<PodLocalWorkspaceManager>.Instance);

    private PodLocalWorkspaceSpec Spec(
        string repository,
        string baseCommitSha,
        string expectedTreeHash,
        ExecutionWorkspaceMode mode = ExecutionWorkspaceMode.LocalReadOnly) =>
        new(
            "workspace-run",
            repository,
            "integration",
            baseCommitSha,
            expectedTreeHash,
            mode,
            Path.Combine(_root, "scratch"));

    private static AgentHostRunConfiguration Configuration(
        string? sharedWorkingDirectory,
        ExecutionWorkspaceMode workspaceMode) =>
        new(
            "workspace-run",
            UserId: "owner",
            TurnBearerToken: "token",
            KvUserSecretName: null,
            GitHubAccessToken: null,
            PreviewRunnerCredential: null,
            SharedWorkingDirectory: sharedWorkingDirectory,
            Purpose: AgentHostPurpose.AssemblyBuildTest,
            SourceRepositoryPath: "/workspace/repository",
            SourceRef: "integration",
            BaseCommitSha: new string('1', 40),
            ExpectedTreeHash: new string('2', 40),
            WorkspaceMode: workspaceMode,
            ScratchRoot: "/local-workspace");

    private string CreateRepository()
    {
        var repository = Path.Combine(_root, "source-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(repository);
        Git(repository, "init");
        Git(repository, "config", "user.email", "tests@example.invalid");
        Git(repository, "config", "user.name", "Agentweaver Tests");
        File.WriteAllText(Path.Combine(repository, "package.json"), """{"scripts":{"build":"echo ok"}}""");
        Git(repository, "add", "package.json");
        Git(repository, "commit", "-m", "fixture");
        Git(repository, "branch", "integration");
        return repository;
    }

    private static string Git(string workingDirectory, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
