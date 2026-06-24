using System.Text;
using System.Text.Json;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Live-SQLite + actual temp-git-repo tests for <see cref="ProjectWorkspaceService"/> (Principle VII,
/// no mocks). A project with a base branch plus one active run worktree is seeded; the tests assert the
/// ref listing, base-vs-worktree tree resolution, file content (text/binary), path containment, unknown
/// ref handling, and owner scoping. They lock the API-first JSON contract the web client binds to.
/// </summary>
public sealed class ProjectWorkspaceServiceTests : IAsyncDisposable
{
    private const string OwnerUser = "owner-user";

    private readonly List<string> _tempDirs = new();
    private TestSqliteDb _testDb = default!;

    private static CallerContext Owner => new() { User = OwnerUser };
    private static CallerContext Other => new() { User = "someone-else" };

    private async Task<(ProjectWorkspaceService Service, ProjectId ProjectId, string Branch)> SetupAsync()
    {
        _testDb = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(_testDb.Db);
        var runStore = new SqliteRunStore(_testDb.Db);

        var repoPath = CreateTempGitRepo();

        var worktreeBase = Path.Combine(Path.GetTempPath(), $"agentweaver-ws-base-{Guid.NewGuid():N}");
        _tempDirs.Add(worktreeBase);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worktrees:BasePath"] = worktreeBase })
            .Build();
        var worktreeManager = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);

        var runId = RunId.New();
        var worktree = worktreeManager.AddWorktree(repoPath, "main", runId);
        _tempDirs.Add(worktree.WorktreePath);

        // Uncommitted edits in the worktree that are absent from the committed base tree.
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "uncommitted.txt"), "fresh local work");
        File.WriteAllBytes(Path.Combine(worktree.WorktreePath, "logo.bin"), new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF });

        var project = new Project
        {
            Id               = ProjectId.New(),
            Name             = "Workspace Project",
            Origin           = ProjectOrigin.Blank(),
            WorkingDirectory = repoPath,
            DefaultBranch    = "main",
            Owner            = OwnerUser,
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State            = ProjectState.Active,
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow,
        };
        await projectStore.InsertAsync(project);

        var run = new Run
        {
            Id                = runId,
            RepositoryPath    = repoPath,
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "Implement the workspace browser",
            SubmittingUser    = OwnerUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
            ProjectId         = project.Id,
            WorktreePath      = worktree.WorktreePath,
            WorktreeBranch    = worktree.BranchName,
        };
        await runStore.InsertAsync(run);

        var service = new ProjectWorkspaceService(projectStore, runStore);
        return (service, project.Id, worktree.BranchName);
    }

    [Fact]
    public async Task ListRefs_ReturnsBaseAndWorktree()
    {
        var (service, projectId, branch) = await SetupAsync();

        var result = await service.ListRefsAsync(projectId, Owner, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.Ok);
        result.Value!.CurrentBranch.Should().Be("main");
        result.Value.Refs.Should().HaveCount(2);

        var baseRef = result.Value.Refs[0];
        baseRef.Kind.Should().Be("base");
        baseRef.Branch.Should().Be("main");
        baseRef.Label.Should().Be("main (base)");

        var worktreeRef = result.Value.Refs[1];
        worktreeRef.Kind.Should().Be("worktree");
        worktreeRef.Branch.Should().Be(branch);
        worktreeRef.RunStatus.Should().Be("in_progress");
        worktreeRef.OriginatingBranch.Should().Be("main");
        worktreeRef.RunId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Workspace_AllowsCoordinatorAssemblyBranchFromRunContext()
    {
        var (service, projectId, _) = await SetupAsync();
        var projectStore = new SqliteProjectStore(_testDb.Db);
        var project = await projectStore.GetAsync(projectId);
        project.Should().NotBeNull();

        var runStore = new SqliteRunStore(_testDb.Db);
        var runId = RunId.New();
        var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName(runId.ToString());
        CommitOnNewBranch(project!.WorkingDirectory, integrationBranch, "assembled.txt", "assembled content");
        await runStore.InsertAsync(new Run
        {
            Id                = runId,
            RepositoryPath    = project.WorkingDirectory,
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "Assemble output",
            SubmittingUser    = OwnerUser,
            Status            = RunStatus.Merged,
            StartedAt         = DateTimeOffset.UtcNow,
            EndedAt           = DateTimeOffset.UtcNow,
            ProjectId         = project.Id,
            AgentName         = "Coordinator",
        });

        var refs = await service.ListRefsAsync(projectId, Owner, CancellationToken.None);
        refs.Value!.Refs.Should().Contain(r =>
            r.Kind == "assembly" &&
            r.Branch == integrationBranch &&
            r.RunId == runId.ToString());

        var tree = await service.ListWorkspaceAsync(projectId, Owner, integrationBranch, CancellationToken.None);
        tree.Outcome.Should().Be(WorkspaceOutcome.Ok);
        tree.Nodes!.Select(n => n.Path).Should().Contain("assembled.txt");

        var content = await service.GetFileContentAsync(projectId, Owner, "assembled.txt", integrationBranch, CancellationToken.None);
        content.Outcome.Should().Be(WorkspaceOutcome.Ok);
        content.Value!.Content.Should().Be("assembled content");
    }

    [Fact]
    public async Task ListRefs_SerializesSnakeCaseContract()
    {
        var (service, projectId, _) = await SetupAsync();

        var result = await service.ListRefsAsync(projectId, Owner, CancellationToken.None);
        var json = JsonSerializer.Serialize(result.Value);

        json.Should().Contain("\"current_branch\"");
        json.Should().Contain("\"refs\"");
        json.Should().Contain("\"run_id\"");
        json.Should().Contain("\"run_status\"");
        json.Should().Contain("\"originating_branch\"");
    }

    [Fact]
    public async Task ListRefs_NonOwner_ReturnsNotFound()
    {
        var (service, projectId, _) = await SetupAsync();

        var result = await service.ListRefsAsync(projectId, Other, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.NotFound);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task ListWorkspace_BaseRef_ReturnsCommittedTreeWithoutUncommitted()
    {
        var (service, projectId, _) = await SetupAsync();

        var result = await service.ListWorkspaceAsync(projectId, Owner, @ref: null, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.Ok);
        var paths = result.Nodes!.Select(n => n.Path).ToList();
        paths.Should().Contain("readme.md");
        paths.Should().Contain("src/app.cs");
        paths.Should().NotContain("uncommitted.txt");
    }

    [Fact]
    public async Task ListWorkspace_WorktreeRef_ReflectsUncommittedFile()
    {
        var (service, projectId, branch) = await SetupAsync();

        var result = await service.ListWorkspaceAsync(projectId, Owner, branch, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.Ok);
        var paths = result.Nodes!.Select(n => n.Path).ToList();
        paths.Should().Contain("uncommitted.txt");
        paths.Should().Contain("readme.md");
        paths.Should().NotContain(p => p.StartsWith(".git", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListWorkspace_FoldersSortBeforeFiles()
    {
        var (service, projectId, _) = await SetupAsync();

        var result = await service.ListWorkspaceAsync(projectId, Owner, @ref: "main", CancellationToken.None);

        var nodes = result.Nodes!;
        var firstFileIndex = nodes.ToList().FindIndex(n => !n.IsFolder);
        var lastFolderIndex = nodes.ToList().FindLastIndex(n => n.IsFolder);
        if (firstFileIndex >= 0 && lastFolderIndex >= 0)
            lastFolderIndex.Should().BeLessThan(firstFileIndex, "folders sort before files");
    }

    [Fact]
    public async Task ListWorkspace_UnknownRef_ReturnsNotFound()
    {
        var (service, projectId, _) = await SetupAsync();

        var result = await service.ListWorkspaceAsync(projectId, Owner, "no-such-branch", CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.NotFound);
    }

    [Fact]
    public async Task GetFileContent_BaseRef_ReturnsLanguageAndContent()
    {
        var (service, projectId, _) = await SetupAsync();

        var result = await service.GetFileContentAsync(projectId, Owner, "readme.md", @ref: null, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.Ok);
        result.Value!.IsBinary.Should().BeFalse();
        result.Value.Language.Should().Be("markdown");
        result.Value.Content.Should().Contain("Workspace");
    }

    [Fact]
    public async Task GetFileContent_WorktreeRef_DetectsBinary()
    {
        var (service, projectId, branch) = await SetupAsync();

        var result = await service.GetFileContentAsync(projectId, Owner, "logo.bin", branch, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.Ok);
        result.Value!.IsBinary.Should().BeTrue();
        result.Value.Content.Should().BeNull();
    }

    [Fact]
    public async Task GetFileContent_WorktreeRef_ReadsUncommittedText()
    {
        var (service, projectId, branch) = await SetupAsync();

        var result = await service.GetFileContentAsync(projectId, Owner, "uncommitted.txt", branch, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.Ok);
        result.Value!.Content.Should().Be("fresh local work");
    }

    [Fact]
    public async Task GetFileContent_MissingPath_ReturnsNotFound()
    {
        var (service, projectId, _) = await SetupAsync();

        var result = await service.GetFileContentAsync(projectId, Owner, "does/not/exist.txt", @ref: null, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.NotFound);
    }

    [Fact]
    public async Task GetFileContent_UnknownRef_ReturnsNotFound()
    {
        var (service, projectId, _) = await SetupAsync();

        var result = await service.GetFileContentAsync(projectId, Owner, "readme.md", "no-such-branch", CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.NotFound);
    }

    [Fact]
    public async Task GetFileContent_PathTraversal_IsRejected()
    {
        var (service, projectId, branch) = await SetupAsync();

        var result = await service.GetFileContentAsync(projectId, Owner, "../escape.txt", branch, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.InvalidPath);
    }

    [Fact]
    public async Task GetFileContent_NonOwner_ReturnsNotFound()
    {
        var (service, projectId, _) = await SetupAsync();

        var result = await service.GetFileContentAsync(projectId, Other, "readme.md", @ref: null, CancellationToken.None);

        result.Outcome.Should().Be(WorkspaceOutcome.NotFound);
    }

    // ── git setup (mirrors CoordinatorAssemblyContentTests) ──────────────────────────────────────

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"agentweaver-ws-{Guid.NewGuid():N}");
        _tempDirs.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        Directory.CreateDirectory(Path.Combine(repoPath, "src"));
        File.WriteAllText(Path.Combine(repoPath, "readme.md"), "# Workspace\nbase content");
        File.WriteAllText(Path.Combine(repoPath, "src", "app.cs"), "class App {}");
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

    private static void CommitOnNewBranch(string repositoryPath, string branchName, string filePath, string fileContent)
    {
        using var repo = new Repository(repositoryPath);
        var main = repo.Branches["main"] ?? throw new InvalidOperationException("main not found");
        var branch = repo.Branches[branchName] ?? repo.CreateBranch(branchName, main.Tip);
        var tmpBlobPath = Path.Combine(repositoryPath, ".git", $"tmp-blob-{Guid.NewGuid():N}");
        File.WriteAllText(tmpBlobPath, fileContent);
        try
        {
            var blob = repo.ObjectDatabase.CreateBlob(tmpBlobPath);
            var treeDef = TreeDefinition.From(branch.Tip.Tree);
            treeDef.Add(filePath, blob, Mode.NonExecutableFile);
            var newTree = repo.ObjectDatabase.CreateTree(treeDef);
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            var newCommit = repo.ObjectDatabase.CreateCommit(sig, sig, "assembly", newTree, new[] { branch.Tip }, prettifyMessage: true);
            repo.Refs.UpdateTarget(repo.Refs[$"refs/heads/{branchName}"], newCommit.Id);
        }
        finally
        {
            if (File.Exists(tmpBlobPath)) File.Delete(tmpBlobPath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_testDb is not null)
            await _testDb.DisposeAsync();

        foreach (var dir in _tempDirs)
            DeleteDirectory(dir);
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch { /* best effort */ }
    }
}
