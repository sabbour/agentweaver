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
/// Integration tests for the Feature 008/009 coordinator assembly per-file CONTENT endpoint
/// (GET /api/runs/{id}/assembly/content/{**path}). The coordinator owns NO worktree — its collective
/// output lives on the integration branch agentweaver/integration/{id} — so the review modal's
/// Preview/source tab cannot use the worktree-backed content endpoint (which 409s "Worktree not
/// available."). This endpoint reads the blob from the integration branch tip instead.
///
/// Drives a REAL temp git repository + a real integration branch built by the production
/// <see cref="WorktreeManager"/> (the same seam as IntegrationBranchBuilderTests), against the
/// hermetic <see cref="CoordinatorWebApplicationFactory"/> host (no mocks, Principle VII). Asserts:
/// content for changed and unchanged tracked paths, 404
/// (never 409) before the integration branch exists, and owner enforcement.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class CoordinatorAssemblyContentTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly HttpClient _other;
    private readonly List<string> _tempRepoDirs = [];

    public CoordinatorAssemblyContentTests()
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
        foreach (var dir in _tempRepoDirs)
        {
            try { DeleteDirectory(dir); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task AssemblyContent_ReturnsBlobFromIntegrationBranch_AndEnforcesWhitelistAndOwner()
    {
        var repoPath = CreateTempGitRepo();
        var runId = RunId.New();
        var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName(runId.ToString());

        // Build the integration branch with ONE changed file (feature.txt) off main — exactly as the
        // collective assembly does in production, via the real WorktreeManager.
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "feature.txt", "feature contents\n", "child a");
        var manager = _factory.Services.GetRequiredService<WorktreeManager>();
        var build = manager.BuildIntegrationBranch(repoPath, "main", integrationBranch, new[] { "agentweaver/child-a" });
        build.Outcome.Should().Be(IntegrationBranchOutcome.Built);

        await InsertCoordinatorRunAsync(runId, repoPath, "main");

        // (1) A path in the collective changed set returns 200 + the blob content from the branch tip
        //     (the bug was a 409 here because the coordinator owns no worktree).
        var ok = await _owner.GetAsync($"/api/runs/{runId}/assembly/content/feature.txt");
        ok.StatusCode.Should().Be(HttpStatusCode.OK,
            "the Preview tab must read coordinator content from the integration branch, not 409");
        var content = await ok.Content.ReadFromJsonAsync<JsonElement>();
        content.GetProperty("path").GetString().Should().Be("feature.txt");
        content.GetProperty("content").GetString().Should().Be("feature contents\n");
        content.GetProperty("is_binary").GetBoolean().Should().BeFalse();

        // (2) A path NOT in the collective changed set but present in the assembled branch returns
        //     content because the Workspace Files tab displays the whole integration tree.
        var notInSet = await _owner.GetAsync($"/api/runs/{runId}/assembly/content/readme.txt");
        notInSet.StatusCode.Should().Be(HttpStatusCode.OK,
            "clicking unchanged files in the full integration-branch Files tree must not 404");
        var unchanged = await notInSet.Content.ReadFromJsonAsync<JsonElement>();
        unchanged.GetProperty("content").GetString().Should().Be("initial content");

        // (3) Owner enforcement — a non-owner gets 404, mirroring the sibling assembly endpoints.
        var foreign = await _other.GetAsync($"/api/runs/{runId}/assembly/content/feature.txt");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound, "assembly content is owner-scoped");
    }

    [Fact]
    public async Task AssemblyContent_BeforeIntegrationBranchExists_Returns404_Never409()
    {
        var repoPath = CreateTempGitRepo();   // a real repo, but NO integration branch built yet
        var runId = RunId.New();
        await InsertCoordinatorRunAsync(runId, repoPath, "main");

        var resp = await _owner.GetAsync($"/api/runs/{runId}/assembly/content/feature.txt");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "before assembly builds the integration branch the content endpoint must 404, never 409");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task InsertCoordinatorRunAsync(RunId runId, string repoPath, string originatingBranch)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.InsertAsync(new Run
        {
            Id                = runId,
            RepositoryPath    = repoPath,
            OriginatingBranch = originatingBranch,
            ModelSource       = ModelSource.GitHubCopilot,
            ModelId           = "gpt-4o",
            Task              = "assembly content test",
            SubmittingUser    = CoordinatorWebApplicationFactory.OwnerUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
            AgentName         = "Coordinator",
            WorkflowRunId     = null,
        });
    }

    // ── git setup (mirrors IntegrationBranchBuilderTests) ─────────────────────────────────────

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"agentweaver-asmcontent-{Guid.NewGuid():N}");
        _tempRepoDirs.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var initial = repo.Commit("Initial commit", sig, sig);

        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        // Detach onto a workspace branch so 'main' is never the checked-out branch (mirrors prod).
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
                sig, sig, commitMessage, newTree, new[] { branch.Tip }, prettifyMessage: true);
            repo.Refs.UpdateTarget(repo.Refs[$"refs/heads/{branchName}"], newCommit.Id);
        }
        finally
        {
            if (File.Exists(tmpBlobPath)) File.Delete(tmpBlobPath);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
