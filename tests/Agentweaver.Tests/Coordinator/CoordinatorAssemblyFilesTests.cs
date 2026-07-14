using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Pins issue #320: once a coordinator run completes, the originating branch may already include the
/// assembled integration commit(s), so recomputing /assembly/files as "integration branch vs current
/// origin tip" can collapse to [] even though the integration workspace is populated. The endpoint
/// must serve the persisted aggregate review artifact for completed/review-ready coordinator runs.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorAssemblyFilesTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly List<string> _tempRepoDirs = [];

    public CoordinatorAssemblyFilesTests()
    {
        _factory = new CoordinatorWebApplicationFactory();
        _owner = _factory.CreateOwnerClient();
    }

    public void Dispose()
    {
        _owner.Dispose();
        _factory.Dispose();
        foreach (var dir in _tempRepoDirs)
        {
            try { DeleteDirectory(dir); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AssemblyFiles_CompletedRun_UsesPersistedAggregateDiffAfterOriginAdvances()
    {
        var repoPath = CreateTempGitRepo();
        var runId = RunId.New();
        var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName(runId.ToString());

        CommitOnNewBranch(repoPath, "agentweaver/child-a", "feature.txt", "feature contents\n", "child a");
        var manager = _factory.Services.GetRequiredService<WorktreeManager>();
        var build = manager.BuildIntegrationBranch(repoPath, "main", integrationBranch, ["agentweaver/child-a"]);
        build.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        build.Diff.Should().Contain("feature.txt");

        FastForwardBranch(repoPath, "main", integrationBranch);

        await InsertCoordinatorRunAsync(
            runId,
            repoPath,
            "main",
            RunStatus.Completed,
            build.TreeHash!,
            build.Diff!,
            "assembly_complete");

        var workspace = await _owner.GetFromJsonAsync<JsonElement[]>($"/api/runs/{runId}/assembly/workspace");
        workspace.Should().NotBeNull();
        workspace!.Select(n => n.GetProperty("path").GetString()).Should().Contain("feature.txt");

        var files = await _owner.GetFromJsonAsync<JsonElement[]>($"/api/runs/{runId}/assembly/files");
        files.Should().NotBeNull();
        files!.Select(f => f.GetProperty("path").GetString()).Should().Contain("feature.txt",
            "completed coordinator runs must retain the actual assembled changed-file set even after main advances");

        var fileDiff = await _owner.GetAsync($"/api/runs/{runId}/assembly/files/feature.txt");
        fileDiff.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await fileDiff.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("path").GetString().Should().Be("feature.txt");
        body.GetProperty("status").GetString().Should().Be("added");
        body.GetProperty("diff").GetString().Should().Contain("feature contents");
    }

    private async Task InsertCoordinatorRunAsync(
        RunId runId,
        string repoPath,
        string originatingBranch,
        RunStatus status,
        string treeHash,
        string diff,
        string result)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var now = DateTimeOffset.UtcNow;
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = repoPath,
            OriginatingBranch = originatingBranch,
            ModelSource = ModelSource.GitHubCopilot,
            ModelId = "gpt-4o",
            Task = "assembly files test",
            SubmittingUser = CoordinatorWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = now.AddMinutes(-2),
            AgentName = "Coordinator",
            WorkflowRunId = null,
        });

        await runStore.UpdateReviewReadyAsync(runId, treeHash, diff, stepCount: 0);

        if (status != RunStatus.AwaitingReview)
            await runStore.UpdateResultAsync(runId, status, result, now);
    }

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"agentweaver-asmfiles-{Guid.NewGuid():N}");
        _tempRepoDirs.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var initial = repo.Commit("Initial commit", sig, sig);

        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        var workspace = repo.CreateBranch("_workspace", initial);
        Commands.Checkout(repo, workspace);

        return repoPath;
    }

    private static void CommitOnNewBranch(
        string repositoryPath, string branchName, string filePath, string fileContent, string commitMessage)
    {
        using var repo = new Repository(repositoryPath);
        var main = repo.Branches["main"] ?? throw new InvalidOperationException("main not found");
        var branch = repo.Branches[branchName] ?? repo.CreateBranch(branchName, main.Tip);

        var tmpBlobPath = Path.Combine(repositoryPath, ".git", $"tmp-blob-{Guid.NewGuid():N}");
        File.WriteAllText(tmpBlobPath, fileContent, Encoding.UTF8);
        try
        {
            var blob = repo.ObjectDatabase.CreateBlob(tmpBlobPath);
            var treeDef = TreeDefinition.From(branch.Tip.Tree);
            treeDef.Add(filePath, blob, Mode.NonExecutableFile);
            var newTree = repo.ObjectDatabase.CreateTree(treeDef);
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            var newCommit = repo.ObjectDatabase.CreateCommit(
                sig, sig, commitMessage, newTree, [branch.Tip], prettifyMessage: true);
            repo.Refs.UpdateTarget(repo.Refs[$"refs/heads/{branchName}"], newCommit.Id);
        }
        finally
        {
            if (File.Exists(tmpBlobPath)) File.Delete(tmpBlobPath);
        }
    }

    private static void FastForwardBranch(string repositoryPath, string targetBranch, string sourceBranch)
    {
        using var repo = new Repository(repositoryPath);
        var targetRef = repo.Refs[$"refs/heads/{targetBranch}"]
            ?? throw new InvalidOperationException($"{targetBranch} not found");
        var sourceTip = repo.Branches[sourceBranch]?.Tip
            ?? throw new InvalidOperationException($"{sourceBranch} not found");
        repo.Refs.UpdateTarget(targetRef, sourceTip.Id);
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
