extern alias agenthost;

using System.Diagnostics;
using System.Reflection;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using AgentHostOptions = agenthost::Agentweaver.AgentHost.AgentHostOptions;
using PodLocalWorkspaceManager = agenthost::Agentweaver.AgentHost.PodLocalWorkspaceManager;
using PodLocalWorkspaceSpec = agenthost::Agentweaver.AgentHost.PodLocalWorkspaceSpec;

namespace Agentweaver.Tests.AgentHost;

public sealed class ImplementationWritebackTests : IDisposable
{
    private readonly List<Fixture> _fixtures = [];
    private readonly string _root = Path.Combine(
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..")),
        ".test-wb",
        Guid.NewGuid().ToString("n")[..8]);

    public ImplementationWritebackTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PreparedWriteback_codec_round_trips_dedicated_a2a_data_part()
    {
        var expected = new PreparedWriteback(
            RunId: "run-1",
            SourceRef: "agentweaver/run-1",
            WritebackRef: "refs/agentweaver/writeback/run-1/nonce",
            BaseCommitSha: new string('1', 40),
            ResultCommitSha: new string('2', 40),
            ResultTreeSha: new string('3', 40),
            ChangedPathCount: 2);

        var decoded = PreparedWritebackDataPartCodec.TryDecode(
            PreparedWritebackDataPartCodec.Encode(expected));

        decoded.Should().Be(expected);
    }

    [Fact]
    public async Task Remote_proxy_marks_absent_required_implementation_envelope_as_missing()
    {
        await using var proxy = CreateRemoteProxy(requiresPreparedWriteback: true);
        await SetupProxyAsync(proxy);

        var envelope = proxy.TakePreparedWritebackEnvelope();

        envelope.Status.Should().Be(PreparedWritebackEnvelopeStatus.Missing);
        envelope.Writeback.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    public async Task Remote_proxy_marks_empty_or_malformed_writeback_descriptor_as_invalid(
        string payload)
    {
        await using var proxy = CreateRemoteProxy(requiresPreparedWriteback: true);
        await SetupProxyAsync(proxy);
        var content = new DataContent(
            System.Text.Encoding.UTF8.GetBytes(payload),
            PreparedWritebackDataPartCodec.MediaType);

        proxy.TryCapturePreparedWritebackEnvelope(content).Should().BeTrue();
        var envelope = proxy.TakePreparedWritebackEnvelope();

        envelope.Status.Should().Be(PreparedWritebackEnvelopeStatus.Invalid);
        envelope.Writeback.Should().BeNull();
    }

    [Fact]
    public async Task Impl_launch_context_uses_authoritative_agentweaver_child_branch_as_source_ref()
    {
        var fixture = CreateFixture();
        var run = new Run
        {
            Id = fixture.RunId,
            RepositoryPath = fixture.Repository,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Implement the subtask",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = fixture.Worktree.WorktreePath,
            WorktreeBranch = fixture.Worktree.BranchName,
            ParentRunId = RunId.New().ToString(),
            SubtaskId = "subtask-1",
        };
        var store = DispatchProxy.Create<IRunStore, SingleRunStoreProxy>();
        ((SingleRunStoreProxy)(object)store).Run = run;
        var resolver = new RunAgentHostContextResolver(
            store,
            fixture.WorktreeManager,
            implementationEnabled: true);

        var context = await resolver.ResolveAsync(fixture.RunId.ToString());

        context.SourceRef.Should().Be($"agentweaver/{fixture.RunId}");
        context.SourceRef.Should().Be(fixture.Worktree.BranchName);
        context.BaseCommitSha.Should().Be(fixture.BaseCommit);
        context.WorkspaceMode.Should().Be(ExecutionWorkspaceMode.LocalWritable);
        context.Purpose.Should().Be(AgentHostPurpose.ImplementationTurn);
    }

    [Fact]
    public async Task Impl_writeback_publishes_local_turn_commit_to_shared_child_branch()
    {
        var fixture = CreateFixture();
        var local = CreateLocalManager();
        var prepared = await local.PrepareAsync(
            fixture.LocalSpec,
            CancellationToken.None);
        File.WriteAllText(
            Path.Combine(prepared.WorkspacePath, "deliverable.txt"),
            "published from pod-local execution");

        var writeback = await local.PrepareWritebackAsync();

        writeback.HasChanges.Should().BeTrue();
        writeback.ChangedPathCount.Should().Be(1);
        writeback.SourceRef.Should().Be(fixture.Worktree.BranchName);
        writeback.WritebackRef.Should().StartWith(PodLocalExecutionWorkspace.WritebackRefPrefix);
        Git(fixture.Repository, "rev-parse", fixture.Worktree.BranchName)
            .Should().Be(fixture.BaseCommit, "the pod publishes only a temporary ref");

        fixture.WorktreeManager.ApplyPreparedWriteback(
            fixture.Repository,
            fixture.Worktree.WorktreePath,
            fixture.Worktree.BranchName,
            fixture.RunId,
            writeback);

        File.ReadAllText(Path.Combine(fixture.Worktree.WorktreePath, "deliverable.txt"))
            .Should().Be("published from pod-local execution");
        Git(fixture.Repository, "rev-parse", fixture.Worktree.BranchName)
            .Should().Be(writeback.ResultCommitSha);
        Git(fixture.Repository, "show", "-s", "--format=%s", writeback.ResultCommitSha)
            .Should().Be($"Agentweaver run {fixture.RunId}");
        Git(fixture.Repository, "show", "-s", "--format=%an <%ae>", writeback.ResultCommitSha)
            .Should().Be("Writeback Tests <writeback@example.invalid>");

        fixture.WorktreeManager.ApplyPreparedWriteback(
            fixture.Repository,
            fixture.Worktree.WorktreePath,
            fixture.Worktree.BranchName,
            fixture.RunId,
            writeback);

        var capturedTree = fixture.WorktreeManager.CommitChanges(
            fixture.Worktree.WorktreePath,
            fixture.RunId);
        capturedTree.Should().Be(writeback.ResultTreeSha);
        Git(fixture.Repository, "rev-parse", fixture.Worktree.BranchName)
            .Should().Be(writeback.ResultCommitSha, "existing bookkeeping must not duplicate the commit");

        await local.CleanupAsync();
    }

    [Fact]
    public async Task Impl_writeback_no_op_keeps_shared_child_branch_at_base()
    {
        var fixture = CreateFixture();
        var local = CreateLocalManager();
        await local.PrepareAsync(fixture.LocalSpec, CancellationToken.None);

        var writeback = await local.PrepareWritebackAsync();
        fixture.WorktreeManager.ApplyPreparedWriteback(
            fixture.Repository,
            fixture.Worktree.WorktreePath,
            fixture.Worktree.BranchName,
            fixture.RunId,
            writeback);

        writeback.HasChanges.Should().BeFalse();
        writeback.WritebackRef.Should().BeNull();
        writeback.ResultCommitSha.Should().Be(fixture.BaseCommit);
        Git(fixture.Repository, "rev-parse", fixture.Worktree.BranchName)
            .Should().Be(fixture.BaseCommit);

        await local.CleanupAsync();
    }

    [Fact]
    public async Task Impl_writeback_captures_nested_repository_contents_without_git_metadata()
    {
        var fixture = CreateFixture();
        var local = CreateLocalManager();
        var prepared = await local.PrepareAsync(
            fixture.LocalSpec,
            CancellationToken.None);
        var nested = Path.Combine(prepared.WorkspacePath, "nested-deliverable");
        CreateNestedRepository(nested, "nested.txt", "nested-only result");

        var writeback = await local.PrepareWritebackAsync();

        writeback.HasChanges.Should().BeTrue();
        fixture.WorktreeManager.ApplyPreparedWriteback(
            fixture.Repository,
            fixture.Worktree.WorktreePath,
            fixture.Worktree.BranchName,
            fixture.RunId,
            writeback);

        File.ReadAllText(Path.Combine(
                fixture.Worktree.WorktreePath,
                "nested-deliverable",
                "nested.txt"))
            .Should().Be("nested-only result");
        Directory.Exists(Path.Combine(
                fixture.Worktree.WorktreePath,
                "nested-deliverable",
                ".git"))
            .Should().BeFalse();
        File.Exists(Path.Combine(
                fixture.Worktree.WorktreePath,
                "nested-deliverable",
                ".git"))
            .Should().BeFalse();
        Git(
                fixture.Worktree.WorktreePath,
                "ls-files",
                "--stage",
                "nested-deliverable/nested.txt")
            .Should().StartWith("100644 ");

        await local.CleanupAsync();
    }

    [Fact]
    public async Task Impl_writeback_captures_top_level_and_nested_deliverables_together()
    {
        var fixture = CreateFixture();
        var local = CreateLocalManager();
        var prepared = await local.PrepareAsync(
            fixture.LocalSpec,
            CancellationToken.None);
        File.WriteAllText(
            Path.Combine(prepared.WorkspacePath, "top-level.txt"),
            "top-level result");
        var nested = Path.Combine(prepared.WorkspacePath, "nested-deliverable");
        CreateNestedRepository(nested, "nested.txt", "nested result");

        var writeback = await local.PrepareWritebackAsync();

        writeback.HasChanges.Should().BeTrue();
        writeback.ChangedPathCount.Should().Be(2);
        fixture.WorktreeManager.ApplyPreparedWriteback(
            fixture.Repository,
            fixture.Worktree.WorktreePath,
            fixture.Worktree.BranchName,
            fixture.RunId,
            writeback);

        File.ReadAllText(Path.Combine(fixture.Worktree.WorktreePath, "top-level.txt"))
            .Should().Be("top-level result");
        File.ReadAllText(Path.Combine(
                fixture.Worktree.WorktreePath,
                "nested-deliverable",
                "nested.txt"))
            .Should().Be("nested result");
        Directory.Exists(Path.Combine(
                fixture.Worktree.WorktreePath,
                "nested-deliverable",
                ".git"))
            .Should().BeFalse("nested repository metadata must never enter the parent commit");

        await local.CleanupAsync();
    }

    [Fact]
    public async Task Impl_writeback_captures_dirty_existing_submodule_with_unchanged_head()
    {
        string? nestedSource = null;
        var fixture = CreateFixture(repository =>
        {
            nestedSource = Path.Combine(repository, "existing-submodule");
            CreateNestedRepository(nestedSource, "nested.txt", "committed content");
        });
        var local = CreateLocalManager();
        var prepared = await local.PrepareAsync(
            fixture.LocalSpec,
            CancellationToken.None);
        var nested = Path.Combine(prepared.WorkspacePath, "existing-submodule");
        Git(prepared.WorkspacePath, "clone", nestedSource!, nested);
        File.WriteAllText(Path.Combine(nested, "nested.txt"), "dirty deliverable");

        var writeback = await local.PrepareWritebackAsync();

        writeback.HasChanges.Should().BeTrue();
        fixture.WorktreeManager.ApplyPreparedWriteback(
            fixture.Repository,
            fixture.Worktree.WorktreePath,
            fixture.Worktree.BranchName,
            fixture.RunId,
            writeback);

        File.ReadAllText(Path.Combine(
                fixture.Worktree.WorktreePath,
                "existing-submodule",
                "nested.txt"))
            .Should().Be("dirty deliverable");
        Git(
                fixture.Worktree.WorktreePath,
                "ls-files",
                "--stage",
                "existing-submodule/nested.txt")
            .Should().StartWith("100644 ");

        await local.CleanupAsync();
    }

    [Fact]
    public async Task Impl_writeback_flattens_recursively_nested_repositories_deepest_first()
    {
        var fixture = CreateFixture();
        var local = CreateLocalManager();
        var prepared = await local.PrepareAsync(
            fixture.LocalSpec,
            CancellationToken.None);
        var outer = Path.Combine(prepared.WorkspacePath, "outer");
        CreateNestedRepository(outer, "outer.txt", "outer deliverable");
        var inner = Path.Combine(outer, "inner");
        CreateNestedRepository(inner, "inner.txt", "inner deliverable");
        Git(outer, "add", "inner");
        Git(outer, "commit", "-m", "add inner repository");

        var writeback = await local.PrepareWritebackAsync();

        writeback.HasChanges.Should().BeTrue();
        fixture.WorktreeManager.ApplyPreparedWriteback(
            fixture.Repository,
            fixture.Worktree.WorktreePath,
            fixture.Worktree.BranchName,
            fixture.RunId,
            writeback);

        File.ReadAllText(Path.Combine(
                fixture.Worktree.WorktreePath,
                "outer",
                "outer.txt"))
            .Should().Be("outer deliverable");
        File.ReadAllText(Path.Combine(
                fixture.Worktree.WorktreePath,
                "outer",
                "inner",
                "inner.txt"))
            .Should().Be("inner deliverable");
        Git(fixture.Worktree.WorktreePath, "ls-files", "--stage")
            .Should().NotContain("160000 ");

        await local.CleanupAsync();
    }

    [Fact]
    public async Task Impl_writeback_conflict_fails_with_structured_base_mismatch()
    {
        var fixture = CreateFixture();
        var local = CreateLocalManager();
        var prepared = await local.PrepareAsync(
            fixture.LocalSpec,
            CancellationToken.None);
        File.WriteAllText(Path.Combine(prepared.WorkspacePath, "local.txt"), "local result");
        var writeback = await local.PrepareWritebackAsync();

        File.WriteAllText(
            Path.Combine(fixture.Worktree.WorktreePath, "concurrent.txt"),
            "concurrent shared update");
        fixture.WorktreeManager.CommitChanges(fixture.Worktree.WorktreePath, fixture.RunId);

        var act = () => fixture.WorktreeManager.ApplyPreparedWriteback(
            fixture.Repository,
            fixture.Worktree.WorktreePath,
            fixture.Worktree.BranchName,
            fixture.RunId,
            writeback);

        act.Should().Throw<WorktreeWritebackException>()
            .Which.Reason.Should().Be("writeback_base_mismatch");
        File.Exists(Path.Combine(fixture.Worktree.WorktreePath, "local.txt"))
            .Should().BeFalse("conflicting work must never be silently applied");

        await local.CleanupAsync();
    }

    private Fixture CreateFixture(Action<string>? configureRepository = null)
    {
        var repository = Path.Combine(_root, "r-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(repository);
        Git(repository, "init");
        Git(repository, "config", "user.name", "Fixture Author");
        Git(repository, "config", "user.email", "fixture@example.invalid");
        File.WriteAllText(Path.Combine(repository, ".gitignore"), "node_modules/\n.agentweaver-cache/\n");
        File.WriteAllText(Path.Combine(repository, "README.md"), "base");
        configureRepository?.Invoke(repository);
        Git(repository, "add", ".");
        Git(repository, "commit", "-m", "base");
        Git(repository, "branch", "-M", "main");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = Path.Combine(_root, "worktrees"),
                ["Git:Author:Name"] = "Writeback Tests",
                ["Git:Author:Email"] = "writeback@example.invalid",
            })
            .Build();
        var manager = new WorktreeManager(
            configuration,
            NullLogger<WorktreeManager>.Instance);
        var runId = RunId.New();
        var worktree = manager.AddWorktree(repository, "main", runId);
        var baseCommit = manager.GetBranchTipCommitSha(repository, worktree.BranchName)!;
        var baseTree = manager.GetBranchTipTreeSha(repository, worktree.BranchName)!;
        var spec = new PodLocalWorkspaceSpec(
            runId.ToString(),
            repository,
            worktree.BranchName,
            baseCommit,
            baseTree,
            ExecutionWorkspaceMode.LocalWritable,
            Path.Combine(_root, "scratch"),
            manager.CommitAuthorName,
            manager.CommitAuthorEmail);

        var fixture = new Fixture(repository, manager, runId, worktree, baseCommit, spec);
        _fixtures.Add(fixture);
        return fixture;
    }

    private PodLocalWorkspaceManager CreateLocalManager() =>
        new(
            Options.Create(new AgentHostOptions
            {
                ExecutionScratchRoot = Path.Combine(_root, "scratch"),
                ExecutionScratchMinimumFreeBytes = 0,
            }),
            NullLogger<PodLocalWorkspaceManager>.Instance);

    private static RemoteAgentProxy CreateRemoteProxy(bool requiresPreparedWriteback) =>
        new(
            new FixedEndpointResolver(requiresPreparedWriteback),
            new StubHttpClientFactory(),
            NullLoggerFactory.Instance);

    private static Task SetupProxyAsync(RemoteAgentProxy proxy) =>
        proxy.SetupAsync(
            workingDirectory: "/workspace",
            repositoryPath: "/workspace",
            runId: "implementation-run",
            modelId: null,
            systemPromptContext: null,
            streamWriter: null,
            projectId: null,
            agentName: null,
            apiBaseUrl: null,
            apiKey: null,
            ct: CancellationToken.None,
            userId: null);

    private static void CreateNestedRepository(
        string path,
        string fileName,
        string content)
    {
        Directory.CreateDirectory(path);
        Git(path, "init");
        Git(path, "config", "user.name", "Nested Fixture");
        Git(path, "config", "user.email", "nested@example.invalid");
        File.WriteAllText(Path.Combine(path, fileName), content);
        Git(path, "add", fileName);
        Git(path, "commit", "-m", "nested deliverable");
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
        foreach (var fixture in _fixtures)
        {
            try
            {
                fixture.WorktreeManager.RemoveWorktree(
                    fixture.Repository,
                    fixture.Worktree.WorktreePath,
                    fixture.Worktree.BranchName);
            }
            catch { }
        }
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(
                _root,
                "*",
                SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record Fixture(
        string Repository,
        WorktreeManager WorktreeManager,
        RunId RunId,
        WorktreeInfo Worktree,
        string BaseCommit,
        PodLocalWorkspaceSpec LocalSpec);

    public class SingleRunStoreProxy : DispatchProxy
    {
        public Run? Run { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IRunStore.GetAsync))
                return Task.FromResult(Run);

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private sealed class FixedEndpointResolver(bool requiresPreparedWriteback)
        : ISandboxAgentEndpointResolver
    {
        public Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct) =>
            Task.FromResult<Uri?>(new Uri("http://localhost:65534/a2a/agent"));

        public Task<bool> RequiresPreparedWritebackAsync(
            string runId,
            CancellationToken ct) =>
            Task.FromResult(requiresPreparedWriteback);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
