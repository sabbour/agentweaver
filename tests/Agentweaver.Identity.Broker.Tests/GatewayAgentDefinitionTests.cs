using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayAgentDefinitionTests
{
    private const string DownloadPath = "/agents/agentweaver.agent.md";

    [Fact]
    public async Task AnonymousDownloadMatchesCommittedDefinitionAndIsNotInOpenApi()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await StartGatewayAsync(rsa);

        // The client sends no token, so a 200 response proves the route is anonymous.
        using var response = await gateway.Client.GetAsync(DownloadPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

        // The Gateway serves an embedded copy. Compare it with the repository file so the
        // two copies cannot drift. Git may check out either copy with CRLF on Windows,
        // so compare text with normalized line endings.
        var served = await response.Content.ReadAsStringAsync();
        var committed = await File.ReadAllTextAsync(FindCommittedDefinition());
        Assert.Equal(NormalizeLineEndings(committed), NormalizeLineEndings(served));

        // Check the endpoint metadata as well, so the anonymous and hidden settings
        // stay explicit and do not depend on a default.
        var endpoint = Assert.Single(gateway.Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == DownloadPath);
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.True(endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription);

        // The MCP host builds tools from this document, so the download path must not appear in it.
        using var openApiResponse = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApiResponse.StatusCode);
        using var document = JsonDocument.Parse(await openApiResponse.Content.ReadAsStringAsync());
        Assert.False(document.RootElement.GetProperty("paths").TryGetProperty(DownloadPath, out _));
    }

    [Fact]
    public async Task ServedToolMapMatchesMcpAdmissibleOperations()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await StartGatewayAsync(rsa);
        var definition = await gateway.Client.GetStringAsync(DownloadPath);

        // Only names inside the generated block count. Other sections of the file may
        // mention tools in examples, and those mentions must not hide a missing map entry.
        const string beginMarker = "<!-- BEGIN GENERATED:tool-map -->";
        const string endMarker = "<!-- END GENERATED:tool-map -->";
        var begin = definition.IndexOf(beginMarker, StringComparison.Ordinal);
        var end = definition.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "Restore the generated tool-map markers in both operator definition copies.");
        var toolMap = definition[(begin + beginMarker.Length)..end];

        // Tool names in the map are written as code spans. Each name must appear once,
        // so a duplicate entry fails here instead of being hidden by the set comparison.
        var listedNames = Regex.Matches(toolMap, "`(agentweaver_[A-Za-z0-9_]+)`")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.NotEmpty(listedNames);
        var actual = listedNames.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(listedNames.Length, actual.Count);

        // Build the expected tool list with the same rules the MCP host uses: only
        // /api/v1/ paths, only the five HTTP methods it maps, and no streaming operations.
        // Each tool name is the agentweaver_ prefix plus the operationId.
        using var document = JsonDocument.Parse(await gateway.Client.GetStringAsync("/openapi/v1.json"));
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!path.Name.StartsWith("/api/v1/", StringComparison.Ordinal))
                continue;
            foreach (var method in path.Value.EnumerateObject())
            {
                // Path items can also hold non-method keys such as parameters; skip them.
                if (method.Name is not ("get" or "post" or "put" or "patch" or "delete"))
                    continue;
                // Streaming (SSE) operations carry this marker and are not MCP tools.
                if (method.Value.TryGetProperty("x-agentweaver-stream", out var stream) &&
                    stream.ValueKind == JsonValueKind.String)
                    continue;
                expected.Add("agentweaver_" + method.Value.GetProperty("operationId").GetString());
            }
        }

        // Guard against an empty document, which would make both sets trivially equal.
        Assert.NotEmpty(expected);

        // Report both directions so a failure says exactly which map lines to add or remove.
        var missing = expected.Except(actual).Order(StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expected).Order(StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0 && extra.Length == 0,
            "Update the tool-map block in .github/agents/agentweaver.agent.md and the embedded " +
            "services/gateway/Agentweaver.Gateway/Agents/agentweaver.agent.md copy to match " +
            $"GET /openapi/v1.json. Missing: [{string.Join(", ", missing)}]. Extra: [{string.Join(", ", extra)}].");
    }

    private static Task<GatewayResourceServer> StartGatewayAsync(RSA rsa) =>
        // The download must be served from the Gateway alone. These callbacks throw so
        // the test fails if the route ever calls Projects or a service owner.
        GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => throw new InvalidOperationException("Agent downloads must not contact Projects."),
            (_, _, _) => throw new InvalidOperationException("Agent downloads must not contact an owner."));

    private static string FindCommittedDefinition()
    {
        // Tests run from the bin folder. Walk up until the solution file marks the repository root.
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Agentweaver.slnx")))
                return Path.Combine(directory.FullName, ".github", "agents", "agentweaver.agent.md");
        }
        throw new InvalidOperationException("Cannot locate the repository's Agentweaver.slnx.");
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
