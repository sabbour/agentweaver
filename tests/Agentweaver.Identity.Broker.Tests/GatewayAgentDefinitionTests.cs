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

        using var response = await gateway.Client.GetAsync(DownloadPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        var served = await response.Content.ReadAsStringAsync();
        var committed = await File.ReadAllTextAsync(FindCommittedDefinition());
        Assert.Equal(NormalizeLineEndings(committed), NormalizeLineEndings(served));

        var endpoint = Assert.Single(gateway.Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == DownloadPath);
        Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        Assert.True(endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription);

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
        const string beginMarker = "<!-- BEGIN GENERATED:tool-map -->";
        const string endMarker = "<!-- END GENERATED:tool-map -->";
        var begin = definition.IndexOf(beginMarker, StringComparison.Ordinal);
        var end = definition.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin, "Restore the generated tool-map markers in both operator definition copies.");
        var toolMap = definition[(begin + beginMarker.Length)..end];
        var listedNames = Regex.Matches(toolMap, "`(agentweaver_[A-Za-z0-9_]+)`")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.NotEmpty(listedNames);
        var actual = listedNames.ToHashSet(StringComparer.Ordinal);
        Assert.Equal(listedNames.Length, actual.Count);

        using var document = JsonDocument.Parse(await gateway.Client.GetStringAsync("/openapi/v1.json"));
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!path.Name.StartsWith("/api/v1/", StringComparison.Ordinal))
                continue;
            foreach (var method in path.Value.EnumerateObject())
            {
                if (method.Name is not ("get" or "post" or "put" or "patch" or "delete"))
                    continue;
                if (method.Value.TryGetProperty("x-agentweaver-stream", out var stream) &&
                    stream.ValueKind == JsonValueKind.String)
                    continue;
                expected.Add("agentweaver_" + method.Value.GetProperty("operationId").GetString());
            }
        }

        Assert.NotEmpty(expected);
        var missing = expected.Except(actual).Order(StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expected).Order(StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0 && extra.Length == 0,
            "Update the tool-map block in .github/agents/agentweaver.agent.md and the embedded " +
            "services/gateway/Agentweaver.Gateway/Agents/agentweaver.agent.md copy to match " +
            $"GET /openapi/v1.json. Missing: [{string.Join(", ", missing)}]. Extra: [{string.Join(", ", extra)}].");
    }

    private static Task<GatewayResourceServer> StartGatewayAsync(RSA rsa) =>
        GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => throw new InvalidOperationException("Agent downloads must not contact Projects."),
            (_, _, _) => throw new InvalidOperationException("Agent downloads must not contact an owner."));

    private static string FindCommittedDefinition()
    {
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
