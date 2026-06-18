using FluentAssertions;
using Agentweaver.SandboxFs;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// T052 — Unit tests for SandboxedSearchTools (grep + file search).
/// All operations are constrained to an isolated temp sandbox root.
/// </summary>
public sealed class SandboxedSearchToolsTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly SandboxedSearchTools _search;

    public SandboxedSearchToolsTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"search-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _search = new SandboxedSearchTools(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch { /* best effort */ }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private void WriteFile(string relative, string content)
    {
        var full = Path.Combine(_sandboxRoot, relative);
        var dir = Path.GetDirectoryName(full);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }

    // ---------------------------------------------------------------
    // GrepSearch — literal match
    // ---------------------------------------------------------------

    [Fact]
    public async Task GrepSearch_LiteralMatch_ReturnsCorrectLines()
    {
        WriteFile("alpha.txt", "foo bar\nhello world\nfoo baz");

        var matches = await _search.GrepSearchAsync(
            "foo", isRegex: false, includePattern: null,
            maxResults: 100, caseSensitive: true, ct: default);

        matches.Should().HaveCount(2);
        matches.Should().AllSatisfy(m => m.LineContent.Should().Contain("foo"));
        matches.Should().Contain(m => m.LineNumber == 1);
        matches.Should().Contain(m => m.LineNumber == 3);
    }

    // ---------------------------------------------------------------
    // GrepSearch — regex match
    // ---------------------------------------------------------------

    [Fact]
    public async Task GrepSearch_RegexMatch_ReturnsCorrectLines()
    {
        WriteFile("regex.txt", "abc123\nxyz\nabc456");

        var matches = await _search.GrepSearchAsync(
            @"abc\d+", isRegex: true, includePattern: null,
            maxResults: 100, caseSensitive: true, ct: default);

        matches.Should().HaveCount(2);
        matches.Should().AllSatisfy(m => m.LineContent.Should().MatchRegex(@"abc\d+"));
    }

    // ---------------------------------------------------------------
    // GrepSearch — no match
    // ---------------------------------------------------------------

    [Fact]
    public async Task GrepSearch_NoMatch_ReturnsEmpty()
    {
        WriteFile("nomatch.txt", "hello world");

        var matches = await _search.GrepSearchAsync(
            "zzznomatch", isRegex: false, includePattern: null,
            maxResults: 100, caseSensitive: true, ct: default);

        matches.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    // GrepSearch — excluded directories (.git is never searched)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GrepSearch_ExcludedDirectory_SkipsGitDir()
    {
        // Place the matching content ONLY inside .git
        WriteFile(Path.Combine(".git", "COMMIT_EDITMSG"), "secret_marker_xyz");

        var matches = await _search.GrepSearchAsync(
            "secret_marker_xyz", isRegex: false, includePattern: null,
            maxResults: 100, caseSensitive: true, ct: default);

        matches.Should().BeEmpty(
            ".git is an excluded directory and must never be searched");
    }

    // ---------------------------------------------------------------
    // GrepSearch — maxResults cap
    // ---------------------------------------------------------------

    [Fact]
    public async Task GrepSearch_MaxResults_CapsOutput()
    {
        // Write a file with 20 matching lines
        var lines = Enumerable.Range(1, 20).Select(i => $"match_{i}");
        WriteFile("many.txt", string.Join("\n", lines));

        var matches = await _search.GrepSearchAsync(
            "match_", isRegex: false, includePattern: null,
            maxResults: 5, caseSensitive: true, ct: default);

        matches.Should().HaveCount(5, "GrepSearch must respect the maxResults cap");
    }

    // ---------------------------------------------------------------
    // FileSearch — glob pattern
    // ---------------------------------------------------------------

    [Fact]
    public void FileSearch_GlobPattern_ReturnsMatchingFiles()
    {
        WriteFile("a.cs", "");
        WriteFile("b.cs", "");
        WriteFile("c.txt", "");

        var results = _search.FileSearch("*.cs", maxResults: 100, ct: default);

        results.Should().HaveCount(2, "only .cs files should match the glob *.cs");
        results.Should().AllSatisfy(r => r.Should().EndWith(".cs"));
    }

    // ---------------------------------------------------------------
    // FileSearch — traversal pattern rejected / returns empty
    // ---------------------------------------------------------------

    [Fact]
    public void FileSearch_TraversalPattern_RejectsOrReturnsEmpty()
    {
        WriteFile("legit.cs", "");

        // "../" traversal patterns must be rejected; result must be empty.
        var results = _search.FileSearch("../outside/*", maxResults: 100, ct: default);

        results.Should().BeEmpty(
            "a glob pattern containing '..' must be rejected and return no results");
    }

    // ---------------------------------------------------------------
    // FileSearch — no match
    // ---------------------------------------------------------------

    [Fact]
    public void FileSearch_NoMatch_ReturnsEmpty()
    {
        WriteFile("real.cs", "");

        var results = _search.FileSearch("*.never_exists_xyz", maxResults: 100, ct: default);

        results.Should().BeEmpty("a glob that matches no files must return an empty list");
    }

    // ---------------------------------------------------------------
    // Symlink root — throws at construction
    // ---------------------------------------------------------------

    [Fact]
    public void SymlinkRoot_ThrowsAtConstruction()
    {
        var realTarget = Path.Combine(Path.GetTempPath(), $"real-st-{Guid.NewGuid():N}");
        var symlinkPath = Path.Combine(Path.GetTempPath(), $"link-st-{Guid.NewGuid():N}");
        Directory.CreateDirectory(realTarget);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(symlinkPath, realTarget);
            }
            catch (UnauthorizedAccessException)
            {
                return; // Requires elevation on this OS — skip gracefully.
            }
            catch (IOException)
            {
                return;
            }

            var act = () => new SandboxedSearchTools(symlinkPath);
            act.Should().Throw<SandboxViolationException>(
                "constructing SandboxedSearchTools with a symlink sandbox root must throw");
        }
        finally
        {
            try { Directory.Delete(realTarget, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(symlinkPath); } catch { /* best effort */ }
        }
    }
}
