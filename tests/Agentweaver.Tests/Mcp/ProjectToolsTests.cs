using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using ModelContextProtocol.Server;

namespace Agentweaver.Tests.Mcp;

public sealed class ProjectToolsTests
{
    [Fact]
    public void ProjectCreate_InputSchema_ExposesOptionalRepositorySelectionCode()
    {
        var schema = BuildTool().ProtocolTool.InputSchema;

        schema.TryGetProperty("properties", out var properties).Should().BeTrue();
        properties.TryGetProperty("repository_selection_code", out _).Should().BeTrue();
        properties.TryGetProperty("source_repository", out _).Should().BeFalse();

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            required.EnumerateArray().Select(e => e.GetString()!).Should().NotContain("repository_selection_code");
        }
    }

    [Fact]
    public void GitHubRepositorySelectionTools_ExposeTheBrowseAndIssueFlow()
    {
        var list = BuildTool(nameof(ProjectTools.GitHubRepositorySelectionsListAsync)).ProtocolTool;
        var issue = BuildTool(nameof(ProjectTools.GitHubRepositorySelectionIssueAsync)).ProtocolTool;

        list.Name.Should().Be("github_repository_selections_list");
        issue.Name.Should().Be("github_repository_selection_issue");
        issue.InputSchema.GetProperty("properties").TryGetProperty("full_name", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GitHubRepositorySelectionTools_ForwardOnlyTheSelectedFullName()
    {
        var calls = new List<(HttpMethod Method, string Path, JsonElement? Body)>();
        var tools = new ProjectTools(CreateApiClient((request, _) =>
        {
            JsonElement? body = request.Content is null
                ? null
                : request.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
            calls.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { selection_code = "opaque-code" })
            });
        }));

        await tools.GitHubRepositorySelectionsListAsync(CancellationToken.None);
        await tools.GitHubRepositorySelectionIssueAsync("octo/secure-repo", CancellationToken.None);

        calls.Should().HaveCount(2);
        calls[0].Method.Should().Be(HttpMethod.Get);
        calls[0].Path.Should().Be("/api/github/repository-selections");
        calls[0].Body.Should().BeNull();
        calls[1].Method.Should().Be(HttpMethod.Post);
        calls[1].Path.Should().Be("/api/github/repository-selections");
        calls[1].Body!.Value.GetProperty("full_name").GetString().Should().Be("octo/secure-repo");
        calls[1].Body!.Value.TryGetProperty("repository_id", out _).Should().BeFalse();
        calls[1].Body!.Value.TryGetProperty("source_repository", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ProjectCreate_ForwardsOnlyTheOpaqueRepositorySelectionCode()
    {
        HttpRequestMessage? capturedRequest = null;
        JsonElement capturedBody = default;
        var tools = new ProjectTools(CreateApiClient((request, _) =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { id = "proj-1" })
            });
        }));

        await tools.ProjectCreateAsync(
            "sabbour.me-blog",
            @"C:\workspace\sabbour.me-blog",
            origin: "github",
            repository_selection_code: "opaque-selection-code");

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Method.Should().Be(HttpMethod.Post);
        capturedRequest.RequestUri!.AbsolutePath.Should().Be("/api/projects");

        capturedBody.GetProperty("name").GetString().Should().Be("sabbour.me-blog");
        capturedBody.GetProperty("working_directory").GetString().Should().Be(@"C:\workspace\sabbour.me-blog");
        capturedBody.GetProperty("origin").GetString().Should().Be("github");
        capturedBody.GetProperty("repository_selection_code").GetString().Should().Be("opaque-selection-code");
        capturedBody.TryGetProperty("source_repository", out _).Should().BeFalse();
    }

    private static McpServerTool BuildTool(string methodName = nameof(ProjectTools.ProjectCreateAsync))
    {
        var method = typeof(ProjectTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method {methodName} not found.");
        return McpServerTool.Create(method, new ProjectTools(CreateApiClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))), options: null);
    }

    private static AgentweaverApiClient CreateApiClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        var httpClient = new HttpClient(new DelegatingHandlerStub(handler))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        return new AgentweaverApiClient(httpClient, new McpConfig("http://localhost", "test-api-key"));
    }

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
