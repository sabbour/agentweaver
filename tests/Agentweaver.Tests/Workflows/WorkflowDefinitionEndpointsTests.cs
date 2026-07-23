using Agentweaver.Api.Workflows;
using FluentAssertions;

namespace Agentweaver.Tests.Workflows;

public sealed class WorkflowDefinitionEndpointsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aw-workflow-read-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "aw-workflow-read-outside-" + Guid.NewGuid().ToString("N"));

    public WorkflowDefinitionEndpointsTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    [Fact]
    public async Task TryReadWorkflowYamlAsync_RejectsSymlinkedWorkflowFile()
    {
        var workflows = Path.Combine(_root, ".agentweaver", "workflows");
        Directory.CreateDirectory(workflows);
        var secret = Path.Combine(_outside, "secret.yaml");
        File.WriteAllText(secret, "host secret");

        try
        {
            File.CreateSymbolicLink(Path.Combine(workflows, "evil.yaml"), secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Symlink creation requires Developer Mode or elevated privileges on some Windows hosts.
        }

        var yaml = await WorkflowDefinitionEndpoints.TryReadWorkflowYamlAsync(workflows, "evil", CancellationToken.None);

        yaml.Should().BeNull();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }
}
