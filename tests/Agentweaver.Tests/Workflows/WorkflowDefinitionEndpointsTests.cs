using Agentweaver.Api.Workflows;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Tests.Helpers;

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

    [Fact]
    public async Task Put_InvalidStaticFanTopology_ReturnsUnprocessableEntity()
    {
        await using var factory = new ProjectsWebApplicationFactory();
        var client = factory.CreateAuthenticatedClient();
        var create = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Fan validation {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = factory.NewWorkingDirectory(),
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var projectId = (await create.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project_id").GetString();

        const string yaml = """
            id: invalid-fan
            name: Invalid fan
            start: entry
            nodes:
              - id: entry
                type: prompt
                prompt: begin
              - id: fan
                type: fan_out
              - id: branch-a
                type: prompt
                prompt: a
              - id: branch-b
                type: prompt
                prompt: b
              - id: join
                type: fan_in
                target: fan
              - id: done
                type: terminal
            edges:
              - from: entry
                to: fan
              - from: fan
                to: branch-a
                when: approved
              - from: fan
                to: branch-b
              - from: branch-a
                to: join
              - from: branch-b
                to: join
              - from: join
                to: done
            """;

        var response = await client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/invalid-fan",
            new { yaml });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("workflow_not_bindable");
        body.GetProperty("validation_errors")[0].GetString().Should().Contain("must all be unconditional");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }
}
