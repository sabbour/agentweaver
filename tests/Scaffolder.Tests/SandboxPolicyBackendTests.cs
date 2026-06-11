using FluentAssertions;
using Scaffolder.SandboxFs;

namespace Scaffolder.Tests.Sandbox;

/// <summary>
/// SC-011 addendum #7 / SC-012 / FR-033: Proves the SandboxPolicyBackend
/// (Layer B — direct containment) denies out-of-sandbox paths unconditionally,
/// regardless of any Layer-A AGT rule allowing the tool name.
/// </summary>
public sealed class SandboxPolicyBackendTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly SandboxPolicyBackend _backend;

    public SandboxPolicyBackendTests()
    {
        _sandboxRoot = Path.Combine(Path.GetTempPath(), $"sandbox-backend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _backend = new SandboxPolicyBackend(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); }
        catch { /* best effort */ }
    }

    // ===================================================================
    // C. Authoritative-containment HARD GATE (SC-011 addendum #7)
    // Layer B denies regardless of Layer-A allow. This proves that even if
    // the AGT rule says "allow read_file", the backend still denies an
    // out-of-sandbox absolute path.
    // ===================================================================

    [Theory]
    [InlineData(@"C:\Windows\system32\cmd.exe")]
    [InlineData(@"C:\")]
    [InlineData(@"D:\evil\payload.txt")]
    [InlineData(@"\\server\share\x.txt")]
    [InlineData(@"\\?\C:\x")]
    public void HardGate_AllowedToolName_OutOfSandboxAbsolutePath_Denied(string escapePath)
    {
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "read_file",   // A tool that Layer-A would allow
            ["path"] = escapePath,
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeFalse(
            "Layer B must deny out-of-sandbox paths regardless of Layer-A allowing the tool name");
    }

    // ===================================================================
    // D. Unit tests
    // ===================================================================

    [Fact]
    public void UnknownTool_Denied()
    {
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "hack_the_planet",
            ["path"] = "file.txt",
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("Unrecognized");
    }

    [Fact]
    public void NullToolName_Denied()
    {
        var context = new Dictionary<string, object>
        {
            ["path"] = "file.txt",
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public void KnownTool_NoPathKey_Denied()
    {
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "read_file",
            // no path, file_path, or directory key
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("No path argument");
    }

    [Fact]
    public void KnownTool_EmptyPath_Denied()
    {
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "write_file",
            ["path"] = "",
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("write_file")]
    [InlineData("create_file")]
    [InlineData("str_replace_editor")]
    public void AllowCase_PopulatesResolvedPath(string toolName)
    {
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = toolName,
            ["path"] = "subdir/file.cs",
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeTrue();
        decision.Metadata.Should().ContainKey("resolved_path");
        var resolvedPath = decision.Metadata!["resolved_path"] as string;
        resolvedPath.Should().NotBeNullOrEmpty();
        resolvedPath.Should().StartWith(_sandboxRoot);
    }

    [Fact]
    public void AllowCase_AbsolutePathInsideSandbox_Allowed()
    {
        var insidePath = Path.Combine(_sandboxRoot, "deep", "file.txt");
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "read_file",
            ["path"] = insidePath,
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeTrue();
        (decision.Metadata!["resolved_path"] as string).Should().Be(insidePath);
    }

    [Fact]
    public void TraversalAttack_Denied()
    {
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "read_file",
            ["path"] = @"..\..\..\..\Windows\system32\cmd.exe",
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public void SiblingPrefixAttack_Denied()
    {
        var siblingPath = _sandboxRoot + "-evil" + Path.DirectorySeparatorChar + "file.txt";
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "read_file",
            ["path"] = siblingPath,
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public void FailClosed_InternalError_ReturnsDenied()
    {
        // Trigger internal error by passing a path with null bytes (causes Path.GetFullPath to throw)
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "read_file",
            ["path"] = "file\0evil.txt",
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeFalse("backend must fail-closed on internal errors");
    }

    // ===================================================================
    // F. Issue 3 — "." path acceptance
    // ===================================================================

    [Theory]
    [InlineData("write_file", ".")]
    [InlineData("write_file", "./")]
    [InlineData("read_file", ".")]
    public void DotPath_KnownTool_Allowed(string toolName, string dotPath)
    {
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = toolName,
            ["path"] = dotPath,
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeTrue(
            $"\"{dotPath}\" for {toolName} resolves to sandbox root and must be allowed");
        decision.Metadata.Should().ContainKey("resolved_path");
    }

    // ===================================================================
    // E. JsonElement coercion (Foundry runner regression)
    // The Foundry runner forwards model tool arguments as System.Text.Json
    // JsonElement values, not System.String. The backend must coerce these to
    // their underlying string so containment is evaluated — before the fix the
    // path key was silently ignored and every Foundry call was denied
    // "No path argument", which ALSO broke legitimate in-sandbox reads.
    // ===================================================================

    [Fact]
    public void JsonElementPath_InsideSandbox_Allowed()
    {
        var insidePath = Path.Combine(_sandboxRoot, "deep", "file.txt");
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "read_file",
            ["path"] = JsonStringElement(insidePath),
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeTrue(
            "a JsonElement string path inside the sandbox must be coerced and allowed");
        (decision.Metadata!["resolved_path"] as string).Should().Be(insidePath);
    }

    [Theory]
    [InlineData(@"C:\Windows\system32\cmd.exe")]
    [InlineData(@"C:\")]
    [InlineData(@"..\..\..\Windows\system32\cmd.exe")]
    public void JsonElementPath_OutsideSandbox_Denied(string escapePath)
    {
        var context = new Dictionary<string, object>
        {
            ["tool_name"] = "read_file",
            ["path"] = JsonStringElement(escapePath),
        };

        var decision = _backend.Evaluate(context);

        decision.Allowed.Should().BeFalse(
            "a JsonElement string path outside the sandbox must be coerced and denied by containment");
        decision.Reason.Should().NotContain("No path argument",
            "the path must reach containment, not be dropped as missing");
    }

    private static System.Text.Json.JsonElement JsonStringElement(string value)
        => System.Text.Json.JsonSerializer.SerializeToElement(value);
}
