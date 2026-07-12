using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Git;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Verifies that <see cref="ProjectGitInitializer.InitBlank"/> seeds a baseline .gitignore and
/// commits it as part of the initial commit, so greenfield projects don't capture dependency/build
/// junk once WorktreeManager staging became scope-independent (issue #222). Uses a real temp git
/// repo — no mocks (Constitution Principle VII).
/// </summary>
public sealed class ProjectGitInitializerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    [Fact]
    public void InitBlank_SeedsAndCommitsBaselineGitignore()
    {
        var repoPath = NewTempDir();
        var init = new ProjectGitInitializer(NullLogger<ProjectGitInitializer>.Instance);

        var branch = init.InitBlank(repoPath, "main");

        branch.Should().Be("main");
        File.Exists(Path.Combine(repoPath, ".gitignore")).Should().BeTrue(
            "InitBlank must write a baseline .gitignore for blank projects");

        using var repo = new Repository(repoPath);
        var tip = repo.Branches["main"]!.Tip;
        tip.Tree[".gitignore"].Should().NotBeNull(
            "the baseline .gitignore must be committed in the initial commit");

        var blob = (Blob)tip.Tree[".gitignore"].Target;
        var content = blob.GetContentText();
        content.Should().Contain("node_modules/");
        content.Should().Contain("__pycache__/");
        content.Should().Contain("bin/");
        content.Should().Contain("obj/");
        content.Should().Contain(".env");
    }

    [Fact]
    public void InitBlank_DoesNotClobberExistingGitignore()
    {
        var repoPath = NewTempDir();
        var existing = "# custom\ncustom-artifact/\n";
        File.WriteAllText(Path.Combine(repoPath, ".gitignore"), existing);

        var init = new ProjectGitInitializer(NullLogger<ProjectGitInitializer>.Instance);
        init.InitBlank(repoPath, "main");

        File.ReadAllText(Path.Combine(repoPath, ".gitignore")).Should().Be(existing,
            "an existing .gitignore must never be overwritten");
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aw-gitinit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort — git locks may linger on Windows */ }
        }
    }
}
