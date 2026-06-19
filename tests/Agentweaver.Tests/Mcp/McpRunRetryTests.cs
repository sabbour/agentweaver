using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Mcp;

/// <summary>
/// Integration tests for the MCP RunTools.run_retry tool.
/// Uses the same in-process API factory seam as sibling HTTP tests (no mocks — Principle VII).
/// Seeding pattern mirrors RunRetryTests (Tank): InsertAsync directly into SqliteRunStore.
/// The success test provides a real git repo so WorktreeManager can create a worktree
/// (same approach as Tank's RegularProjectFailedRun_Retry_CreatesNewRun_CopyingInputs).
/// </summary>
public sealed class McpRunRetryTests : IClassFixture<ProjectsWebApplicationFactory>, IDisposable
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly List<string> _tempRepos = new();

    public McpRunRetryTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public void Dispose()
    {
        foreach (var dir in _tempRepos)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best effort */ }
                Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }
    }

    private RunTools CreateTools()
    {
        var httpClient = _factory.CreateClient();
        var config = new McpConfig("http://localhost", ProjectsWebApplicationFactory.TestApiKey);
        var apiClient = new AgentweaverApiClient(httpClient, config);
        return new RunTools(apiClient);
    }

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"agentweaver-mcp-retry-{Guid.NewGuid():N}");
        _tempRepos.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var initial = repo.Commit("Initial commit", sig, sig);

        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        var workspace = repo.CreateBranch("_workspace", initial);
        Commands.Checkout(repo, workspace);

        return repoPath;
    }

    // =========================================================================
    // Unknown run id -> 404
    // =========================================================================
    [Fact]
    public async Task RunRetry_UnknownRunId_ThrowsMcpApiException404()
    {
        var tools = CreateTools();
        var act = () => tools.RunRetryAsync(Guid.NewGuid().ToString("N"), CancellationToken.None);

        await act.Should().ThrowAsync<McpApiException>()
            .Where(ex => ex.StatusCode == 404);
    }

    // =========================================================================
    // Failed run -> success summary: "Retried run {id} -> new run {newId}."
    // =========================================================================
    [Fact]
    public async Task RunRetry_FailedRun_ReturnsSummaryString()
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var repo = CreateTempGitRepo();
        var runId = RunId.New();

        await runStore.InsertAsync(new Run
        {
            Id                = runId,
            RepositoryPath    = repo,
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "retry test task",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.Failed,
            StartedAt         = DateTimeOffset.UtcNow,
            EndedAt           = DateTimeOffset.UtcNow,
        });

        var tools = CreateTools();
        var result = await tools.RunRetryAsync(runId.ToString(), CancellationToken.None);

        result.Should().StartWith($"Retried run {runId} -> new run ");
        result.Should().EndWith(".");
    }

    // =========================================================================
    // Non-retryable run (InProgress) -> 409 run_not_retryable.
    // The endpoint returns 409 before attempting StartRunAsync so no git repo needed.
    // =========================================================================
    [Fact]
    public async Task RunRetry_NonRetryableRun_ThrowsMcpApiException409()
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runId = RunId.New();

        await runStore.InsertAsync(new Run
        {
            Id                = runId,
            RepositoryPath    = Path.Combine(Path.GetTempPath(), "agentweaver-mcp-retry-norepo"),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "non-retryable test task",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
        });

        var tools = CreateTools();
        var act = () => tools.RunRetryAsync(runId.ToString(), CancellationToken.None);

        await act.Should().ThrowAsync<McpApiException>()
            .Where(ex => ex.StatusCode == 409);
    }
}

