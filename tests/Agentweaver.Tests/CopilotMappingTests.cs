using FluentAssertions;
using GitHub.Copilot;
using Agentweaver.AgentRuntime;

namespace Agentweaver.Tests.Sandbox;

/// <summary>
/// SC-011 addendum #6: Tests the Copilot SDK permission request mapping
/// (MapToToolCall / MapReadRequest) that disambiguates read vs list_directory.
/// This is the BLOCKING design fix — without correct mapping, directory-listing
/// requests would bypass the sandbox by using the wrong tool name.
/// </summary>
public sealed class CopilotMappingTests : IDisposable
{
    private readonly string _tempDir;

    public CopilotMappingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mapping-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private static PermissionRequestRead MakeReadRequest(string path) =>
        new() { Path = path, Intention = "test" };

    private static PermissionRequestWrite MakeWriteRequest(string fileName) =>
        new() { FileName = fileName, Intention = "test", Diff = "", CanOfferSessionApproval = false };

    private static PermissionRequestShell MakeShellRequest(string command) =>
        new()
        {
            FullCommandText = command,
            Intention = "test",
            Commands = [],
            HasWriteFileRedirection = false,
            PossiblePaths = [],
            PossibleUrls = [],
            CanOfferSessionApproval = false,
        };

    // ===================================================================
    // E. MapReadRequest — directory heuristic
    // ===================================================================

    [Fact]
    public void MapReadRequest_TrailingSeparator_MapsToListDirectory()
    {
        var request = MakeReadRequest(@"src\models\");
        var (toolName, args) = GitHubCopilotAgentRunner.MapReadRequest(request);

        toolName.Should().Be("list_directory");
        args["path"].Should().Be(@"src\models\");
    }

    [Fact]
    public void MapReadRequest_ExistingDirectory_MapsToListDirectory()
    {
        var request = MakeReadRequest(_tempDir);
        var (toolName, _) = GitHubCopilotAgentRunner.MapReadRequest(request);

        toolName.Should().Be("list_directory");
    }

    [Fact]
    public void MapReadRequest_FilePath_MapsToReadFile()
    {
        var request = MakeReadRequest("src/models/User.cs");
        var (toolName, args) = GitHubCopilotAgentRunner.MapReadRequest(request);

        toolName.Should().Be("read_file");
        args["path"].Should().Be("src/models/User.cs");
    }

    [Fact]
    public void MapReadRequest_AltDirSeparator_MapsToListDirectory()
    {
        var request = MakeReadRequest("src/models/");
        var (toolName, _) = GitHubCopilotAgentRunner.MapReadRequest(request);

        toolName.Should().Be("list_directory");
    }

    // ===================================================================
    // E. MapToToolCall — write and shell
    // ===================================================================

    [Fact]
    public void MapToToolCall_WriteRequest_MapsToWriteFile()
    {
        var request = MakeWriteRequest("output.cs");
        var (toolName, args) = GitHubCopilotAgentRunner.MapToToolCall(request);

        toolName.Should().Be("write_file");
        args["path"].Should().Be("output.cs");
    }

    [Fact]
    public void MapToToolCall_ShellRequest_MapsToRunCommand()
    {
        var request = MakeShellRequest("dotnet build");
        var (toolName, args) = GitHubCopilotAgentRunner.MapToToolCall(request);

        // Shell requests now map to 'run_command' so the allow-shell-sandboxed
        // YAML rule fires instead of the explicit deny-native-shell rule.
        toolName.Should().Be("run_command");
        args["command"].Should().Be("dotnet build");
    }
}
