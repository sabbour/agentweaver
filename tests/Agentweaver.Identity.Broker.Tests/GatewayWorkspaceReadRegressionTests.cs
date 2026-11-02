extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayWorkspaceReadRegressionTests
{
    private const string SelectionCode = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task WorkspaceRoutesPublishTypedOwnerBoundReadContracts()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var expected = new[]
        {
            ("listWorkspaceRepositoryRefs", "SourceControlWorkspaceRefsPage"),
            ("readWorkspaceRepositoryTree", "SourceControlWorkspaceTree"),
            ("readWorkspaceRepositoryFile", "SourceControlWorkspaceFile"),
        };

        foreach (var (operationId, responseSchema) in expected)
        {
            var route = Assert.Single(GatewayRouteCatalog.Routes, item => item.OperationId == operationId);
            Assert.Equal("GET", route.Method);
            Assert.Equal(GatewayOwner.Orchestrator, route.Owner);
            Assert.True(route.ForwardTenantSelector);
            Assert.True(route.ExcludeFromMcp);
            Assert.Equal(
                new[] { ProjectTeamSyncContract.SelectionCodeHeader },
                route.ForwardRequestHeaders.ToArray());
            Assert.False(route.HasJsonBody);

            var operation = paths.EnumerateObject()
                .Where(path => path.Value.TryGetProperty("get", out _))
                .Select(path => path.Value.GetProperty("get"))
                .Single(item => item.GetProperty("operationId").GetString() == operationId);
            Assert.Equal(route.OwnerPath, operation.GetProperty("x-agentweaver-owner-path").GetString());
            Assert.Equal("#/components/schemas/" + responseSchema,
                operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                    .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.Contains(
                operation.GetProperty("parameters").EnumerateArray(),
                parameter => parameter.GetProperty("name").GetString() == ProjectTeamSyncContract.SelectionCodeHeader &&
                    parameter.GetProperty("in").GetString() == "header" &&
                    parameter.GetProperty("required").GetBoolean());
        }
    }

    [Fact]
    public async Task FileReadEscapesNestedSegmentsAndRejectsEncodedTraversalBeforeForwarding()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        var forwarded = new List<(string Path, string? Selection)>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            (owner, request, _) =>
            {
                Assert.Equal(GatewayOwner.Orchestrator, owner);
                request.Headers.TryGetValues(ProjectTeamSyncContract.SelectionCodeHeader, out var selectionValues);
                forwarded.Add((request.RequestUri!.PathAndQuery, selectionValues?.SingleOrDefault()));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"repository":"octo/repo","path":"src/nested/a file.cs","commitSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","content":"ok","isBinary":false,"language":"csharp","size":2}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            });

        using var valid = CreateRequest(
            bearer,
            "/api/v1/projects/project-1/source-control/files/src/nested/a%20file.cs?ref=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        using var response = await gateway.Client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            "/api/projects/project-1/source-control/files/src/nested/a%20file.cs?ref=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Assert.Single(forwarded).Path);
        Assert.Equal(SelectionCode, Assert.Single(forwarded).Selection);

        foreach (var invalidPath in new[] { "src%252Fprivate.txt", "%252e%252e/private.txt", "src%25252Fprivate.txt" })
        {
            using var invalid = CreateRequest(
                bearer,
                $"/api/v1/projects/project-1/source-control/files/{invalidPath}?ref=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using var rejected = await gateway.Client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        Assert.Single(forwarded);
    }

    [Fact]
    public async Task FileReadForwardsMaximumMultibyteTextWithinRouteResponseLimit()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        const int maximumFileBytes = 1024 * 1024;
        var content = new string('é', maximumFileBytes / 2);
        var ownerBody = JsonSerializer.SerializeToUtf8Bytes(
            new SourceControlWorkspaceFile(
                "octo/repo",
                "file.txt",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                content,
                false,
                "plaintext",
                maximumFileBytes),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var route = Assert.Single(
            GatewayRouteCatalog.Routes,
            item => item.OperationId == "readWorkspaceRepositoryFile");
        Assert.Equal(maximumFileBytes, Encoding.UTF8.GetByteCount(content));
        Assert.True(ownerBody.Length > 2 * 1024 * 1024);
        Assert.True(ownerBody.Length <= route.MaximumResponseBytes);

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ownerBody)
                {
                    Headers = { ContentType = new("application/json") },
                },
            }));

        using var request = CreateRequest(
            bearer,
            "/api/v1/projects/project-1/source-control/files/file.txt?ref=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        using var response = await gateway.Client.SendAsync(request);
        var forwardedBody = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ownerBody, forwardedBody);
    }

    [Fact]
    public async Task TreeReadForwardsActualAdapterOutputAfterOwnerJsonExpansion()
    {
        const int nodeCount = 13_001;
        const int pathLength = 470;
        const int escapedCharactersPerPath = 53;
        const string commitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string treeSha = "cccccccccccccccccccccccccccccccccccccccc";
        const string blobSha = "dddddddddddddddddddddddddddddddddddddddd";
        var repository = new SourceControlRepositoryIdentity("octo", "repo");
        var providerTree = CreateProviderTree(treeSha, blobSha, nodeCount, pathLength, escapedCharactersPerPath);
        var providerHandler = new WorkspaceProviderHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == $"/repos/octo/repo/commits/{commitSha}")
                return JsonResponse(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    sha = commitSha,
                    commit = new { tree = new { sha = treeSha } }
                }));
            if (path == $"/repos/octo/repo/git/trees/{treeSha}?recursive=1")
                return JsonResponse(providerTree);
            throw new InvalidOperationException("Unexpected GitHub request.");
        });
        using var providerClient = new HttpClient(providerHandler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var adapter = new GitHubSourceControlAdapter(providerClient);
        var tree = await adapter.ReadWorkspaceTreeAsync(
            repository,
            commitSha,
            new SecretCredential("test-token", DateTimeOffset.UtcNow.AddMinutes(5)),
            CancellationToken.None);
        var ownerBody = JsonSerializer.SerializeToUtf8Bytes(
            tree,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(nodeCount, tree.Items.Length);
        Assert.Equal(7_410_647, providerTree.Length);
        Assert.True(providerTree.Length < 8 * 1024 * 1024);
        Assert.True(ownerBody.Length > 9 * 1024 * 1024);
        var route = Assert.Single(
            GatewayRouteCatalog.Routes,
            item => item.OperationId == "readWorkspaceRepositoryTree");
        Assert.True(ownerBody.Length <= route.MaximumResponseBytes);

        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            (_, request, _) =>
            {
                Assert.Equal(
                    "/api/projects/project-1/source-control/tree?ref=" + commitSha,
                    request.RequestUri!.PathAndQuery);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ownerBody)
                    {
                        Headers = { ContentType = new("application/json") },
                    },
                });
            });

        using var request = CreateRequest(
            bearer,
            "/api/v1/projects/project-1/source-control/tree?ref=" + commitSha);
        using var response = await gateway.Client.SendAsync(request);
        var forwardedBody = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ownerBody, forwardedBody);
    }

    private static byte[] CreateProviderTree(
        string treeSha,
        string blobSha,
        int nodeCount,
        int pathLength,
        int escapedCharactersPerPath)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("sha", treeSha);
            writer.WriteBoolean("truncated", false);
            writer.WriteStartArray("tree");
            for (var index = 0; index < nodeCount; index++)
            {
                var prefix = "entry-" + index.ToString("D5", CultureInfo.InvariantCulture);
                var path = prefix +
                    new string('a', pathLength - prefix.Length - escapedCharactersPerPath) +
                    new string('<', escapedCharactersPerPath);
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteString("mode", "100644");
                writer.WriteString("type", "blob");
                writer.WriteString("sha", blobSha);
                writer.WriteNumber("size", 1);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static HttpResponseMessage JsonResponse(byte[] body) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new("application/json") },
            },
        };

    private static HttpRequestMessage CreateRequest(string bearer, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
        request.Headers.Add(ProjectTeamSyncContract.SelectionCodeHeader, SelectionCode);
        return request;
    }

    private static string CreateBearer(SecurityKey signingKey)
    {
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "workspace-read-actor"), new Claim("scope", "api.read")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class WorkspaceProviderHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
