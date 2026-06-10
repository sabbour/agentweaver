using FluentAssertions;
using Scaffolder.SandboxFs;

namespace Scaffolder.Tests.Sandbox;

/// <summary>
/// T051 — Unit tests for SandboxedFileTools.ApplyPatchAsync.
/// Covers the two-phase validation (all paths validated before any write),
/// Add / Delete / Update / Move-to patch grammar, and escape-path rejection.
/// </summary>
public sealed class ApplyPatchTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly SandboxedFileTools _tools;

    public ApplyPatchTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"apply-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _tools = new SandboxedFileTools(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch { /* best effort */ }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private string SandboxPath(string relative) =>
        Path.Combine(_sandboxRoot, relative);

    private void WriteFile(string relative, string content) =>
        File.WriteAllText(SandboxPath(relative), content);

    private string ReadFile(string relative) =>
        File.ReadAllText(SandboxPath(relative));

    // ---------------------------------------------------------------
    // Add File
    // ---------------------------------------------------------------

    [Fact]
    public async Task AddFile_CreatesNewFile()
    {
        const string patch = """
            *** Begin Patch
            *** Add File: added.txt
            +hello
            +world
            *** End Patch
            """;

        var result = await _tools.ApplyPatchAsync(patch);

        result.Success.Should().BeTrue(result.Reason);
        File.Exists(SandboxPath("added.txt")).Should().BeTrue("Add File must create the file");
        ReadFile("added.txt").Should().Be("hello\nworld");
    }

    // ---------------------------------------------------------------
    // Delete File
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteFile_RemovesExistingFile()
    {
        WriteFile("todelete.txt", "goodbye");

        const string patch = """
            *** Begin Patch
            *** Delete File: todelete.txt
            *** End Patch
            """;

        var result = await _tools.ApplyPatchAsync(patch);

        result.Success.Should().BeTrue(result.Reason);
        File.Exists(SandboxPath("todelete.txt")).Should().BeFalse("Delete File must remove the file");
    }

    // ---------------------------------------------------------------
    // Update File
    // ---------------------------------------------------------------

    [Fact]
    public async Task UpdateFile_AppliesHunkCorrectly()
    {
        WriteFile("update.cs", "line A\nline B\nline C");

        const string patch = """
            *** Begin Patch
            *** Update File: update.cs
            @@
             line A
            -line B
            +line B modified
             line C
            *** End Patch
            """;

        var result = await _tools.ApplyPatchAsync(patch);

        result.Success.Should().BeTrue(result.Reason);
        ReadFile("update.cs").Should().Be("line A\nline B modified\nline C");
    }

    // ---------------------------------------------------------------
    // Move to — escape rejection (entire patch rejected, zero mutation)
    // ---------------------------------------------------------------

    [Fact]
    public async Task MoveTo_EscapingPath_RejectsEntirePatch_ZeroMutation()
    {
        WriteFile("source.cs", "hello\nworld");

        // "../outside.cs" contains a ".." segment — must be rejected in Phase 1.
        const string patch = """
            *** Begin Patch
            *** Update File: source.cs
            @@
             hello
            -world
            +world updated
            *** Move to: ../outside.cs
            *** End Patch
            """;

        var result = await _tools.ApplyPatchAsync(patch);

        result.Success.Should().BeFalse("traversal in Move-to path must reject the entire patch");
        result.Hunks.Should().BeEmpty("Phase 1 rejection produces no hunk results");
        // Source file must be unchanged
        ReadFile("source.cs").Should().Be("hello\nworld");
        File.Exists(Path.Combine(Path.GetDirectoryName(_sandboxRoot)!, "outside.cs"))
            .Should().BeFalse("the escaping target must never be created");
    }

    [Fact]
    public async Task MoveTo_AbsoluteEscape_RejectsEntirePatch_ZeroMutation()
    {
        WriteFile("source2.cs", "original content");

        // Absolute path is rejected by ValidateAndResolve (absolute paths are not permitted).
        var escapePath = OperatingSystem.IsWindows()
            ? @"C:\Windows\evil.cs"
            : "/etc/passwd";

        var patch = $"""
            *** Begin Patch
            *** Update File: source2.cs
            @@
             original content
            -original content
            +modified
            *** Move to: {escapePath}
            *** End Patch
            """;

        var result = await _tools.ApplyPatchAsync(patch);

        result.Success.Should().BeFalse("an absolute Move-to escape path must reject the entire patch");
        result.Hunks.Should().BeEmpty();
        ReadFile("source2.cs").Should().Be("original content");
    }

    // ---------------------------------------------------------------
    // Move to — valid rename within sandbox
    // ---------------------------------------------------------------

    [Fact]
    public async Task MoveTo_ValidRename_WithinSandbox_Succeeds()
    {
        WriteFile("orig.cs", "hello\nworld");

        const string patch = """
            *** Begin Patch
            *** Update File: orig.cs
            @@
             hello
            -world
            +world renamed
            *** Move to: renamed.cs
            *** End Patch
            """;

        var result = await _tools.ApplyPatchAsync(patch);

        result.Success.Should().BeTrue(result.Reason);
        File.Exists(SandboxPath("orig.cs")).Should().BeFalse("original file must be deleted after rename");
        File.Exists(SandboxPath("renamed.cs")).Should().BeTrue("renamed file must exist");
        ReadFile("renamed.cs").Should().Be("hello\nworld renamed");
    }

    // ---------------------------------------------------------------
    // Mixed patch — one valid hunk + one escaping Move-to → entire rejected
    // ---------------------------------------------------------------

    [Fact]
    public async Task MixedPatch_OneValidHunk_OneEscapingMoveTo_EntirePatchRejected()
    {
        WriteFile("existing.cs", "alpha\nbeta");

        // Patch has two hunks:
        //   1. Add File: valid_new.txt   (would succeed)
        //   2. Update File: existing.cs  with Move to: ../escape.cs   (must fail Phase 1)
        // Phase 1 should catch the escape and return without applying hunk 1.
        const string patch = """
            *** Begin Patch
            *** Add File: valid_new.txt
            +new content
            *** Update File: existing.cs
            @@
             alpha
            -beta
            +beta updated
            *** Move to: ../escape.cs
            *** End Patch
            """;

        var result = await _tools.ApplyPatchAsync(patch);

        result.Success.Should().BeFalse("the entire patch must be rejected when any path is invalid");
        result.Hunks.Should().BeEmpty();

        // Neither file must be mutated.
        File.Exists(SandboxPath("valid_new.txt"))
            .Should().BeFalse("valid_new.txt must not be created when the patch is rejected");
        ReadFile("existing.cs").Should().Be("alpha\nbeta",
            "existing.cs must be unchanged when the patch is rejected");
    }

    // ---------------------------------------------------------------
    // Missing End Patch marker
    // ---------------------------------------------------------------

    [Fact]
    public async Task MissingEndPatch_RejectsWithError()
    {
        const string patch = """
            *** Begin Patch
            *** Add File: never.txt
            +content
            """;

        var result = await _tools.ApplyPatchAsync(patch);

        result.Success.Should().BeFalse("a patch without '*** End Patch' must be rejected");
        result.Reason.Should().Contain("End Patch");
        result.Hunks.Should().BeEmpty();
    }
}
