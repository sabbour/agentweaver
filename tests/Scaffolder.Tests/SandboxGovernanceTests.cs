using FluentAssertions;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Sandbox;

/// <summary>
/// SC-011: Proves the sandbox escape is CLOSED at the authoritative enforcement
/// layer (SandboxGovernance.EvaluateToolCall). Both GitHub Copilot and Foundry
/// runners call this same gate — testing it directly with the exact tool-call
/// contexts they build reproduces the escape faithfully without needing a live LLM.
/// </summary>
public sealed class SandboxGovernanceTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly SandboxGovernance _governance;
    private readonly CapturingLogger _logger;
    private const string AgentId = "did:mesh:scaffolder:test:governance";

    public SandboxGovernanceTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"sandbox-gov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _logger = new CapturingLogger();
        _governance = SandboxGovernance.Create(_sandboxRoot, "test-run-001", _logger);
    }

    public void Dispose()
    {
        _governance.Dispose();
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch { /* best effort */ }
    }

    // ===================================================================
    // A. HEADLINE ESCAPE REPRODUCTION — "List contents of C:\"
    // This is the EXACT reproduction of the CONFIRMED escape where a
    // Copilot run in the artifact directory listed C:\.
    // ===================================================================

    [Fact]
    public void EscapeReproduction_ListDirectory_CRoot_IsDenied()
    {
        // REPRODUCTION: Agent requested list_directory on "C:\" — an out-of-sandbox
        // absolute path that was previously ALLOWED before the containment fix.
        var result = _governance.EvaluateToolCall(
            AgentId, "list_directory",
            new Dictionary<string, object> { ["path"] = @"C:\" },
            _logger);

        result.Allowed.Should().BeFalse("the confirmed C:\\ escape must be denied");
    }

    [Fact]
    public void EscapeReproduction_ReadFile_AbsoluteOutOfSandbox_IsDenied()
    {
        var result = _governance.EvaluateToolCall(
            AgentId, "read_file",
            new Dictionary<string, object> { ["path"] = @"C:\Windows\system32\config\SAM" },
            _logger);

        result.Allowed.Should().BeFalse();
    }

    // ===================================================================
    // B. SC-011 Matrix — ALLOW cases
    // ===================================================================

    [Theory]
    [InlineData("read_file", "file.txt")]
    [InlineData("list_directory", "subdir")]
    [InlineData("write_file", "output.cs")]
    [InlineData("edit_file", "Program.cs")]
    public void Allow_KnownTool_InSandboxRelativePath(string toolName, string relativePath)
    {
        var result = _governance.EvaluateToolCall(
            AgentId, toolName,
            new Dictionary<string, object> { ["path"] = relativePath },
            _logger);

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Allow_ListDirectory_SubdirectoryInSandbox()
    {
        // list_directory on a subdirectory within the sandbox
        var result = _governance.EvaluateToolCall(
            AgentId, "list_directory",
            new Dictionary<string, object> { ["path"] = "src" },
            _logger);

        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Allow_ReadFile_InSandboxAbsolutePath()
    {
        // An ABSOLUTE path that is within the sandbox boundary
        var inSandboxAbsolute = Path.Combine(_sandboxRoot, "nested", "file.cs");
        var result = _governance.EvaluateToolCall(
            AgentId, "read_file",
            new Dictionary<string, object> { ["path"] = inSandboxAbsolute },
            _logger);

        result.Allowed.Should().BeTrue();
    }

    // ===================================================================
    // B. SC-011 Matrix — DENY cases
    // ===================================================================

    [Theory]
    [InlineData("read_file", @"C:\Windows\system32\cmd.exe")]
    [InlineData("read_file", @"D:\evil\x.txt")]
    [InlineData("read_file", @"..\..\..\etc\passwd")]
    [InlineData("list_directory", @"C:\")]
    [InlineData("write_file", @"C:\Windows\temp\evil.txt")]
    [InlineData("read_file", @"\\?\C:\x")]
    [InlineData("read_file", @"\\server\share\x")]
    public void Deny_OutOfSandbox_VariousEscapePaths(string toolName, string path)
    {
        var result = _governance.EvaluateToolCall(
            AgentId, toolName,
            new Dictionary<string, object> { ["path"] = path },
            _logger);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Deny_SiblingPrefixAttack()
    {
        // Sandbox root is e.g. "...\work"; target is "...\work-evil\file.txt"
        // which shares a prefix but is NOT inside the sandbox.
        var siblingPath = _sandboxRoot + "-evil" + Path.DirectorySeparatorChar + "file.txt";
        var result = _governance.EvaluateToolCall(
            AgentId, "read_file",
            new Dictionary<string, object> { ["path"] = siblingPath },
            _logger);

        result.Allowed.Should().BeFalse("sibling-prefix paths must not be treated as contained");
    }

    [Fact]
    public void Deny_DriveRelativePath()
    {
        // "C:foo" is a drive-relative path — ambiguous and must be denied
        var result = _governance.EvaluateToolCall(
            AgentId, "read_file",
            new Dictionary<string, object> { ["path"] = "C:foo" },
            _logger);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Deny_EmptyPath()
    {
        var result = _governance.EvaluateToolCall(
            AgentId, "read_file",
            new Dictionary<string, object> { ["path"] = "" },
            _logger);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Deny_ShellTool()
    {
        var result = _governance.EvaluateToolCall(
            AgentId, "shell",
            new Dictionary<string, object> { ["command"] = "echo hi" },
            _logger);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Deny_McpTool()
    {
        var result = _governance.EvaluateToolCall(
            AgentId, "mcp",
            new Dictionary<string, object> { ["tool"] = "some_mcp_tool" },
            _logger);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Deny_UnknownGarbageToolName()
    {
        var result = _governance.EvaluateToolCall(
            AgentId, "hack_the_planet",
            new Dictionary<string, object> { ["path"] = "file.txt" },
            _logger);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Deny_UrlFetchTool()
    {
        // Network egress is not a sandboxed file tool — the URL fetch capability
        // must fall through to default-deny (spec §11 SC-011: "URL fetch -> Deny").
        var result = _governance.EvaluateToolCall(
            AgentId, "url",
            new Dictionary<string, object> { ["url"] = "http://evil.example.com" },
            _logger);

        result.Allowed.Should().BeFalse();
    }

    [Fact]
    public void Deny_SymlinkInsideSandbox_PointingOutside()
    {
        // A symlink created INSIDE the sandbox that targets a path OUTSIDE it must
        // be denied (spec §11 SC-011). Symlink creation on Windows needs Developer
        // Mode or elevation; skip gracefully when the privilege is unavailable.
        var outsideTarget = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        File.WriteAllText(outsideTarget, "secret");

        var linkPath = Path.Combine(_sandboxRoot, "link.txt");
        try
        {
            File.CreateSymbolicLink(linkPath, outsideTarget);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }
        finally
        {
            try { File.Delete(outsideTarget); } catch { /* best effort */ }
        }

        try
        {
            var result = _governance.EvaluateToolCall(
                AgentId, "read_file",
                new Dictionary<string, object> { ["path"] = "link.txt" },
                _logger);

            result.Allowed.Should().BeFalse("a symlink escaping the sandbox must be denied");
        }
        finally
        {
            try { File.Delete(linkPath); } catch { /* best effort */ }
        }
    }

    // ===================================================================
    // H. SC-012: Audit — ALLOW entry contains agentId and resolved path
    // ===================================================================

    [Fact]
    public void Audit_AllowEntry_ContainsAgentIdAndResolvedPath()
    {
        var auditLogger = new CapturingLogger();
        using var gov = SandboxGovernance.Create(_sandboxRoot, "audit-run", auditLogger);

        gov.EvaluateToolCall(AgentId, "read_file",
            new Dictionary<string, object> { ["path"] = "hello.txt" },
            auditLogger);

        auditLogger.HasEntryMatching(LogLevel.Information, "ALLOWED").Should().BeTrue();
        auditLogger.HasEntryContaining(AgentId).Should().BeTrue();
        auditLogger.HasEntryContaining("hello.txt").Should().BeTrue();
    }

    [Fact]
    public void Audit_DenyEntry_ContainsAgentIdAndReason()
    {
        var auditLogger = new CapturingLogger();
        using var gov = SandboxGovernance.Create(_sandboxRoot, "audit-run", auditLogger);

        gov.EvaluateToolCall(AgentId, "read_file",
            new Dictionary<string, object> { ["path"] = @"C:\evil.txt" },
            auditLogger);

        auditLogger.HasEntryMatching(LogLevel.Warning, "DENIED").Should().BeTrue();
        auditLogger.HasEntryContaining(AgentId).Should().BeTrue();
    }

    [Fact]
    public void Audit_GovernanceKernel_EmitsAuditEvent()
    {
        // Verify that the AGT kernel emits an audit event (wired via AuditEmitter.OnAll)
        var auditLogger = new CapturingLogger();
        using var gov = SandboxGovernance.Create(_sandboxRoot, "audit-kernel", auditLogger);

        gov.EvaluateToolCall(AgentId, "read_file",
            new Dictionary<string, object> { ["path"] = "test.txt" },
            auditLogger);

        auditLogger.HasEntryContaining("GovernanceAudit").Should().BeTrue(
            "the governance kernel should emit audit events via the AuditEmitter");
    }

    // ===================================================================
    // I. Issue 3 — "." and "./" acceptance for list_directory
    // ===================================================================

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    public void Allow_ListDirectory_DotPath(string dotPath)
    {
        var result = _governance.EvaluateToolCall(
            AgentId, "list_directory",
            new Dictionary<string, object> { ["path"] = dotPath },
            _logger);

        result.Allowed.Should().BeTrue($"list_directory with path \"{dotPath}\" should resolve to sandbox root and be allowed");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    public void Allow_ReadFile_DotPath_PassesGovernance(string dotPath)
    {
        // Governance should allow "." for read_file (the tool itself returns "is a directory")
        var result = _governance.EvaluateToolCall(
            AgentId, "read_file",
            new Dictionary<string, object> { ["path"] = dotPath },
            _logger);

        result.Allowed.Should().BeTrue("governance should accept '.' as a valid contained path");
    }
}
