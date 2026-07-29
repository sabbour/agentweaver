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
    public void ProjectCreate_InputSchema_ExposesOptionalSourceRepository()
    {
        var schema = BuildTool().ProtocolTool.InputSchema;

        schema.TryGetProperty("properties", out var properties).Should().BeTrue();
        properties.TryGetProperty("source_repository", out _).Should().BeTrue();

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            required.EnumerateArray().Select(e => e.GetString()!).Should().NotContain("source_repository");
        }
    }

    [Theory]
    [InlineData("sabbour/sabbour.github.io", "https://github.com/sabbour/sabbour.github.io")]
    [InlineData("https://github.com/sabbour/sabbour.github.io", "https://github.com/sabbour/sabbour.github.io")]
    [InlineData("sabbour/sabbour.github.io.git", "https://github.com/sabbour/sabbour.github.io.git")]
    public async Task ProjectCreate_NormalizesSourceRepository_ToFullGitHubHttpsUrl(
        string sourceRepository,
        string expectedSourceRepository)
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
            source_repository: sourceRepository);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Method.Should().Be(HttpMethod.Post);
        capturedRequest.RequestUri!.AbsolutePath.Should().Be("/api/projects");

        capturedBody.GetProperty("name").GetString().Should().Be("sabbour.me-blog");
        capturedBody.GetProperty("working_directory").GetString().Should().Be(@"C:\workspace\sabbour.me-blog");
        capturedBody.GetProperty("origin").GetString().Should().Be("github");
        capturedBody.GetProperty("source_repository").GetString().Should().Be(expectedSourceRepository);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("sabbour/sabbour.github.io", "https://github.com/sabbour/sabbour.github.io")]
    [InlineData("https://github.com/sabbour/sabbour.github.io.git", "https://github.com/sabbour/sabbour.github.io.git")]
    [InlineData("  sabbour/sabbour.github.io  ", "https://github.com/sabbour/sabbour.github.io")]
    [InlineData("git@github.com:sabbour/sabbour.github.io.git", "git@github.com:sabbour/sabbour.github.io.git")]
    public void NormalizeGitHubSourceRepository_MatchesToolContract(string? sourceRepository, string? expected)
    {
        var method = typeof(ProjectTools).GetMethod("NormalizeGitHubSourceRepository", BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();
        method!.Invoke(null, [sourceRepository]).Should().Be(expected);
    }

    private static McpServerTool BuildTool()
    {
        var method = typeof(ProjectTools).GetMethod(nameof(ProjectTools.ProjectCreateAsync), BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method {nameof(ProjectTools.ProjectCreateAsync)} not found.");
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
