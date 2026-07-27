using System.IO;
using FluentAssertions;
using Agentweaver.Api.Sandbox.Preview;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// Unit coverage for the LLM preview-command fallback plumbing (issue #541): the defensive JSON
/// parser (<see cref="CopilotPreviewCommandModel.ParseProposal"/>) and the token-bounded worktree
/// digest builder (<see cref="PreviewWorktreeDigest"/>). No model calls — pure functions only.
/// </summary>
public sealed class PreviewCommandModelTests : IDisposable
{
    private readonly string _dir;

    public PreviewCommandModelTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-cmdmodel-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ParseProposal_PreviewableCommand_IsParsed()
    {
        var result = CopilotPreviewCommandModel.ParseProposal(
            """{"previewable": true, "command": "npx --yes serve -l tcp://0.0.0.0:0 .", "cwd": "site"}""");

        result.Should().NotBeNull();
        result!.Previewable.Should().BeTrue();
        result.Command.Should().Be("npx --yes serve -l tcp://0.0.0.0:0 .");
        result.Cwd.Should().Be("site");
    }

    [Fact]
    public void ParseProposal_DefaultsCwdToDot_WhenMissing()
    {
        var result = CopilotPreviewCommandModel.ParseProposal(
            """{"previewable": true, "command": "python3 -m http.server --bind 0.0.0.0 0"}""");

        result.Should().NotBeNull();
        result!.Cwd.Should().Be(".");
    }

    [Fact]
    public void ParseProposal_NotPreviewable_ReturnsDecline()
    {
        var result = CopilotPreviewCommandModel.ParseProposal("""{"previewable": false}""");

        result.Should().NotBeNull();
        result!.Previewable.Should().BeFalse();
        result.Command.Should().BeNull();
    }

    [Fact]
    public void ParseProposal_PreviewableButEmptyCommand_TreatedAsDecline()
    {
        var result = CopilotPreviewCommandModel.ParseProposal("""{"previewable": true, "command": "  "}""");

        result.Should().NotBeNull();
        result!.Previewable.Should().BeFalse();
    }

    [Fact]
    public void ParseProposal_ToleratesSurroundingProse()
    {
        var result = CopilotPreviewCommandModel.ParseProposal(
            "Sure! Here you go:\n{\"previewable\": true, \"command\": \"go run .\", \"cwd\": \".\"}\nHope that helps.");

        result.Should().NotBeNull();
        result!.Command.Should().Be("go run .");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"other\": 1}")]
    public void ParseProposal_UnparseableOrMissing_ReturnsNull(string? response)
    {
        CopilotPreviewCommandModel.ParseProposal(response).Should().BeNull();
    }

    [Fact]
    public void Digest_IncludesFileListing_AndKeyFileContents()
    {
        File.WriteAllText(Path.Combine(_dir, "index.html"), "<html><body>hello</body></html>");
        File.WriteAllText(Path.Combine(_dir, "styles.css"), "body{color:red}");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "Run with any static server.");

        var digest = PreviewWorktreeDigest.Build(_dir);

        digest.Should().Contain("index.html");
        digest.Should().Contain("styles.css");
        // Key files are inlined verbatim.
        digest.Should().Contain("Run with any static server.");
        digest.Should().Contain("----- FILE: README.md -----");
        // Non-key files are listed but not inlined.
        digest.Should().NotContain("body{color:red}");
    }

    [Fact]
    public void Digest_ExcludesBuildAndDependencyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules", "left-pad"));
        File.WriteAllText(Path.Combine(_dir, "node_modules", "left-pad", "index.js"), "module.exports=1;");
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        File.WriteAllText(Path.Combine(_dir, ".git", "config"), "[core]");
        File.WriteAllText(Path.Combine(_dir, "app.html"), "<html></html>");

        var digest = PreviewWorktreeDigest.Build(_dir);

        digest.Should().Contain("app.html");
        digest.Should().NotContain("left-pad");
        digest.Should().NotContain("node_modules");
    }

    [Fact]
    public void Digest_MissingDirectory_ReturnsEmpty()
    {
        PreviewWorktreeDigest.Build(Path.Combine(_dir, "does-not-exist")).Should().BeEmpty();
    }
}
