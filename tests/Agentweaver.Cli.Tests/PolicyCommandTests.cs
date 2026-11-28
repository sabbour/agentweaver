using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;
using Agentweaver.Gateway;

namespace Agentweaver.Cli.Tests;

[Collection(CliEnvironmentCollection.Name)]
public sealed class PolicyCommandTests
{
    private const string Time = "2026-11-16T00:00:00+00:00";
    private const string Canary = "POLICY_PRIVATE_CANARY";
    private const string ArtifactCanary = "67,65,78,65,82,89";
    private const string ProjectId = "00000000000000000000000000000042";
    private static readonly string Hash = ArtifactHash(3);
    private static readonly string[] Target =
    [
        "--gateway-url", "https://gateway.example/", "--broker-issuer", "https://identity.example/",
        "--api-audience", "https://gateway.example/", "--tenant-id", "tenant-1",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShowProjectsOnlyExactMetadata(bool exact)
    {
        var fixture = new Fixture { Body = ShowBody(exact) };
        var result = await fixture.Run(exact
            ? ["show", "--scope", "platform", "--revision", "2"]
            : ["show", "--scope", "platform"]);
        Assert.Equal(0, result);
        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(
            (exact ? new[] { "contentSha256", "createdAt", "policyId", "revision", "scopeId", "scopeType" }
                   : new[] { "contentSha256", "policyId", "revision", "scopeId", "scopeType", "updatedAt" }),
            output.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(2, output["revision"]!.GetValue<long>());
        Assert.Equal(Hash, output["contentSha256"]!.GetValue<string>());
        Assert.Equal(Time, output[exact ? "createdAt" : "updatedAt"]!.GetValue<string>());
        Assert.Equal(exact ? "/api/v1/policies/platform/revisions/2" : "/api/v1/policies/platform", fixture.PolicyPath);
        Assert.DoesNotContain(Canary, fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
        Assert.Null(fixture.Authentication.Identity!.ProjectId);
        Assert.Null(fixture.Authentication.Identity.RunId);
        Assert.Equal(["openid", "api.read", "offline_access"], fixture.Authentication.Identity.Scopes);
        Assert.Equal(InteractionPolicy.Never, fixture.Authentication.Interaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectShowReadsExpectedScopeAndKeepsIdentityUnbound(bool exact)
    {
        var fixture = new Fixture { Body = ProjectBody(exact), UseGatewayContract = true };
        var args = exact
            ? new[] { "show", "--scope", "project", "--project-id", ProjectId, "--revision", "2" }
            : new[] { "show", "--scope", "project", "--project-id", ProjectId };

        Assert.Equal(0, await fixture.Run(args));

        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal("project", output["scopeType"]!.GetValue<string>());
        Assert.Equal(ProjectId, output["scopeId"]!.GetValue<string>());
        Assert.Equal("canonical", output["policyId"]!.GetValue<string>());
        Assert.Equal(2, output["revision"]!.GetValue<long>());
        Assert.Equal(Hash, output["contentSha256"]!.GetValue<string>());
        Assert.Equal(Time, output[exact ? "createdAt" : "updatedAt"]!.GetValue<string>());
        Assert.Equal(
            exact
                ? $"/api/v1/policies/projects/{ProjectId}/revisions/2"
                : $"/api/v1/policies/projects/{ProjectId}",
            fixture.PolicyPath);
        Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
        Assert.Null(fixture.Authentication.Identity!.ProjectId);
        Assert.Null(fixture.Authentication.Identity.RunId);
        Assert.Equal(["openid", "api.read", "offline_access"], fixture.Authentication.Identity.Scopes);
        Assert.Equal(InteractionPolicy.Never, fixture.Authentication.Interaction);
    }

    [Fact]
    public async Task ProjectHistoryReturnsOnePageAndForwardsTheCursor()
    {
        var fixture = new Fixture { Body = ProjectHistoryBody(), UseGatewayContract = true };

        Assert.Equal(0, await fixture.Run(
            "history", "--scope", "project", "--project-id", ProjectId, "--page-size", "1", "--cursor", "NEXT"));

        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal("project", output["scopeType"]!.GetValue<string>());
        Assert.Equal(ProjectId, output["scopeId"]!.GetValue<string>());
        Assert.Equal(3, output["snapshotRevision"]!.GetValue<long>());
        Assert.Equal("NEXT", output["nextCursor"]!.GetValue<string>());
        Assert.Single(output["items"]!.AsArray());
        Assert.Equal($"/api/v1/policies/projects/{ProjectId}/revisions", fixture.PolicyPath);
        Assert.Contains("pageSize=1", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Contains("cursor=NEXT", fixture.PolicyQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectListReturnsVisibleHeadsAndOneCursorPage()
    {
        const string nextCursor = "NEXT_PAGE";
        var fixture = new Fixture
        {
            Body = ProjectListBody(nextCursor),
            UseGatewayContract = true,
        };

        Assert.Equal(0, await fixture.Run(
            "list", "--scope", "project", "--page-size", "2", "--cursor", "PREVIOUS_PAGE"));

        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal("project", output["scopeType"]!.GetValue<string>());
        Assert.Equal(nextCursor, output["nextCursor"]!.GetValue<string>());
        var items = output["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal(ProjectId, items[0]!["scopeId"]!.GetValue<string>());
        Assert.Equal("00000000000000000000000000000043", items[1]!["scopeId"]!.GetValue<string>());
        Assert.All(items, item => Assert.Equal("project", item!["scopeType"]!.GetValue<string>()));
        Assert.Equal("/api/v1/policies/projects", fixture.PolicyPath);
        Assert.Contains("pageSize=2", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Contains("cursor=PREVIOUS_PAGE", fixture.PolicyQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectListAcceptsAnEmptyPage()
    {
        var fixture = new Fixture
        {
            Body = """{"items":[],"nextCursor":null}""",
            UseGatewayContract = true,
        };

        Assert.Equal(0, await fixture.Run("list", "--scope", "project"));

        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Empty(output["items"]!.AsArray());
        Assert.Null(output["nextCursor"]);
    }

    [Fact]
    public async Task ProjectListRejectsOutOfOrderScopeRows()
    {
        var body = JsonNode.Parse(ProjectListBody(null))!.AsObject();
        var items = body["items"]!.AsArray();
        var first = items[0]!.DeepClone();
        items[0] = items[1]!.DeepClone();
        items[1] = first;
        var fixture = new Fixture { Body = body.ToJsonString(), UseGatewayContract = true };

        Assert.Equal(1, await fixture.Run("list", "--scope", "project"));
        Assert.Empty(fixture.Output.ToString());
    }

    [Fact]
    public async Task ProjectHistoryAcceptsEmptyLineage()
    {
        var fixture = new Fixture
        {
            Body = $$"""{"scopeType":"project","scopeId":"{{ProjectId}}","policyId":null,"snapshotRevision":null,"items":[],"nextCursor":null}""",
            UseGatewayContract = true,
        };

        Assert.Equal(0, await fixture.Run("history", "--scope", "project", "--project-id", ProjectId));

        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Null(output["policyId"]);
        Assert.Null(output["snapshotRevision"]);
        Assert.Empty(output["items"]!.AsArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectExportWritesFullVerifiedArtifactAndOnlyMetadata(bool exact)
    {
        var outputPath = NewOutputPath();
        var fixture = new Fixture { Body = ProjectBody(exact), UseGatewayContract = true };
        var args = exact
            ? new[] { "export", "--scope", "project", "--project-id", ProjectId, "--revision", "2", "--output", outputPath }
            : new[] { "export", "--scope", "project", "--project-id", ProjectId, "--output", outputPath };
        try
        {
            Assert.Equal(0, await fixture.Run(args));

            var exported = JsonNode.Parse(await File.ReadAllTextAsync(outputPath))!.AsObject();
            Assert.Equal("project", exported["scopeType"]!.GetValue<string>());
            Assert.Equal(ProjectId, exported["scopeId"]!.GetValue<string>());
            Assert.Equal("canonical", exported["policyId"]!.GetValue<string>());
            Assert.Equal(2, exported["revision"]!.GetValue<long>());
            Assert.Equal(Hash, exported["contentSha256"]!.GetValue<string>());
            Assert.Equal(Time, exported[exact ? "createdAt" : "updatedAt"]!.GetValue<string>());
            Assert.Equal(Hash, exported["artifact"]!["contentSha256"]!.GetValue<string>());
            Assert.Equal(new byte[] { 65, 65, 65 },
                exported["artifact"]!["manifestBytes"]!.AsArray()
                    .Select(value => value!.GetValue<byte>()).ToArray());
            var files = exported["artifact"]!["files"]!.AsArray();
            Assert.Equal(2, files.Count);
            Assert.Equal("modules/network.rego", files[0]!["relativePath"]!.GetValue<string>());
            Assert.Equal(new byte[] { 80, 79, 76, 73, 67, 89 },
                files[0]!["bytes"]!.AsArray().Select(value => value!.GetValue<byte>()).ToArray());
            Assert.Equal("data/policy.json", files[1]!["relativePath"]!.GetValue<string>());
            Assert.Equal(
                new byte[] { 123, 34, 101, 110, 97, 98, 108, 101, 100, 34, 58, 116, 114, 117, 101, 125 },
                files[1]!["bytes"]!.AsArray().Select(value => value!.GetValue<byte>()).ToArray());
            Assert.DoesNotContain(Canary, exported.ToJsonString(), StringComparison.Ordinal);

            var summary = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.Equal("project", summary["scopeType"]!.GetValue<string>());
            Assert.Equal(ProjectId, summary["scopeId"]!.GetValue<string>());
            Assert.Equal(outputPath, summary["output"]!.GetValue<string>());
            Assert.DoesNotContain("manifestBytes", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.Null(fixture.Authentication.Identity!.ProjectId);
            Assert.Null(fixture.Authentication.Identity.RunId);
            Assert.Equal(["openid", "api.read", "offline_access"], fixture.Authentication.Identity.Scopes);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData(200, "application/json")]
    [InlineData(200, "text/plain")]
    [InlineData(403, "application/json")]
    public async Task ProjectArtifactResponsesNeverAppearInEvidence(int status, string mediaType)
    {
        var fixture = new Fixture
        {
            Body = ProjectBodyWithCanary(exact: false),
            Status = (HttpStatusCode)status,
            MediaType = mediaType,
            UseGatewayContract = true,
        };

        var result = await fixture.Run("show", "--scope", "project", "--project-id", ProjectId);

        Assert.Equal(status == 200 && mediaType == "application/json" ? 0 : 1, result);
        Assert.DoesNotContain(ArtifactCanary, fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ArtifactCanary, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ArtifactCanary, fixture.Client!.Calls.Last().ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["responseBody"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProjectShowRejectsAResponseForAnotherProject()
    {
        var body = JsonNode.Parse(ProjectBody(exact: false))!.AsObject();
        body["scopeId"] = "00000000000000000000000000000043";
        var fixture = new Fixture { Body = body.ToJsonString(), UseGatewayContract = true };

        Assert.Equal(1, await fixture.Run("show", "--scope", "project", "--project-id", ProjectId));
        Assert.Empty(fixture.Output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("project-id")]
    [InlineData("00000000-0000-0000-0000-000000000042")]
    public async Task ProjectReadsRequireCanonicalProjectIdBeforeNetwork(string projectId)
    {
        var fixture = new Fixture();

        Assert.Equal(1, await fixture.Run("show", "--scope", "project", "--project-id", projectId));
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task ProjectListCursorUsesThePublishedGatewayBound()
    {
        var cursor = new string('A', 4096);
        var fixture = new Fixture { Body = ProjectListBody(null), UseGatewayContract = true };

        Assert.Equal(0, await fixture.Run("list", "--scope", "project", "--cursor", cursor));
        Assert.Equal("/api/v1/policies/projects", fixture.PolicyPath);
        Assert.Contains(cursor, fixture.PolicyQuery, StringComparison.Ordinal);

        var client = fixture.Client!;
        var policyRequestCount = fixture.PolicyRequestCount;
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InvokeAsync(
            "listProjectNativeAcsPolicies",
            new GatewayInvocation(Query: new Dictionary<string, string> { ["cursor"] = new string('A', 4097) }),
            CancellationToken.None));
        Assert.Equal(policyRequestCount, fixture.PolicyRequestCount);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InvokeAsync(
            "listProjectNativeAcsPolicyRevisions",
            new GatewayInvocation(
                new Dictionary<string, string> { ["projectId"] = ProjectId },
                new Dictionary<string, string> { ["cursor"] = new string('A', 2049) }),
            CancellationToken.None));
        Assert.Equal(policyRequestCount, fixture.PolicyRequestCount);

        await client.InvokeAsync(
            "listProjectNativeAcsPolicyRevisions",
            new GatewayInvocation(
                new Dictionary<string, string> { ["projectId"] = ProjectId },
                new Dictionary<string, string> { ["cursor"] = new string('A', 2048) }),
            CancellationToken.None);
        Assert.Equal($"/api/v1/policies/projects/{ProjectId}/revisions", fixture.PolicyPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportWritesCompleteArtifactAndOnlyMetadataToStandardOutput(bool exact)
    {
        var outputPath = NewOutputPath();
        var fixture = new Fixture { Body = ShowBody(exact) };
        try
        {
            Assert.Equal(0, await fixture.Run(exact
                ? ["export", "--scope", "platform", "--revision", "2", "--output", outputPath]
                : ["export", "--scope", "platform", "--output", outputPath]));

            var exported = JsonNode.Parse(await File.ReadAllTextAsync(outputPath))!.AsObject();
            Assert.Equal(
                (exact
                    ? new[] { "artifact", "contentSha256", "createdAt", "policyId", "revision", "scopeId", "scopeType" }
                    : new[] { "artifact", "contentSha256", "policyId", "revision", "scopeId", "scopeType", "updatedAt" }),
                exported.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal("platform", exported["scopeType"]!.GetValue<string>());
            Assert.Equal("default", exported["scopeId"]!.GetValue<string>());
            Assert.Equal("canonical", exported["policyId"]!.GetValue<string>());
            Assert.Equal(2, exported["revision"]!.GetValue<long>());
            Assert.Equal(Hash, exported["contentSha256"]!.GetValue<string>());
            Assert.Equal(Time, exported[exact ? "createdAt" : "updatedAt"]!.GetValue<string>());

            var artifact = exported["artifact"]!.AsObject();
            Assert.Equal(["contentSha256", "files", "manifestBytes"],
                artifact.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal(Hash, artifact["contentSha256"]!.GetValue<string>());
            Assert.Equal(new byte[] { 65, 65, 65 },
                artifact["manifestBytes"]!.AsArray().Select(value => value!.GetValue<byte>()).ToArray());
            var files = artifact["files"]!.AsArray();
            Assert.Equal(2, files.Count);
            Assert.Equal("modules/network.rego", files[0]!["relativePath"]!.GetValue<string>());
            Assert.Equal(new byte[] { 80, 79, 76, 73, 67, 89 },
                files[0]!["bytes"]!.AsArray().Select(value => value!.GetValue<byte>()).ToArray());
            Assert.Equal("data/policy.json", files[1]!["relativePath"]!.GetValue<string>());
            Assert.Equal(new byte[] { 123, 34, 101, 110, 97, 98, 108, 101, 100, 34, 58, 116, 114, 117, 101, 125 },
                files[1]!["bytes"]!.AsArray().Select(value => value!.GetValue<byte>()).ToArray());
            Assert.DoesNotContain("private", exported.ToJsonString(), StringComparison.Ordinal);

            var summary = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.Equal(
                (exact
                    ? new[] { "contentSha256", "createdAt", "output", "policyId", "revision", "scopeId", "scopeType" }
                    : new[] { "contentSha256", "output", "policyId", "revision", "scopeId", "scopeType", "updatedAt" }),
                summary.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal(outputPath, summary["output"]!.GetValue<string>());
            Assert.DoesNotContain("manifestBytes", fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Equal(exact
                    ? "/api/v1/policies/platform/revisions/2"
                    : "/api/v1/policies/platform",
                fixture.PolicyPath);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.Null(fixture.Authentication.Identity!.ProjectId);
            Assert.Null(fixture.Authentication.Identity.RunId);
            Assert.Equal(["openid", "api.read", "offline_access"], fixture.Authentication.Identity.Scopes);
            Assert.Equal(InteractionPolicy.Never, fixture.Authentication.Interaction);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ExportRejectsHashMismatchWithoutCreatingAFile()
    {
        var outputPath = NewOutputPath();
        var body = JsonNode.Parse(ShowBody(exact: false))!.AsObject();
        body["artifact"]!["manifestBytes"]![0] = 66;
        var fixture = new Fixture { Body = body.ToJsonString() };
        try
        {
            Assert.Equal(1, await fixture.Run("export", "--scope", "platform", "--output", outputPath));
            Assert.False(File.Exists(outputPath));
            Assert.Empty(fixture.Output.ToString());
            Assert.Contains("invalid policy response", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.DoesNotContain("65,65,65", fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("65,65,65", fixture.Client.Calls.Last().ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateReadsExportAndProjectsOnlyServerDiagnostics(bool exact)
    {
        var inputPath = NewInputPath();
        var exportFixture = new Fixture { Body = ShowBodyWithCanary(exact) };
        var fixture = new Fixture
        {
            Body = """{"isValid":true,"diagnostics":[{"code":"validated","message":"Artifact accepted","private":"POLICY_PRIVATE_CANARY"}],"manifestBytes":[67,65,78,65,82,89]}""",
        };
        try
        {
            Assert.Equal(0, await exportFixture.Run(exact
                ? ["export", "--scope", "platform", "--revision", "2", "--output", inputPath]
                : ["export", "--scope", "platform", "--output", inputPath]));
            Assert.Equal(0, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            Assert.Equal("POST", fixture.PolicyMethod);
            Assert.Equal("/api/v1/policies/platform/validate", fixture.PolicyPath);
            using var request = JsonDocument.Parse(fixture.PolicyBody!);
            Assert.Equal(["artifact"], request.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal(
                new byte[] { 67, 65, 78, 65, 82, 89 },
                request.RootElement.GetProperty("artifact").GetProperty("manifestBytes")
                    .EnumerateArray().Select(value => value.GetByte()).ToArray());
            var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.Equal(["diagnostics", "isValid"], result.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.True(result["isValid"]!.GetValue<bool>());
            var diagnostic = result["diagnostics"]!.AsArray()[0]!.AsObject();
            Assert.Equal(["code", "column", "line", "message", "relativePath"],
                diagnostic.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal("validated", diagnostic["code"]!.GetValue<string>());
            Assert.Equal("Artifact accepted", diagnostic["message"]!.GetValue<string>());
            Assert.DoesNotContain("private", fixture.Output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ArtifactCanary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["requestBody"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.DoesNotContain(ArtifactCanary, fixture.Client.Calls.Last().ToJsonString(), StringComparison.Ordinal);
            Assert.Null(fixture.Authentication.Identity!.ProjectId);
            Assert.Null(fixture.Authentication.Identity.RunId);
            Assert.Equal(["openid", "api.read", "offline_access"], fixture.Authentication.Identity.Scopes);
            Assert.Equal(InteractionPolicy.Never, fixture.Authentication.Interaction);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ValidateReturnsBoundedDiagnosticsForTheDocumentedInvalidResponse()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture
        {
            Status = HttpStatusCode.UnprocessableEntity,
            Body = $$"""{"code":"native_acs_policy_invalid","detail":"{{Canary}}","diagnostics":[{"code":"manifest_shape","message":"The manifest has an invalid shape.","relativePath":"manifest.yaml","line":2,"column":3,"private":"{{Canary}}"}],"manifestBytes":[67,65,78,65,82,89]}""",
        };
        try
        {
            Assert.Equal(1, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.Equal(["diagnostics", "isValid"], result.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.False(result["isValid"]!.GetValue<bool>());
            var diagnostic = result["diagnostics"]!.AsArray()[0]!.AsObject();
            Assert.Equal("manifest_shape", diagnostic["code"]!.GetValue<string>());
            Assert.Equal("The manifest has an invalid shape.", diagnostic["message"]!.GetValue<string>());
            Assert.Equal("manifest.yaml", diagnostic["relativePath"]!.GetValue<string>());
            Assert.Equal(2, diagnostic["line"]!.GetValue<int>());
            Assert.Equal(3, diagnostic["column"]!.GetValue<int>());
            Assert.DoesNotContain(Canary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["requestBody"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.DoesNotContain(Canary, fixture.Client.Calls.Last().ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, fixture.Client.Calls.Last().ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ValidateRejectsInvalidAndOversizedInputBeforeGatewayDiscovery()
    {
        var inputPath = NewInputPath();
        var invalid = JsonNode.Parse(ExportInput(exact: false))!.AsObject();
        invalid["artifact"]!["manifestBytes"]![0] = 66;
        await File.WriteAllTextAsync(inputPath, invalid.ToJsonString());
        var fixture = new Fixture();
        try
        {
            Assert.Equal(1, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            Assert.Equal(0, fixture.RequestCount);
            Assert.Empty(fixture.Output.ToString());
            Assert.Contains("export file is invalid", fixture.Error.ToString(), StringComparison.OrdinalIgnoreCase);

            invalid = JsonNode.Parse(ExportInput(exact: false))!.AsObject();
            invalid["artifact"]!["private"] = Canary;
            await File.WriteAllTextAsync(inputPath, invalid.ToJsonString());
            fixture = new Fixture();
            Assert.Equal(1, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            Assert.Equal(0, fixture.RequestCount);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);

            invalid = JsonNode.Parse(ExportInput(exact: false))!.AsObject();
            invalid["artifact"]!["files"]![0]!["private"] = Canary;
            await File.WriteAllTextAsync(inputPath, invalid.ToJsonString());
            fixture = new Fixture();
            Assert.Equal(1, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            Assert.Equal(0, fixture.RequestCount);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);

            await using (var stream = new FileStream(inputPath, FileMode.Create, FileAccess.Write, FileShare.None))
                stream.SetLength(64L * 1024 * 1024 + 1);
            fixture = new Fixture();
            Assert.Equal(1, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            Assert.Equal(0, fixture.RequestCount);
            Assert.Empty(fixture.Output.ToString());
            Assert.Contains("64 MiB", fixture.Error.ToString(), StringComparison.Ordinal);

            fixture = new Fixture();
            Assert.Equal(1, await fixture.Run("validate", "--scope", "platform"));
            Assert.Equal(0, fixture.RequestCount);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ValidateRejectsAnAdvertisedRequestBoundAboveThePolicyApiLimit()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture { UnsafeValidationRequestBound = true };
        try
        {
            Assert.Equal(1, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            Assert.Null(fixture.PolicyPath);
            Assert.Equal(1, fixture.RequestCount);
            Assert.Empty(fixture.Output.ToString());
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ValidateRejectsAnOperationWithTheWrongPath()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture { WrongValidationPath = true };
        try
        {
            Assert.Equal(1, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            Assert.Null(fixture.PolicyPath);
            Assert.Equal(1, fixture.RequestCount);
            Assert.Empty(fixture.Output.ToString());
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Theory]
    [InlineData(5242880)]
    [InlineData(5242881)]
    public async Task ValidateChecksTheSerializedRequestSize(int requestBytes)
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInputWithRequestSize(requestBytes));
        var fixture = new Fixture { Body = """{"isValid":true,"diagnostics":[]}""" };
        try
        {
            var result = await fixture.Run("validate", "--scope", "platform", "--input", inputPath);
            if (requestBytes == 5242880)
            {
                Assert.Equal(0, result);
                Assert.Equal("POST", fixture.PolicyMethod);
                Assert.Equal(requestBytes, Encoding.UTF8.GetByteCount(fixture.PolicyBody!));
            }
            else
            {
                Assert.Equal(1, result);
                Assert.Null(fixture.PolicyPath);
                Assert.Equal(1, fixture.RequestCount);
                Assert.DoesNotContain(ArtifactCanary, fixture.Error.ToString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Theory]
    [InlineData(200, "application/json")]
    [InlineData(200, "text/plain")]
    [InlineData(500, "text/plain")]
    public async Task ValidateArtifactNeverAppearsInGatewayEvidence(int status, string mediaType)
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture
        {
            Status = (HttpStatusCode)status,
            MediaType = mediaType,
            Body = mediaType == "application/json"
                ? """{"isValid":true,"diagnostics":[],"manifestBytes":[67,65,78,65,82,89]}"""
                : $$"""{{Canary}} 67,65,78,65,82,89""",
        };
        try
        {
            await fixture.Run("validate", "--scope", "platform", "--input", inputPath);
            var evidence = fixture.Client!.Calls.Last().ToJsonString();
            Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["requestBody"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.DoesNotContain(Canary, evidence, StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, evidence, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, fixture.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectValidateReadsProjectExportWithUnboundAdminIdentity(bool exact)
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ProjectExportInput(exact));
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            Body = """{"isValid":true,"diagnostics":[],"private":"POLICY_PRIVATE_CANARY"}""",
        };
        try
        {
            Assert.Equal(0, await fixture.Run(
                "validate", "--scope", "project", "--project-id", ProjectId, "--input", inputPath));

            Assert.Equal("POST", fixture.PolicyMethod);
            Assert.Equal($"/api/v1/policies/projects/{ProjectId}/validate", fixture.PolicyPath);
            Assert.True(string.IsNullOrEmpty(fixture.PolicyQuery));
            Assert.Null(fixture.PolicyProjectAuthorizationHeader);
            var request = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            Assert.Equal(["artifact"], request.Select(pair => pair.Key));
            Assert.DoesNotContain(Canary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["requestBody"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.Null(fixture.Authentication.Identity!.ProjectId);
            Assert.Null(fixture.Authentication.Identity.RunId);
            Assert.Equal(
                ["openid", "api.read", "offline_access", "projects.admin"],
                fixture.Authentication.Identity.Scopes);
            Assert.Equal(InteractionPolicy.Never, fixture.Authentication.Interaction);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ProjectValidateReturnsOnlySafeBoundedDiagnostics()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ProjectExportInput(exact: false));
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            Status = HttpStatusCode.UnprocessableEntity,
            Body = $$"""{"code":"native_acs_policy_invalid","detail":"{{Canary}}","diagnostics":[{"code":"file_path","message":"The path is unsafe.","relativePath":"../{{Canary}}","line":2,"column":3,"private":"{{Canary}}"}],"manifestBytes":[67,65,78,65,82,89]}""",
        };
        try
        {
            Assert.Equal(1, await fixture.Run(
                "validate", "--scope", "project", "--project-id", ProjectId, "--input", inputPath));

            var result = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.False(result["isValid"]!.GetValue<bool>());
            var diagnostic = Assert.Single(result["diagnostics"]!.AsArray())!.AsObject();
            Assert.Equal(["code", "column", "line", "message", "relativePath"],
                diagnostic.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Null(diagnostic["relativePath"]);
            Assert.DoesNotContain(Canary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Client!.Calls.Last().ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ProjectValidateRejectsScopeProjectAndDigestBeforeDiscovery()
    {
        var inputPath = NewInputPath();
        try
        {
            var wrongScope = JsonNode.Parse(ProjectExportInput(exact: false))!.AsObject();
            wrongScope["scopeType"] = "platform";
            wrongScope["scopeId"] = "default";
            await File.WriteAllTextAsync(inputPath, wrongScope.ToJsonString());
            var fixture = new Fixture();
            Assert.Equal(1, await fixture.Run(
                "validate", "--scope", "project", "--project-id", ProjectId, "--input", inputPath));
            Assert.Equal(0, fixture.RequestCount);

            var wrongProject = JsonNode.Parse(ProjectExportInput(exact: false))!.AsObject();
            wrongProject["scopeId"] = "00000000000000000000000000000043";
            await File.WriteAllTextAsync(inputPath, wrongProject.ToJsonString());
            fixture = new Fixture();
            Assert.Equal(1, await fixture.Run(
                "validate", "--scope", "project", "--project-id", ProjectId, "--input", inputPath));
            Assert.Equal(0, fixture.RequestCount);

            var wrongHash = JsonNode.Parse(ProjectExportInput(exact: false))!.AsObject();
            wrongHash["artifact"]!["manifestBytes"]![0] = 66;
            await File.WriteAllTextAsync(inputPath, wrongHash.ToJsonString());
            fixture = new Fixture();
            Assert.Equal(1, await fixture.Run(
                "validate", "--scope", "project", "--project-id", ProjectId, "--input", inputPath));
            Assert.Equal(0, fixture.RequestCount);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("project-id")]
    [InlineData("00000000-0000-0000-0000-000000000042")]
    public async Task ProjectValidateRejectsNonCanonicalProjectIdBeforeDiscovery(string projectId)
    {
        var fixture = new Fixture();

        Assert.Equal(1, await fixture.Run(
            "validate", "--scope", "project", "--project-id", projectId, "--input", "missing.json"));
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task ProjectValidateRejectsRunBindingBeforeDiscovery()
    {
        var fixture = new Fixture();

        Assert.Equal(1, await fixture.Run(
            "validate", "--scope", "project", "--project-id", ProjectId,
            "--input", "missing.json", "--run-id", "run-a"));
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task ProjectValidateRejectsCompactRequestAboveFiveMiBBeforeDiscovery()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ProjectExportInputWithRequestSize(5242881));
        var fixture = new Fixture();
        try
        {
            Assert.Equal(1, await fixture.Run(
                "validate", "--scope", "project", "--project-id", ProjectId, "--input", inputPath));
            Assert.Equal(0, fixture.RequestCount);
            Assert.Empty(fixture.Output.ToString());
            Assert.DoesNotContain(ArtifactCanary, fixture.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ProjectValidateHonorsSmallerAdvertisedRequestLimit()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ProjectExportInput(exact: false));
        var fixture = new Fixture { ProjectValidationRequestBound = 1 };
        try
        {
            Assert.Equal(1, await fixture.Run(
                "validate", "--scope", "project", "--project-id", ProjectId, "--input", inputPath));
            Assert.Equal(1, fixture.RequestCount);
            Assert.Null(fixture.PolicyPath);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Theory]
    [InlineData("wrong-reference")]
    [InlineData("inline")]
    [InlineData("missing")]
    [InlineData("missing-component")]
    [InlineData("optional-artifact")]
    public async Task ProjectValidateRejectsIncompatiblePublishedSchemaBeforeAuthOrPost(string mutation)
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ProjectExportInput(exact: false));
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            ProjectValidationSchemaMutation = mutation,
        };
        try
        {
            Assert.Equal(1, await fixture.Run(
                "validate", "--scope", "project", "--project-id", ProjectId, "--input", inputPath));

            Assert.Equal(1, fixture.RequestCount);
            Assert.Equal(0, fixture.PolicyRequestCount);
            Assert.Null(fixture.PolicyPath);
            Assert.Null(fixture.Authentication.Identity);
            Assert.Empty(fixture.Output.ToString());
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ProjectValidateRedactsTransportExceptionText()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ProjectExportInput(exact: false));
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            PolicyException = new HttpRequestException(Canary),
        };
        try
        {
            Assert.Equal(1, await fixture.Run(
                "validate", "--scope", "project", "--project-id", ProjectId, "--input", inputPath));
            var evidence = fixture.Client!.Calls.Last();
            Assert.Equal("[REDACTED]", evidence["error"]!.GetValue<string>());
            Assert.DoesNotContain(Canary, evidence.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Theory]
    [InlineData("4", 4L)]
    [InlineData("none", null)]
    public async Task ApplySendsPlatformArtifactWithDurableIdsAndProjectsReceipt(string expectedText, long? expectedRevision)
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture();
        fixture.ApplyResponse = (attempt, body, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, ApplyReceipt(body, replayed: false)));
        fixture.BeforePolicyApply = body =>
        {
            var request = JsonNode.Parse(body)!.AsObject();
            var records = Directory.GetFiles(fixture.PolicyApplyStateDirectory, "*.json");
            Assert.Single(records);
            var saved = JsonNode.Parse(File.ReadAllText(records[0]))!.AsObject();
            Assert.Equal(request["idempotencyKey"]!.GetValue<string>(), saved["idempotencyKey"]!.GetValue<string>());
            Assert.Equal(request["correlationId"]!.GetValue<string>(), saved["correlationId"]!.GetValue<string>());
            Assert.DoesNotContain("Approve reviewed policy update", File.ReadAllText(records[0]), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, File.ReadAllText(records[0]), StringComparison.Ordinal);
        };
        try
        {
            Assert.Equal(0, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", expectedText, "--reason", "Approve reviewed policy update"));

            Assert.Equal("PUT", fixture.PolicyMethod);
            Assert.Equal("/api/v1/policies/platform", fixture.PolicyPath);
            var request = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            Assert.Equal(
                new[] { "artifact", "correlationId", "expectedRevision", "idempotencyKey", "reason" },
                request.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal(expectedRevision, request["expectedRevision"]?.GetValue<long>());
            Assert.Equal("Approve reviewed policy update", request["reason"]!.GetValue<string>());
            Assert.Matches("^[0-9a-f]{32}$", request["idempotencyKey"]!.GetValue<string>());
            Assert.Matches("^[0-9a-f]{32}$", request["correlationId"]!.GetValue<string>());
            Assert.Equal(
                new byte[] { 67, 65, 78, 65, 82, 89 },
                request["artifact"]!["manifestBytes"]!.AsArray()
                    .Select(value => value!.GetValue<byte>()).ToArray());

            var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.Equal(
                ["contentSha256", "policyId", "replayed", "revision", "scopeId", "scopeType"],
                output.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal("platform", output["scopeType"]!.GetValue<string>());
            Assert.Equal("default", output["scopeId"]!.GetValue<string>());
            Assert.Equal(expectedRevision is null ? 1L : expectedRevision.Value + 1, output["revision"]!.GetValue<long>());
            Assert.False(output["replayed"]!.GetValue<bool>());
            Assert.DoesNotContain(ArtifactCanary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["requestBody"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.Null(fixture.Authentication.Identity!.ProjectId);
            Assert.Null(fixture.Authentication.Identity.RunId);
            Assert.Equal(["openid", "api.read", "offline_access", "projects.admin"], fixture.Authentication.Identity.Scopes);
            Assert.Equal(InteractionPolicy.Never, fixture.Authentication.Interaction);
            Assert.Empty(Directory.GetFiles(fixture.PolicyApplyStateDirectory));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Theory]
    [InlineData("4", 4L, true)]
    [InlineData("none", null, false)]
    public async Task ApplyAcceptsPendingPreparationReceiptWithoutClaimingRevision(
        string expectedText, long? expectedRevision, bool includeGovernanceDecision)
    {
        var inputPath = NewInputPath();
        var inputJson = ExportInput(exact: false);
        var expectedHash = JsonNode.Parse(inputJson)!["contentSha256"]!.GetValue<string>();
        await File.WriteAllTextAsync(inputPath, inputJson);
        var fixture = new Fixture();
        fixture.ApplyResponse = (_, body, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK,
                PendingApplyReceipt(body, replayed: false, includeGovernanceDecision)));
        fixture.BeforePolicyApply = body =>
        {
            var request = JsonNode.Parse(body)!.AsObject();
            var records = Directory.GetFiles(fixture.PolicyApplyStateDirectory, "*.json");
            Assert.Single(records);
            var recordText = File.ReadAllText(records[0]);
            var saved = JsonNode.Parse(recordText)!.AsObject();
            Assert.Equal(request["idempotencyKey"]!.GetValue<string>(), saved["idempotencyKey"]!.GetValue<string>());
            Assert.Equal(request["correlationId"]!.GetValue<string>(), saved["correlationId"]!.GetValue<string>());
            Assert.DoesNotContain("Approve reviewed policy update", recordText, StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, recordText, StringComparison.Ordinal);
        };
        try
        {
            Assert.Equal(0, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", expectedText, "--reason", "Approve reviewed policy update"));

            var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.False(output.ContainsKey("revision"));
            Assert.Equal(
                [
                    "checkpoint", "createdAt", "expectedHeadContentSha256", "expectedHeadPolicyId",
                    "expectedHeadRevision", "governanceDecisionHash", "governanceDecisionId",
                    "policyId", "preparationId", "proposedContentSha256", "replayed",
                    "scopeId", "scopeType", "state",
                ],
                output.Select(pair => pair.Key).Order(StringComparer.Ordinal));
            Assert.Equal("platform", output["scopeType"]!.GetValue<string>());
            Assert.Equal("default", output["scopeId"]!.GetValue<string>());
            Assert.Equal("canonical", output["policyId"]!.GetValue<string>());
            Assert.Equal("pending", output["state"]!.GetValue<string>());
            Assert.Equal(expectedHash, output["proposedContentSha256"]!.GetValue<string>());
            Assert.Equal(Time, output["createdAt"]!.GetValue<string>());
            Assert.False(output["replayed"]!.GetValue<bool>());
            if (expectedRevision is null)
            {
                Assert.Null(output["expectedHeadPolicyId"]);
                Assert.Null(output["expectedHeadRevision"]);
                Assert.Null(output["expectedHeadContentSha256"]);
            }
            else
            {
                Assert.Equal("canonical", output["expectedHeadPolicyId"]!.GetValue<string>());
                Assert.Equal(expectedRevision.Value, output["expectedHeadRevision"]!.GetValue<long>());
                Assert.Equal(ArtifactHash(2), output["expectedHeadContentSha256"]!.GetValue<string>());
            }
            if (includeGovernanceDecision)
            {
                Assert.Equal("00000000-0000-0000-0000-000000000012", output["governanceDecisionId"]!.GetValue<string>());
                Assert.Equal(ArtifactHash(9), output["governanceDecisionHash"]!.GetValue<string>());
            }
            else
            {
                Assert.Null(output["governanceDecisionId"]);
                Assert.Null(output["governanceDecisionHash"]);
            }
            Assert.DoesNotContain(ArtifactCanary, fixture.Output.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["requestBody"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.Empty(Directory.GetFiles(fixture.PolicyApplyStateDirectory));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyReusesSavedIdsAfterResponseLossAndRecoversPendingReceipt()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture();
        fixture.ApplyResponse = (attempt, body, _) =>
        {
            if (attempt == 1)
                throw new HttpRequestException(Canary);
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, PendingApplyReceipt(body, replayed: true)));
        };
        try
        {
            var args = new[]
            {
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Recover pending receipt",
            };
            Assert.Equal(1, await fixture.Run(args));
            Assert.Contains("saved recovery record remains", fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
            var first = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            var firstKey = first["idempotencyKey"]!.GetValue<string>();
            var firstCorrelation = first["correlationId"]!.GetValue<string>();
            Assert.Single(Directory.GetFiles(fixture.PolicyApplyStateDirectory, "*.json"));

            Assert.Equal(0, await fixture.Run(args));
            var second = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            Assert.Equal(firstKey, second["idempotencyKey"]!.GetValue<string>());
            Assert.Equal(firstCorrelation, second["correlationId"]!.GetValue<string>());
            var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
            Assert.Equal("pending", output["state"]!.GetValue<string>());
            Assert.True(output["replayed"]!.GetValue<bool>());
            Assert.False(output.ContainsKey("revision"));
            Assert.Empty(Directory.GetFiles(fixture.PolicyApplyStateDirectory));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyRetainsJournalWhenOutputWriteFails()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture();
        fixture.ApplyResponse = (_, body, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, PendingApplyReceipt(body, replayed: false)));
        try
        {
            var runtime = new CliRuntimeOptions(
                GatewayClientFactory: fixture,
                Authentication: fixture.Authentication,
                Output: new FailingTextWriter(),
                Error: fixture.Error)
            {
                PolicyApplyStateDirectory = fixture.PolicyApplyStateDirectory,
            };
            Assert.Equal(1, await CliApplication.RunAsync(
                ["policy", "apply", "--scope", "platform", "--input", inputPath, "--expected-revision", "4", "--reason", "Approve", .. Target],
                runtime));
            Assert.Single(Directory.GetFiles(fixture.PolicyApplyStateDirectory, "*.json"));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Theory]
    [InlineData("none", "nonnull-expected-policy")]
    [InlineData("none", "nonnull-expected-revision")]
    [InlineData("none", "nonnull-expected-hash")]
    [InlineData("4", "null-expected-policy")]
    [InlineData("4", "mismatch-expected-policy")]
    [InlineData("4", "null-expected-revision")]
    [InlineData("4", "mismatch-expected-revision")]
    [InlineData("4", "null-expected-hash")]
    [InlineData("4", "uppercase-expected-hash")]
    [InlineData("4", "empty-preparation-id")]
    [InlineData("4", "partial-decision-id-only")]
    [InlineData("4", "partial-decision-hash-only")]
    [InlineData("4", "empty-decision-id")]
    [InlineData("4", "uppercase-decision-hash")]
    [InlineData("4", "wrong-state")]
    [InlineData("4", "hybrid-committed-revision")]
    [InlineData("4", "hybrid-committed-audit")]
    [InlineData("4", "committed-with-preparation-id")]
    public async Task ApplyRejectsMalformedOrHybridPendingReceiptsAndRetainsJournal(string expectedText, string mutation)
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture();
        fixture.ApplyResponse = (_, body, _) =>
        {
            var node = mutation == "committed-with-preparation-id"
                ? JsonNode.Parse(ApplyReceipt(body, replayed: false))!.AsObject()
                : JsonNode.Parse(PendingApplyReceipt(body, replayed: false))!.AsObject();
            switch (mutation)
            {
                case "nonnull-expected-policy":
                    node["expectedHeadPolicyId"] = "canonical";
                    break;
                case "nonnull-expected-revision":
                    node["expectedHeadRevision"] = 1;
                    break;
                case "nonnull-expected-hash":
                    node["expectedHeadContentSha256"] = ArtifactHash(2);
                    break;
                case "null-expected-policy":
                    node["expectedHeadPolicyId"] = null;
                    break;
                case "mismatch-expected-policy":
                    node["expectedHeadPolicyId"] = "other";
                    break;
                case "null-expected-revision":
                    node["expectedHeadRevision"] = null;
                    break;
                case "mismatch-expected-revision":
                    node["expectedHeadRevision"] = 3;
                    break;
                case "null-expected-hash":
                    node["expectedHeadContentSha256"] = null;
                    break;
                case "uppercase-expected-hash":
                    node["expectedHeadContentSha256"] = ArtifactHash(2).ToUpperInvariant();
                    break;
                case "empty-preparation-id":
                    node["preparationId"] = Guid.Empty.ToString("D");
                    break;
                case "partial-decision-id-only":
                    node["governanceDecisionHash"] = null;
                    break;
                case "partial-decision-hash-only":
                    node["governanceDecisionId"] = null;
                    break;
                case "empty-decision-id":
                    node["governanceDecisionId"] = Guid.Empty.ToString("D");
                    break;
                case "uppercase-decision-hash":
                    node["governanceDecisionHash"] = ArtifactHash(9).ToUpperInvariant();
                    break;
                case "wrong-state":
                    node["state"] = "committed";
                    break;
                case "hybrid-committed-revision":
                    node["revision"] = 5;
                    break;
                case "hybrid-committed-audit":
                    node["auditId"] = "00000000-0000-0000-0000-000000000001";
                    break;
                case "committed-with-preparation-id":
                    node["preparationId"] = "00000000-0000-0000-0000-000000000011";
                    break;
            }
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, node.ToJsonString()));
        };
        try
        {
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", expectedText, "--reason", "Validate malformed receipt"));
            Assert.Empty(fixture.Output.ToString());
            Assert.Contains("The Gateway returned an invalid policy response.", fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(fixture.PolicyApplyStateDirectory, "*.json"));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyReusesSavedIdsAfterResponseLossAndRecoversTheReceipt()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture();
        fixture.ApplyResponse = (attempt, body, _) =>
        {
            if (attempt == 1)
                throw new HttpRequestException("POLICY_PRIVATE_CANARY");
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, ApplyReceipt(body, replayed: true)));
        };
        try
        {
            var args = new[]
            {
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Recover response",
            };
            Assert.Equal(1, await fixture.Run(args));
            Assert.Contains("saved recovery record remains", fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
            var first = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            var firstKey = first["idempotencyKey"]!.GetValue<string>();
            var firstCorrelation = first["correlationId"]!.GetValue<string>();
            Assert.Single(Directory.GetFiles(fixture.PolicyApplyStateDirectory, "*.json"));

            Assert.Equal(0, await fixture.Run(args));
            var second = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            Assert.Equal(firstKey, second["idempotencyKey"]!.GetValue<string>());
            Assert.Equal(firstCorrelation, second["correlationId"]!.GetValue<string>());
            Assert.True(JsonNode.Parse(fixture.Output.ToString())!["replayed"]!.GetValue<bool>());
            Assert.Empty(Directory.GetFiles(fixture.PolicyApplyStateDirectory));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyKeepsRecoveryIdsAcrossDisabledGateDuringAnEarlierInFlightWrite()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ApplyResponse = async (attempt, body, ct) =>
        {
            if (attempt == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(ct);
                throw new HttpRequestException("response lost after commit");
            }
            if (attempt == 2)
            {
                await firstStarted.Task.WaitAsync(ct);
                return JsonResponse(HttpStatusCode.ServiceUnavailable,
                    """{"code":"native_acs_policy_writes_unavailable","detail":"POLICY_PRIVATE_CANARY"}""",
                    "application/problem+json");
            }
            return JsonResponse(HttpStatusCode.OK, ApplyReceipt(body, replayed: true));
        };
        try
        {
            var args = new[]
            {
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Recover in-flight write",
            };
            var firstTask = fixture.Run(args);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, await fixture.Run(args));
            Assert.Contains("native_acs_policy_writes_unavailable", fixture.Error.ToString(), StringComparison.Ordinal);
            var first = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            var firstKey = first["idempotencyKey"]!.GetValue<string>();
            Assert.Single(Directory.GetFiles(fixture.PolicyApplyStateDirectory, "*.json"));

            releaseFirst.TrySetResult();
            Assert.Equal(1, await firstTask);
            Assert.Single(Directory.GetFiles(fixture.PolicyApplyStateDirectory, "*.json"));

            Assert.Equal(0, await fixture.Run(args));
            var replay = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            Assert.Equal(firstKey, replay["idempotencyKey"]!.GetValue<string>());
            Assert.True(JsonNode.Parse(fixture.Output.ToString())!["replayed"]!.GetValue<bool>());
            Assert.Empty(Directory.GetFiles(fixture.PolicyApplyStateDirectory));
        }
        finally
        {
            releaseFirst.TrySetResult();
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyAllowsCorrectedExpectedRevisionWithItsOwnKey()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture { Status = HttpStatusCode.Conflict, Body = """{"code":"conflict"}""" };
        var keys = new List<string>();
        fixture.ApplyResponse = (attempt, body, _) =>
        {
            keys.Add(JsonNode.Parse(body)!["idempotencyKey"]!.GetValue<string>());
            return Task.FromResult(attempt == 1
                ? JsonResponse(HttpStatusCode.Conflict, """{"code":"conflict"}""", "application/problem+json")
                : JsonResponse(HttpStatusCode.OK, ApplyReceipt(body, replayed: false)));
        };
        try
        {
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "First attempt"));
            Assert.Equal(0, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "5", "--reason", "Corrected revision"));
            Assert.Equal(2, keys.Count);
            Assert.NotEqual(keys[0], keys[1]);
            Assert.Equal(6, JsonNode.Parse(fixture.Output.ToString())!["revision"]!.GetValue<long>());
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyUsesANewKeyForACorrectedArtifactAfterAnUncertainResponse()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture();
        fixture.ApplyResponse = (attempt, body, _) =>
        {
            if (attempt == 1)
                throw new HttpRequestException("response lost");
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, ApplyReceipt(body, replayed: false)));
        };
        try
        {
            var args = new[]
            {
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Corrected artifact",
            };
            Assert.Equal(1, await fixture.Run(args));
            var first = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            var firstKey = first["idempotencyKey"]!.GetValue<string>();

            var corrected = JsonNode.Parse(ExportInput(exact: false))!.AsObject();
            corrected["artifact"]!["manifestBytes"]![0] = 68;
            RehashExport(corrected);
            await File.WriteAllTextAsync(inputPath, corrected.ToJsonString());

            Assert.Equal(0, await fixture.Run(args));
            var second = JsonNode.Parse(fixture.PolicyBody!)!.AsObject();
            Assert.NotEqual(firstKey, second["idempotencyKey"]!.GetValue<string>());
            Assert.Equal(second["artifact"]!["contentSha256"]!.GetValue<string>(),
                JsonNode.Parse(fixture.Output.ToString())!["contentSha256"]!.GetValue<string>());
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyReportsDisabledGateWithoutLeakingBodyAndKeepsItsKey()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture
        {
            Status = HttpStatusCode.ServiceUnavailable,
            Body = $$"""{"code":"native_acs_policy_writes_unavailable","detail":"{{Canary}}","manifestBytes":[67,65,78,65,82,89]}""",
            MediaType = "application/problem+json",
        };
        try
        {
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Disabled gate"));
            Assert.Contains("Platform Policy writes are disabled", fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.Contains("native_acs_policy_writes_unavailable", fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["requestBody"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.DoesNotContain(Canary, fixture.Client.Calls.Last().ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, fixture.Client.Calls.Last().ToJsonString(), StringComparison.Ordinal);
            Assert.Single(Directory.GetFiles(fixture.PolicyApplyStateDirectory, "*.json"));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyDoesNotSendWhenItCannotSaveTheRecoveryRecord()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture();
        var statePath = fixture.PolicyApplyStateDirectory;
        await File.WriteAllTextAsync(statePath, "not a directory");
        try
        {
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Recovery record failure"));
            Assert.Contains("recovery record is unavailable", fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, fixture.ApplyRequestCount);
            Assert.False(Directory.Exists(statePath));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (File.Exists(statePath))
                File.Delete(statePath);
            fixture.CleanupPolicyState();
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("9223372036854775808")]
    public async Task ApplyRejectsInvalidExpectedRevisionBeforeNetwork(string expectedRevision)
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture();
        try
        {
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", expectedRevision, "--reason", "Invalid revision"));
            Assert.Equal(0, fixture.RequestCount);
            Assert.False(Directory.Exists(fixture.PolicyApplyStateDirectory));
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyRequiresReasonAndPlatformInputBeforeNetwork()
    {
        var inputPath = NewInputPath();
        var export = JsonNode.Parse(ExportInput(exact: false))!.AsObject();
        export["scopeType"] = "project";
        await File.WriteAllTextAsync(inputPath, export.ToJsonString());
        var fixture = new Fixture();
        try
        {
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath, "--expected-revision", "4"));
            Assert.Equal(0, fixture.RequestCount);
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "   "));
            Assert.Equal(0, fixture.RequestCount);
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Valid reason"));
            Assert.Equal(0, fixture.RequestCount);
            Assert.Contains("invalid or could not be read", fixture.Error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ApplyRejectsUnsafeOperationBoundsAndWrongPath()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture { UnsafePolicyApplyRequestBound = true };
        try
        {
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Unsafe bound"));
            Assert.Null(fixture.PolicyPath);
            fixture = new Fixture { UnsafePolicyApplyResponseBound = true };
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Unsafe response bound"));
            Assert.Null(fixture.PolicyPath);
            fixture = new Fixture { WrongPolicyApplyPath = true };
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Wrong path"));
            Assert.Null(fixture.PolicyPath);
            fixture = new Fixture { WrongPolicyApplyMethod = true };
            Assert.Equal(1, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Wrong method"));
            Assert.Null(fixture.PolicyPath);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Fact]
    public async Task ValidateDispatchesAgainstThePublishedGatewayContract()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            Body = """{"isValid":true,"diagnostics":[]}""",
        };
        try
        {
            Assert.Equal(0, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            Assert.Equal("POST", fixture.PolicyMethod);
            Assert.Equal("/api/v1/policies/platform/validate", fixture.PolicyPath);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ApplyDispatchesAgainstThePublishedGatewayContract()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture
        {
            UseGatewayContract = true,
        };
        fixture.ApplyResponse = (attempt, body, _) =>
            Task.FromResult(JsonResponse(HttpStatusCode.OK, ApplyReceipt(body, replayed: false)));
        try
        {
            Assert.Equal(0, await fixture.Run(
                "apply", "--scope", "platform", "--input", inputPath,
                "--expected-revision", "4", "--reason", "Published contract"));
            Assert.Equal("PUT", fixture.PolicyMethod);
            Assert.Equal("/api/v1/policies/platform", fixture.PolicyPath);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            fixture.CleanupPolicyState();
        }
    }

    [Theory]
    [InlineData(403, "native_acs_dependency_unavailable", false)]
    [InlineData(429, "native_acs_validation_rate_limited", true)]
    [InlineData(503, "native_acs_dependency_unavailable", true)]
    public async Task ValidateReportsSafeServerFailures(int status, string code, bool includeCode)
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var fixture = new Fixture
        {
            Status = (HttpStatusCode)status,
            Body = $$"""{"code":"{{code}}","detail":"{{Canary}}","manifestBytes":[67,65,78,65,82,89]}""",
        };
        try
        {
            Assert.Equal(1, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
            Assert.Empty(fixture.Output.ToString());
            Assert.Contains(status.ToString(), fixture.Error.ToString(), StringComparison.Ordinal);
            if (includeCode)
                Assert.Contains(code, fixture.Error.ToString(), StringComparison.Ordinal);
            else
                Assert.DoesNotContain(code, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["requestBody"]!.GetValue<string>());
            Assert.Equal("[REDACTED]", fixture.Client.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.DoesNotContain(Canary, fixture.Client.Calls.Last().ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ArtifactCanary, fixture.Client.Calls.Last().ToJsonString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ValidateRejectsMalformedSuccessAndInvalidDiagnostics()
    {
        var inputPath = NewInputPath();
        await File.WriteAllTextAsync(inputPath, ExportInput(exact: false));
        var responses = new[]
        {
            (HttpStatusCode.OK, """{"isValid":false,"diagnostics":[],"private":"POLICY_PRIVATE_CANARY"}"""),
            (HttpStatusCode.UnprocessableEntity,
                """{"code":"native_acs_policy_invalid","diagnostics":[{"code":"bad","message":"POLICY_PRIVATE_CANARY","line":"not-an-integer"}]}"""),
        };
        try
        {
            foreach (var (status, body) in responses)
            {
                var fixture = new Fixture { Status = status, Body = body };
                Assert.Equal(1, await fixture.Run("validate", "--scope", "platform", "--input", inputPath));
                Assert.Empty(fixture.Output.ToString());
                Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(ArtifactCanary, fixture.Client!.Calls.Last().ToJsonString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
        }
    }

    [Fact]
    public async Task ExportRequiresANewOutputFileBeforeNetworkAccess()
    {
        var fixture = new Fixture();
        Assert.Equal(1, await fixture.Run("export", "--scope", "platform"));
        Assert.Equal(0, fixture.RequestCount);

        var outputPath = NewOutputPath();
        try
        {
            await File.WriteAllTextAsync(outputPath, "keep");
            fixture = new Fixture();
            Assert.Equal(1, await fixture.Run("export", "--scope", "platform", "--output", outputPath));
            Assert.Equal(0, fixture.RequestCount);
            Assert.Equal("keep", await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ExportDispatchesAgainstThePublishedGatewayContract()
    {
        var outputPath = NewOutputPath();
        var fixture = new Fixture { UseGatewayContract = true };
        try
        {
            Assert.Equal(0, await fixture.Run("export", "--scope", "platform", "--output", outputPath));
            Assert.Equal("/api/v1/policies/platform", fixture.PolicyPath);
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task ExportRejectsWrongRevisionBeforeWritingAFile()
    {
        var outputPath = NewOutputPath();
        var fixture = new Fixture { Body = ShowBody(exact: true, revision: 3) };
        try
        {
            Assert.Equal(1, await fixture.Run(
                "export", "--scope", "platform", "--revision", "2", "--output", outputPath));
            Assert.False(File.Exists(outputPath));
            Assert.Empty(fixture.Output.ToString());
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task HistoryReturnsOnePageAndForwardsTheCursor()
    {
        var fixture = new Fixture
        {
            Body = $$"""{"scopeType":"platform","scopeId":"default","policyId":"canonical","snapshotRevision":3,"items":[{"revision":2,"contentSha256":"{{Hash}}","createdAt":"{{Time}}","private":"{{Canary}}"}],"nextCursor":"next_2","private":"{{Canary}}"}""",
        };
        Assert.Equal(0, await fixture.Run("history", "--scope", "platform", "--page-size", "2", "--cursor", "prior_1"));
        Assert.Equal("?cursor=prior_1&pageSize=2", fixture.PolicyQuery);
        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal(["items", "nextCursor", "policyId", "scopeId", "scopeType", "snapshotRevision"],
            output.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        var row = output["items"]!.AsArray()[0]!.AsObject();
        Assert.Equal(["contentSha256", "createdAt", "revision"], row.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(Hash, row["contentSha256"]!.GetValue<string>());
        Assert.Equal("next_2", output["nextCursor"]!.GetValue<string>());
        Assert.DoesNotContain(Canary, fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("show", "2026-11-16T00:00:00.1234567+05:30")]
    [InlineData("exact", "2026-11-16T00:00:00.1234567+05:30")]
    [InlineData("history", "2026-11-16T00:00:00.1234567+05:30")]
    [InlineData("show", "2026-11-16T00:00:00.1234567Z")]
    [InlineData("exact", "2026-11-16T00:00:00.1234567Z")]
    [InlineData("history", "2026-11-16T00:00:00.1234567Z")]
    public async Task AcceptsIso8601TimestampsWithExplicitOffsets(string command, string timestamp)
    {
        var fixture = new Fixture { Body = PolicyBody(command, timestamp) };
        Assert.Equal(0, await fixture.Run(Args(command)));
        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        var actual = command == "history"
            ? output["items"]![0]!["createdAt"]
            : output[command == "exact" ? "createdAt" : "updatedAt"];
        Assert.Equal(timestamp, actual!.GetValue<string>());
    }

    [Theory]
    [InlineData("show", "00:00:00")]
    [InlineData("exact", "00:00:00")]
    [InlineData("history", "00:00:00")]
    [InlineData("show", "2026-11-16T00:00:00")]
    [InlineData("exact", "2026-11-16T00:00:00")]
    [InlineData("history", "2026-11-16T00:00:00")]
    [InlineData("show", "2026-11-16T00:00:00+25:00")]
    [InlineData("exact", "2026-11-16T00:00:00+25:00")]
    [InlineData("history", "2026-11-16T00:00:00+25:00")]
    public async Task RejectsTimestampsWithoutAValidExplicitOffset(string command, string timestamp)
    {
        var fixture = new Fixture { Body = PolicyBody(command, timestamp) };
        Assert.Equal(1, await fixture.Run(Args(command)));
        Assert.Empty(fixture.Output.ToString());
    }

    [Theory]
    [InlineData("show")]
    [InlineData("exact")]
    [InlineData("history")]
    public async Task RejectsOversizedPaddedTimestamps(string command)
    {
        var fixture = new Fixture { Body = PolicyBody(command, new string(' ', 100_000) + Time) };
        Assert.Equal(1, await fixture.Run(Args(command)));
        Assert.Empty(fixture.Output.ToString());
    }

    [Theory]
    [InlineData("show")]
    [InlineData("exact")]
    [InlineData("history")]
    public async Task CommandsDispatchAgainstTheActualGatewayOpenApi(string command)
    {
        var fixture = new Fixture { UseGatewayContract = true };
        fixture.Body = command switch
        {
            "exact" => ShowBody(true),
            "history" => """{"scopeType":"platform","scopeId":"default","policyId":null,"snapshotRevision":null,"items":[],"nextCursor":null}""",
            _ => ShowBody(false),
        };
        Assert.Equal(0, await fixture.Run(Args(command)));
        Assert.NotNull(fixture.PolicyPath);
    }

    [Fact]
    public async Task EmptyHistoryIsSuccessAndMissingHeadIsNot()
    {
        var fixture = new Fixture
        {
            Body = """{"scopeType":"platform","scopeId":"default","policyId":null,"snapshotRevision":null,"items":[],"nextCursor":null}""",
        };
        Assert.Equal(0, await fixture.Run("history", "--scope", "platform"));
        var page = JsonNode.Parse(fixture.Output.ToString())!;
        Assert.Null(page["policyId"]);
        Assert.Empty(page["items"]!.AsArray());
        fixture = new Fixture { Status = HttpStatusCode.NotFound, Body = """{"code":"native_acs_policy_not_found"}""" };
        Assert.Equal(1, await fixture.Run("show", "--scope", "platform"));
        Assert.Contains("native_acs_policy_not_found", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("", fixture.Output.ToString());
    }

    [Theory]
    [InlineData("--revision", "0")]
    [InlineData("--revision", "01")]
    [InlineData("--revision", "+1")]
    [InlineData("--revision", "9223372036854775808")]
    [InlineData("--page-size", "+1")]
    [InlineData("--page-size", " 1")]
    [InlineData("--page-size", "1 ")]
    [InlineData("--page-size", "101")]
    [InlineData("--cursor", "bad cursor")]
    [InlineData("--project-id", "project")]
    [InlineData("--run-id", "run")]
    public async Task InvalidInputFailsBeforeNetwork(string option, string value)
    {
        var fixture = new Fixture();
        var command = option is "--revision" or "--project-id" or "--run-id" ? "show" : "history";
        Assert.Equal(1, await fixture.Run(command, "--scope", "platform", option, value));
        Assert.Equal(0, fixture.RequestCount);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("history")]
    public async Task RepeatedOrWrongScopeFailsBeforeNetwork(string command)
    {
        var fixture = new Fixture();
        Assert.Equal(1, await fixture.Run(command, "--scope", "platform", "--scope", "platform"));
        Assert.Equal(0, fixture.RequestCount);
        fixture = new Fixture();
        Assert.Equal(1, await fixture.Run(command, "--scope", "project"));
        Assert.Equal(0, fixture.RequestCount);
    }

    [Theory]
    [InlineData("show", "readPlatformNativeAcsPolicy")]
    [InlineData("exact", "readPlatformNativeAcsPolicyRevision")]
    [InlineData("history", "listPlatformNativeAcsPolicyRevisions")]
    public async Task RejectsOperationWithUnsafeResponseBound(string command, string operation)
    {
        var fixture = new Fixture { UnsafeOperation = operation };
        Assert.Equal(1, await fixture.Run(Args(command)));
        Assert.Null(fixture.PolicyPath);
    }

    [Fact]
    public async Task RejectsWrongOperationPathAndWrongRevision()
    {
        var fixture = new Fixture { WrongPath = true };
        Assert.Equal(1, await fixture.Run("show", "--scope", "platform"));
        Assert.Null(fixture.PolicyPath);
        fixture = new Fixture { Body = ShowBody(exact: true, revision: 3) };
        Assert.Equal(1, await fixture.Run("show", "--scope", "platform", "--revision", "2"));
        Assert.Equal("", fixture.Output.ToString());
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task ServerErrorsAreBoundedAndDoNotLeak(int status)
    {
        var fixture = new Fixture { Status = (HttpStatusCode)status, Body = $$"""{"code":"{{Canary}}","detail":"{{Canary}}"}""" };
        Assert.Equal(1, await fixture.Run("show", "--scope", "platform"));
        Assert.Contains(status.ToString(), fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, fixture.Client!.Calls.Last().ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("history", 403, "native_acs_policy_not_found")]
    [InlineData("history", 503, "native_acs_policy_source_unavailable")]
    [InlineData("exact", 500, "native_acs_policy_corrupt")]
    public async Task HistoryAndExactReadPropagateSafeFailures(string command, int status, string code)
    {
        var fixture = new Fixture { Status = (HttpStatusCode)status, Body = $$"""{"code":"{{code}}","detail":"{{Canary}}"}""" };
        Assert.Equal(1, await fixture.Run(Args(command)));
        Assert.Contains(status.ToString(), fixture.Error.ToString(), StringComparison.Ordinal);
        if (status != 403)
            Assert.Contains(code, fixture.Error.ToString(), StringComparison.Ordinal);
        else
            Assert.DoesNotContain(code, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Empty(fixture.Output.ToString());
    }

    [Fact]
    public async Task InheritedRunBindingDoesNotChangeThePolicyIdentity()
    {
        var previousProject = Environment.GetEnvironmentVariable("AGENTWEAVER_PROJECT_ID");
        var previousRun = Environment.GetEnvironmentVariable("AGENTWEAVER_RUN_ID");
        try
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", "project-1");
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", "run-1");
            var fixture = new Fixture();
            Assert.Equal(0, await fixture.Run("show", "--scope", "platform"));
            Assert.Null(fixture.Authentication.Identity!.ProjectId);
            Assert.Null(fixture.Authentication.Identity.RunId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_PROJECT_ID", previousProject);
            Environment.SetEnvironmentVariable("AGENTWEAVER_RUN_ID", previousRun);
        }
    }

    [Fact]
    public async Task MalformedHistoryAndOversizedArtifactFailClosed()
    {
        var missingField = new Fixture
        {
            Body = """{"scopeType":"platform","scopeId":"default","policyId":null,"snapshotRevision":null,"items":[]}""",
        };
        Assert.Equal(1, await missingField.Run("history", "--scope", "platform"));
        Assert.Empty(missingField.Output.ToString());
        var fixture = new Fixture
        {
            Body = $$"""{"scopeType":"platform","scopeId":"default","policyId":"canonical","snapshotRevision":1,"items":[{"revision":2,"contentSha256":"{{Hash}}","createdAt":"{{Time}}"}],"nextCursor":null}""",
        };
        Assert.Equal(1, await fixture.Run("history", "--scope", "platform"));
        Assert.Empty(fixture.Output.ToString());

        fixture = new Fixture { Body = new string('a', 5 * 1024 * 1024 + 1), MediaType = "text/plain" };
        Assert.Equal(1, await fixture.Run("show", "--scope", "platform"));
        Assert.Empty(fixture.Output.ToString());
        Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, 200, "application/json")]
    [InlineData(true, 200, "application/json")]
    [InlineData(false, 200, "text/plain")]
    [InlineData(true, 200, "text/plain")]
    [InlineData(false, 403, "application/problem+json")]
    [InlineData(true, 403, "application/problem+json")]
    [InlineData(false, 500, "text/plain")]
    [InlineData(true, 500, "text/plain")]
    public async Task ArtifactResponsesNeverAppearInEvidence(bool exact, int status, string mediaType)
    {
        var fixture = new Fixture
        {
            Status = (HttpStatusCode)status,
            MediaType = mediaType,
            Body = mediaType == "application/json" ? ShowBody(exact) :
                mediaType == "text/plain" ? Canary : $$"""{"detail":"{{Canary}}","bytes":[80,79,76,73,67,89]}""",
        };
        await fixture.Run(Args(exact ? "exact" : "show"));
        var evidence = fixture.Client!.Calls.Last().ToJsonString();
        Assert.Contains("[REDACTED]", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("80,79,76,73,67,89", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LargeArtifactRemainsBoundedAndIsNotPrinted()
    {
        var fixture = new Fixture { Body = ShowBody(false, bytes: 800_000) };
        Assert.Equal(0, await fixture.Run("show", "--scope", "platform"));
        Assert.True(fixture.Body.Length > 1_000_000);
        Assert.True(fixture.Output.ToString().Length < 1000);
        Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
    }

    [Fact]
    public async Task LargeExportWritesArtifactToFileWithoutPrintingItsBytes()
    {
        var outputPath = NewOutputPath();
        var fixture = new Fixture { Body = ShowBody(false, bytes: 400_000) };
        try
        {
            Assert.Equal(0, await fixture.Run("export", "--scope", "platform", "--output", outputPath));
            Assert.True(fixture.Body.Length > 1_000_000);
            Assert.True(new FileInfo(outputPath).Length > 1_000_000);
            Assert.True(fixture.Output.ToString().Length < 1000);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private static string[] Args(string command) => command switch
    {
        "exact" => ["show", "--scope", "platform", "--revision", "2"],
        _ => [command, "--scope", "platform"],
    };

    private static string PolicyBody(string command, string timestamp) => command switch
    {
        "exact" => ShowBody(exact: true, timestamp: timestamp),
        "history" => $$"""{"scopeType":"platform","scopeId":"default","policyId":"canonical","snapshotRevision":3,"items":[{"revision":2,"contentSha256":"{{Hash}}","createdAt":"{{timestamp}}"}],"nextCursor":null}""",
        _ => ShowBody(exact: false, timestamp: timestamp),
    };

    private static string ShowBody(bool exact, int revision = 2, int bytes = 3, string timestamp = Time)
    {
        var hash = ArtifactHash(bytes);
        return $$"""{"scopeType":"platform","scopeId":"default","policyId":"canonical","revision":{{revision}},"contentSha256":"{{hash}}","{{(exact ? "createdAt" : "updatedAt")}}":"{{timestamp}}","artifact":{"manifestBytes":[{{string.Join(',', Enumerable.Repeat(65, bytes))}}],"files":[{"relativePath":"modules/network.rego","bytes":[80,79,76,73,67,89]},{"relativePath":"data/policy.json","bytes":[123,34,101,110,97,98,108,101,100,34,58,116,114,117,101,125]}],"contentSha256":"{{hash}}"},"private":"{{Canary}}"}""";
    }

    private static string ExportInput(bool exact)
    {
        var export = JsonNode.Parse(ShowBody(exact))!.AsObject();
        export.Remove("private");
        export["artifact"]!["manifestBytes"] = new JsonArray(67, 65, 78, 65, 82, 89);
        RehashExport(export);
        return export.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) +
            Environment.NewLine;
    }

    private static string ProjectExportInput(bool exact)
    {
        var export = JsonNode.Parse(ExportInput(exact))!.AsObject();
        export["scopeType"] = "project";
        export["scopeId"] = ProjectId;
        return export.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) +
            Environment.NewLine;
    }

    private static string ShowBodyWithCanary(bool exact)
    {
        var body = JsonNode.Parse(ShowBody(exact))!.AsObject();
        body["artifact"]!["manifestBytes"] = new JsonArray(67, 65, 78, 65, 82, 89);
        RehashExport(body);
        return body.ToJsonString();
    }

    private static string ProjectBody(bool exact)
    {
        var body = JsonNode.Parse(ShowBody(exact))!.AsObject();
        body["scopeType"] = "project";
        body["scopeId"] = ProjectId;
        return body.ToJsonString();
    }

    private static string ProjectBodyWithCanary(bool exact)
    {
        var body = JsonNode.Parse(ShowBodyWithCanary(exact))!.AsObject();
        body["scopeType"] = "project";
        body["scopeId"] = ProjectId;
        return body.ToJsonString();
    }

    private static string ProjectHistoryBody() =>
        $$"""{"scopeType":"project","scopeId":"{{ProjectId}}","policyId":"canonical","snapshotRevision":3,"items":[{"revision":2,"contentSha256":"{{Hash}}","createdAt":"{{Time}}"}],"nextCursor":"NEXT"}""";

    private static string ProjectListBody(string? nextCursor)
    {
        static JsonObject Summary(string scopeId) =>
            new()
            {
                ["scopeType"] = "project",
                ["scopeId"] = scopeId,
                ["policyId"] = "canonical",
                ["revision"] = 2,
                ["contentSha256"] = Hash,
                ["updatedAt"] = Time,
            };

        return new JsonObject
        {
            ["items"] = new JsonArray(Summary(ProjectId), Summary("00000000000000000000000000000043")),
            ["nextCursor"] = nextCursor,
        }.ToJsonString();
    }

    private static string ExportInputWithRequestSize(int requestBytes)
    {
        var export = JsonNode.Parse(ExportInput(exact: false))!.AsObject();
        var artifact = export["artifact"]!.AsObject();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = new JsonObject { ["artifact"] = artifact.DeepClone() };
        var currentSize = Encoding.UTF8.GetByteCount(request.ToJsonString(options));
        var currentPath = artifact["files"]![0]!["relativePath"]!.GetValue<string>();
        artifact["files"]![0]!["relativePath"] = new string('x', currentPath.Length + requestBytes - currentSize);
        RehashExport(export);
        request = new JsonObject { ["artifact"] = artifact.DeepClone() };
        Assert.Equal(requestBytes, Encoding.UTF8.GetByteCount(request.ToJsonString(options)));
        return export.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) +
            Environment.NewLine;
    }

    private static string ProjectExportInputWithRequestSize(int requestBytes)
    {
        var export = JsonNode.Parse(ExportInputWithRequestSize(requestBytes))!.AsObject();
        export["scopeType"] = "project";
        export["scopeId"] = ProjectId;
        return export.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }) +
            Environment.NewLine;
    }

    private static void RehashExport(JsonObject export)
    {
        var artifact = JsonSerializer.Deserialize<NativeAcsArtifact>(
            export["artifact"]!.ToJsonString(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var hash = NativeAcsArtifactHash.Compute(artifact);
        export["contentSha256"] = hash;
        export["artifact"]!["contentSha256"] = hash;
    }

    private static string ArtifactHash(int manifestBytes)
    {
        var artifact = new NativeAcsArtifact(
            ImmutableArray.CreateRange(Enumerable.Repeat((byte)65, manifestBytes)),
            ImmutableArray.Create(
                new NativeAcsArtifactFile(
                    "modules/network.rego",
                    ImmutableArray.Create<byte>(80, 79, 76, 73, 67, 89)),
                new NativeAcsArtifactFile(
                    "data/policy.json",
                    ImmutableArray.Create<byte>(123, 34, 101, 110, 97, 98, 108, 101, 100, 34, 58, 116, 114, 117, 101, 125))),
            string.Empty);
        return NativeAcsArtifactHash.Compute(artifact);
    }

    private static string NewOutputPath() =>
        Path.Combine(AppContext.BaseDirectory, $"policy-export-{Guid.NewGuid():N}.json");

    private static string NewInputPath() =>
        Path.Combine(AppContext.BaseDirectory, $"policy-input-{Guid.NewGuid():N}.json");

    private static string PendingApplyReceipt(string requestBody, bool replayed, bool includeGovernanceDecision = true)
    {
        var request = JsonNode.Parse(requestBody)!.AsObject();
        var expectedRevision = request["expectedRevision"]?.GetValue<long>();
        var hash = request["artifact"]!["contentSha256"]!.GetValue<string>();
        var receipt = new JsonObject
        {
            ["preparationId"] = "00000000-0000-0000-0000-000000000011",
            ["scopeType"] = "platform",
            ["scopeId"] = "default",
            ["policyId"] = "canonical",
            ["expectedHeadPolicyId"] = expectedRevision is null ? null : "canonical",
            ["expectedHeadRevision"] = expectedRevision,
            ["expectedHeadContentSha256"] = expectedRevision is null ? null : ArtifactHash(2),
            ["proposedContentSha256"] = hash,
            ["state"] = "pending",
            ["checkpoint"] = "prepared",
            ["createdAt"] = Time,
            ["replayed"] = replayed,
        };
        if (includeGovernanceDecision)
        {
            receipt["governanceDecisionId"] = "00000000-0000-0000-0000-000000000012";
            receipt["governanceDecisionHash"] = ArtifactHash(9);
        }
        return receipt.ToJsonString();
    }

    [Fact]
    public async Task DecisionsProjectsAllowlistedFieldsPreservesPendingAndNullRevisionAndRedactsIdentity()
    {
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            Body = DecisionHistoryBody(
                resourceId: "canonical",
                nextBeforeSequence: 5,
                items:
                [
                    DecisionHistoryItemJson(
                        sequence: 6,
                        outcome: "allow",
                        approvalState: "approved",
                        policyRevision: 4,
                        includeSensitive: true),
                    DecisionHistoryItemJson(
                        sequence: 5,
                        outcome: "pending",
                        approvalState: "pending",
                        policyRevision: null,
                        includeSensitive: true),
                ]),
        };

        Assert.Equal(0, await fixture.Run("decisions", "--scope", "platform", "--page-size", "2"));

        var stdout = fixture.Output.ToString();
        var output = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("platform", output["scopeType"]!.GetValue<string>());
        Assert.Equal("default", output["scopeId"]!.GetValue<string>());
        Assert.Equal("platform_policy", output["resourceType"]!.GetValue<string>());
        Assert.Equal("canonical", output["resourceId"]!.GetValue<string>());
        Assert.Equal(5, output["nextBeforeSequence"]!.GetValue<long>());

        var items = output["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal(6, items[0]!["sequence"]!.GetValue<long>());
        Assert.Equal(4, items[0]!["decision"]!["policyRevision"]!.GetValue<long>());
        Assert.Equal("allow", items[0]!["decision"]!["outcome"]!.GetValue<string>());
        Assert.Equal(5, items[1]!["sequence"]!.GetValue<long>());
        Assert.Equal("pending", items[1]!["decision"]!["outcome"]!.GetValue<string>());
        Assert.Equal("pending", items[1]!["decision"]!["approvalState"]!.GetValue<string>());
        Assert.Null(items[1]!["decision"]!["policyRevision"]);

        foreach (var forbidden in new[]
        {
            "brokerIssuer", "brokerSubject", "humanEntraOid", "tenantId",
            "effectiveRoleRevisions", "assignmentId", Canary, "https://sensitive.example/",
        })
        {
            Assert.DoesNotContain(forbidden, stdout, StringComparison.Ordinal);
        }

        Assert.Equal("/api/v1/policies/platform/decisions", fixture.PolicyPath);
        Assert.Contains("resourceType=platform_policy", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Contains("resourceId=canonical", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Contains("pageSize=2", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
        Assert.Null(fixture.Authentication.Identity!.ProjectId);
        Assert.Null(fixture.Authentication.Identity.RunId);
        Assert.Equal(["openid", "api.read", "offline_access"], fixture.Authentication.Identity.Scopes);
    }

    [Fact]
    public async Task DecisionsSupportsCustomPolicyIdLargeBeforeSequenceAndEmptyHistory()
    {
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            Body = DecisionHistoryBody(resourceId: "custom.policy-1", nextBeforeSequence: null, items: []),
        };
        var maxSequence = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, await fixture.Run(
            "decisions", "--scope", "platform", "--policy-id", "custom.policy-1",
            "--page-size", "25", "--before-sequence", maxSequence));

        var output = JsonNode.Parse(fixture.Output.ToString())!.AsObject();
        Assert.Equal("custom.policy-1", output["resourceId"]!.GetValue<string>());
        Assert.Empty(output["items"]!.AsArray());
        Assert.Null(output["nextBeforeSequence"]);
        Assert.Contains("resourceType=platform_policy", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Contains("resourceId=custom.policy-1", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Contains("pageSize=25", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Contains($"beforeSequence={maxSequence}", fixture.PolicyQuery, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "native_acs_forbidden", "denied (HTTP 403)")]
    [InlineData(HttpStatusCode.BadRequest, "governance_decision_history_request_invalid", "HTTP 400, governance_decision_history_request_invalid")]
    [InlineData(HttpStatusCode.InternalServerError, "governance_decision_corrupt", "HTTP 500, governance_decision_corrupt")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "governance_decision_source_unavailable", "HTTP 503, governance_decision_source_unavailable")]
    public async Task DecisionsReportsControlledRedactedErrors(
        HttpStatusCode status, string code, string expectedMessage)
    {
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            Status = status,
            MediaType = "application/problem+json",
            Body = $$"""{"code":"{{code}}","detail":"{{Canary}}","brokerSubject":"{{Canary}}"}""",
        };

        Assert.Equal(1, await fixture.Run("decisions", "--scope", "platform"));
        Assert.Contains(expectedMessage, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("wrongScope")]
    [InlineData("wrongResourceType")]
    [InlineData("wrongResourceId")]
    [InlineData("outOfOrder")]
    [InlineData("sequenceAtCursor")]
    [InlineData("missingNextCursor")]
    [InlineData("cursorOnShortPage")]
    [InlineData("cursorOne")]
    [InlineData("nonScalarSelectedRule")]
    [InlineData("nonCanonicalDecisionId")]
    [InlineData("invalidOutcome")]
    public async Task DecisionsFailsClosedOnWrongStreamOrMalformedData(string mode)
    {
        var body = mode switch
        {
            "wrongScope" => DecisionHistoryBody("canonical", null, [], scopeType: "project"),
            "wrongResourceType" => DecisionHistoryBody("canonical", null, [], resourceType: "project_policy"),
            "wrongResourceId" => DecisionHistoryBody("other", null, []),
            "outOfOrder" => DecisionHistoryBody("canonical", null,
                [DecisionHistoryItemJson(4), DecisionHistoryItemJson(5)]),
            "sequenceAtCursor" => DecisionHistoryBody("canonical", null,
                [DecisionHistoryItemJson(10)]),
            "missingNextCursor" => """{"scopeType":"platform","scopeId":"default","resourceType":"platform_policy","resourceId":"canonical","items":[]}""",
            "cursorOnShortPage" => DecisionHistoryBody("canonical", nextBeforeSequence: 5,
                items: [DecisionHistoryItemJson(5)]),
            "cursorOne" => DecisionHistoryBody("canonical", nextBeforeSequence: 1,
                items: [DecisionHistoryItemJson(2), DecisionHistoryItemJson(1)]),
            "nonScalarSelectedRule" => DecisionHistoryBody("canonical", null,
                [DecisionHistoryItemJson(5, selectedRuleJson: """{"brokerIssuer":"leak"}""")]),
            "nonCanonicalDecisionId" => DecisionHistoryBody("canonical", null,
                [DecisionHistoryItemJson(5).Replace("00000000-0000-0000-0000-000000000005", " 00000000-0000-0000-0000-000000000005 ", StringComparison.Ordinal)]),
            _ => DecisionHistoryBody("canonical", null,
                [DecisionHistoryItemJson(5, outcome: "unknown")]),
        };
        var args = mode == "cursorOnShortPage"
            ? new[] { "decisions", "--scope", "platform", "--page-size", "2" }
            : mode == "cursorOne"
                ? new[] { "decisions", "--scope", "platform", "--page-size", "2" }
                : mode == "sequenceAtCursor"
                    ? new[] { "decisions", "--scope", "platform", "--before-sequence", "10" }
                    : new[] { "decisions", "--scope", "platform" };
        var fixture = new Fixture { UseGatewayContract = true, Body = body };

        Assert.Equal(1, await fixture.Run(args));
        Assert.Contains("invalid policy response", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("brokerIssuer", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--scope", "project")]
    [InlineData("--project-id", ProjectId)]
    [InlineData("--run-id", "run-1")]
    [InlineData("--policy-id", "bad/id")]
    [InlineData("--before-sequence", "0")]
    [InlineData("--before-sequence", "01")]
    [InlineData("--page-size", "101")]
    public async Task DecisionsRejectsInvalidArgumentsBeforeNetwork(string flag, string value)
    {
        var fixture = new Fixture();
        var args = flag == "--scope"
            ? new[] { "decisions", flag, value }
            : new[] { "decisions", "--scope", "platform", flag, value };

        Assert.Equal(1, await fixture.Run(args));
        Assert.Equal(0, fixture.RequestCount);
    }

    [Fact]
    public async Task HelpIncludesPlatformPolicyDecisionsCommand()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await CliApplication.RunAsync(["--help"], new CliRuntimeOptions { Output = output, Error = error });
        Assert.Equal(0, exitCode);
        Assert.Contains(
            "agentweaver policy decisions --scope platform [--policy-id <id>] [--page-size <1-100>] [--before-sequence <sequence>] [--output <new-file>]",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "agentweaver policy decisions --scope project --project-id <id> --resource-type skill --resource-id <skill-id> [--page-size <1-100>] [--before-sequence <sequence>] [--output <new-file>]",
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectSkillDecisionsProjectsAllowlistedFieldsPreservesPendingAndNullsAndRedactsIdentity()
    {
        var skillId = "Skill.Alpha/v1";
        var upperProjectId = ProjectId.ToUpperInvariant();
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            Body = DecisionHistoryBody(
                resourceId: skillId,
                nextBeforeSequence: 9_007_199_254_740_993,
                items:
                [
                    DecisionHistoryItemJson(
                        sequence: 9_007_199_254_740_994,
                        decisionType: "skill.trust.evidence.v1",
                        outcome: "allow",
                        approvalState: "approved",
                        policyRevision: 4,
                        includeSensitive: true),
                    DecisionHistoryItemJson(
                        sequence: 9_007_199_254_740_993,
                        decisionType: "skill.trust.evidence.v1",
                        outcome: "pending",
                        approvalState: "pending",
                        policyRevision: null,
                        policyArtifactSha256: null,
                        selectedRuleJson: "null",
                        ownerReceiptPosition: null,
                        includeSensitive: true),
                ],
                scopeType: "project",
                scopeId: ProjectId,
                resourceType: "skill"),
        };

        Assert.Equal(0, await fixture.Run(
            "decisions", "--scope", "project", "--project-id", upperProjectId,
            "--resource-type", "skill", "--resource-id", skillId, "--page-size", "2"));

        var stdout = fixture.Output.ToString();
        var output = JsonNode.Parse(stdout)!.AsObject();
        Assert.Equal("project", output["scopeType"]!.GetValue<string>());
        Assert.Equal(ProjectId, output["scopeId"]!.GetValue<string>());
        Assert.Equal("skill", output["resourceType"]!.GetValue<string>());
        Assert.Equal(skillId, output["resourceId"]!.GetValue<string>());
        Assert.Equal(9_007_199_254_740_993L, output["nextBeforeSequence"]!.GetValue<long>());

        var items = output["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal(9_007_199_254_740_994L, items[0]!["sequence"]!.GetValue<long>());
        Assert.Equal(4, items[0]!["decision"]!["policyRevision"]!.GetValue<long>());
        Assert.Equal("allow", items[0]!["decision"]!["outcome"]!.GetValue<string>());
        Assert.Equal(9_007_199_254_740_993L, items[1]!["sequence"]!.GetValue<long>());
        Assert.Equal("skill.trust.evidence.v1", items[1]!["decision"]!["decisionType"]!.GetValue<string>());
        Assert.Equal("pending", items[1]!["decision"]!["outcome"]!.GetValue<string>());
        Assert.Equal("pending", items[1]!["decision"]!["approvalState"]!.GetValue<string>());
        Assert.Null(items[1]!["decision"]!["policyRevision"]);
        Assert.Null(items[1]!["decision"]!["policyArtifactSha256"]);
        Assert.Null(items[1]!["decision"]!["selectedRule"]);
        Assert.Null(items[1]!["decision"]!["ownerReceiptPosition"]);

        foreach (var forbidden in new[]
        {
            "brokerIssuer", "brokerSubject", "humanEntraOid", "tenantId",
            "effectiveRoleRevisions", "assignmentId", Canary, "https://sensitive.example/",
        })
        {
            Assert.DoesNotContain(forbidden, stdout, StringComparison.Ordinal);
        }

        Assert.Equal("GET", fixture.PolicyMethod);
        Assert.Null(fixture.PolicyBody);
        Assert.Equal($"/api/v1/policies/projects/{ProjectId}/decisions", fixture.PolicyPath);
        Assert.Contains("resourceType=skill", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Contains("resourceId=Skill.Alpha%2Fv1", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Contains("pageSize=2", fixture.PolicyQuery, StringComparison.Ordinal);
        Assert.Null(fixture.PolicyProjectAuthorizationHeader);
        Assert.Equal("tenant-1", fixture.Authentication.Identity!.Target.TenantId);
        Assert.Null(fixture.Authentication.Identity.ProjectId);
        Assert.Null(fixture.Authentication.Identity.RunId);
        Assert.Equal(["openid", "api.read", "offline_access"], fixture.Authentication.Identity.Scopes);
        Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProjectSkillDecisionsSupportsContinuationFullLastPageEmptyPageAndMaxInt64Cursor()
    {
        var maxSequence = long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var fullLastPageFixture = new Fixture
        {
            UseGatewayContract = true,
            Body = DecisionHistoryBody(
                resourceId: "skill-1",
                nextBeforeSequence: null,
                items:
                [
                    DecisionHistoryItemJson(sequence: 9_007_199_254_740_993, decisionType: "skill.trust.evidence.v1"),
                    DecisionHistoryItemJson(sequence: 1, decisionType: "skill.trust.evidence.v1", outcome: "pending", approvalState: "pending", policyRevision: null),
                ],
                scopeType: "project",
                scopeId: ProjectId,
                resourceType: "skill"),
        };

        Assert.Equal(0, await fullLastPageFixture.Run(
            "decisions", "--scope", "project", "--project-id", ProjectId,
            "--resource-type", "skill", "--resource-id", "skill-1",
            "--page-size", "2", "--before-sequence", maxSequence));
        Assert.Contains($"beforeSequence={maxSequence}", fullLastPageFixture.PolicyQuery, StringComparison.Ordinal);
        var fullLastOutput = JsonNode.Parse(fullLastPageFixture.Output.ToString())!.AsObject();
        Assert.Equal(2, fullLastOutput["items"]!.AsArray().Count);
        Assert.Null(fullLastOutput["nextBeforeSequence"]);

        var emptyFixture = new Fixture
        {
            UseGatewayContract = true,
            Body = DecisionHistoryBody(
                resourceId: "skill-1",
                nextBeforeSequence: null,
                items: [],
                scopeType: "project",
                scopeId: ProjectId,
                resourceType: "skill"),
        };
        Assert.Equal(0, await emptyFixture.Run(
            "decisions", "--scope", "project", "--project-id", ProjectId,
            "--resource-type", "skill", "--resource-id", "skill-1",
            "--before-sequence", "1"));
        var emptyOutput = JsonNode.Parse(emptyFixture.Output.ToString())!.AsObject();
        Assert.Empty(emptyOutput["items"]!.AsArray());
        Assert.Null(emptyOutput["nextBeforeSequence"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized", "HTTP 401")]
    [InlineData(HttpStatusCode.Forbidden, "native_acs_forbidden", "denied (HTTP 403)")]
    [InlineData(HttpStatusCode.BadRequest, "governance_decision_history_request_invalid", "HTTP 400, governance_decision_history_request_invalid")]
    [InlineData(HttpStatusCode.InternalServerError, "governance_decision_corrupt", "HTTP 500, governance_decision_corrupt")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "governance_decision_source_unavailable", "HTTP 503, governance_decision_source_unavailable")]
    public async Task ProjectSkillDecisionsReportsControlledRedactedErrors(
        HttpStatusCode status, string code, string expectedMessage)
    {
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            Status = status,
            MediaType = "application/problem+json",
            Body = $$"""{"code":"{{code}}","detail":"{{Canary}}","brokerSubject":"{{Canary}}","unknownField":"{{Canary}}"}""",
        };

        Assert.Equal(1, await fixture.Run(
            "decisions", "--scope", "project", "--project-id", ProjectId,
            "--resource-type", "skill", "--resource-id", "skill-1"));
        Assert.Empty(fixture.Output.ToString());
        Assert.Contains(expectedMessage, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("project_not_found", "missing project")]
    [InlineData("project_forbidden", "inaccessible project")]
    public async Task ProjectSkillDecisions404ReturnsSameGenericMessageAndRedactsResponseBody(string code, string detail)
    {
        var fixture = new Fixture
        {
            UseGatewayContract = true,
            Status = HttpStatusCode.NotFound,
            MediaType = "application/problem+json",
            Body = $$"""{"code":"{{code}}","detail":"{{detail}} {{Canary}}"}""",
        };

        Assert.Equal(1, await fixture.Run(
            "decisions", "--scope", "project", "--project-id", ProjectId,
            "--resource-type", "skill", "--resource-id", "skill-1"));
        Assert.Empty(fixture.Output.ToString());
        Assert.Equal("The policy decision history request failed (HTTP 404).", fixture.Error.ToString().Trim());
        Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(code, fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProjectSkillDecisionsRedactsGatewayDispatchExceptionAndHandlesCancellation()
    {
        var transportFixture = new Fixture
        {
            UseGatewayContract = true,
            PolicyException = new HttpRequestException($"upstream failure {Canary}"),
        };

        Assert.Equal(1, await transportFixture.Run(
            "decisions", "--scope", "project", "--project-id", ProjectId,
            "--resource-type", "skill", "--resource-id", "skill-1"));
        Assert.Empty(transportFixture.Output.ToString());
        Assert.DoesNotContain(Canary, transportFixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED]", transportFixture.Client!.Calls.Last()["error"]!.GetValue<string>());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelFixture = new Fixture { UseGatewayContract = true };
        Assert.Equal(130, await cancelFixture.RunWithCancellation(
            cts.Token,
            "decisions", "--scope", "project", "--project-id", ProjectId,
            "--resource-type", "skill", "--resource-id", "skill-1"));
        Assert.Empty(cancelFixture.Output.ToString());
    }

    [Theory]
    [InlineData("wrongScope")]
    [InlineData("wrongProjectId")]
    [InlineData("wrongResourceType")]
    [InlineData("wrongResourceId")]
    [InlineData("outOfOrder")]
    [InlineData("sequenceAtCursor")]
    [InlineData("missingNextCursor")]
    [InlineData("cursorOnShortPage")]
    [InlineData("cursorOne")]
    public async Task ProjectSkillDecisionsFailsClosedOnWrongStreamOrMalformedData(string mode)
    {
        var otherProjectId = "11111111111111111111111111111111";
        var body = mode switch
        {
            "wrongScope" => DecisionHistoryBody("skill-1", null, [], scopeType: "platform", scopeId: "default", resourceType: "skill"),
            "wrongProjectId" => DecisionHistoryBody("skill-1", null, [], scopeType: "project", scopeId: otherProjectId, resourceType: "skill"),
            "wrongResourceType" => DecisionHistoryBody("skill-1", null, [], scopeType: "project", scopeId: ProjectId, resourceType: "project_policy"),
            "wrongResourceId" => DecisionHistoryBody("other-skill", null, [], scopeType: "project", scopeId: ProjectId, resourceType: "skill"),
            "outOfOrder" => DecisionHistoryBody("skill-1", null,
                [DecisionHistoryItemJson(4), DecisionHistoryItemJson(5)],
                scopeType: "project", scopeId: ProjectId, resourceType: "skill"),
            "sequenceAtCursor" => DecisionHistoryBody("skill-1", null,
                [DecisionHistoryItemJson(10)],
                scopeType: "project", scopeId: ProjectId, resourceType: "skill"),
            "missingNextCursor" => $$"""{"scopeType":"project","scopeId":"{{ProjectId}}","resourceType":"skill","resourceId":"skill-1","items":[]}""",
            "cursorOnShortPage" => DecisionHistoryBody("skill-1", nextBeforeSequence: 5,
                items: [DecisionHistoryItemJson(5)],
                scopeType: "project", scopeId: ProjectId, resourceType: "skill"),
            _ => DecisionHistoryBody("skill-1", nextBeforeSequence: 1,
                items: [DecisionHistoryItemJson(2), DecisionHistoryItemJson(1)],
                scopeType: "project", scopeId: ProjectId, resourceType: "skill"),
        };
        var args = mode is "cursorOnShortPage" or "cursorOne"
            ? new[] { "decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--page-size", "2" }
            : mode == "sequenceAtCursor"
                ? new[] { "decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--before-sequence", "10" }
                : new[] { "decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1" };
        var fixture = new Fixture { UseGatewayContract = true, Body = body };

        Assert.Equal(1, await fixture.Run(args));
        Assert.Empty(fixture.Output.ToString());
        Assert.Contains("invalid policy response", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-project-id")]
    [InlineData("bad-project-id")]
    [InlineData("missing-resource-type")]
    [InlineData("bad-resource-type")]
    [InlineData("missing-resource-id")]
    [InlineData("empty-resource-id")]
    [InlineData("blank-resource-id")]
    [InlineData("leading-space-resource-id")]
    [InlineData("trailing-space-resource-id")]
    [InlineData("too-long-resource-id")]
    [InlineData("page-size-zero")]
    [InlineData("page-size-leading-zero")]
    [InlineData("page-size-too-large")]
    [InlineData("before-sequence-zero")]
    [InlineData("before-sequence-leading-zero")]
    [InlineData("before-sequence-overflow")]
    [InlineData("run-id")]
    [InlineData("project-rejects-policy-id")]
    [InlineData("project-rejects-cursor")]
    [InlineData("platform-rejects-project-id")]
    [InlineData("platform-rejects-resource-type")]
    [InlineData("platform-rejects-resource-id")]
    public async Task ProjectAndPlatformDecisionsRejectInvalidAndCrossScopeOptionsBeforeNetwork(string mode)
    {
        var fixture = new Fixture();
        string[] args = mode switch
        {
            "missing-project-id" => ["decisions", "--scope", "project", "--resource-type", "skill", "--resource-id", "skill-1"],
            "bad-project-id" => ["decisions", "--scope", "project", "--project-id", "not-a-guid", "--resource-type", "skill", "--resource-id", "skill-1"],
            "missing-resource-type" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-id", "skill-1"],
            "bad-resource-type" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "project_policy", "--resource-id", "skill-1"],
            "missing-resource-id" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill"],
            "empty-resource-id" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", ""],
            "blank-resource-id" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "   "],
            "leading-space-resource-id" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", " skill-1"],
            "trailing-space-resource-id" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1 "],
            "too-long-resource-id" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", new string('a', 257)],
            "page-size-zero" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--page-size", "0"],
            "page-size-leading-zero" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--page-size", "01"],
            "page-size-too-large" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--page-size", "101"],
            "before-sequence-zero" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--before-sequence", "0"],
            "before-sequence-leading-zero" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--before-sequence", "01"],
            "before-sequence-overflow" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--before-sequence", "9223372036854775808"],
            "run-id" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--run-id", "run-1"],
            "project-rejects-policy-id" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--policy-id", "canonical"],
            "project-rejects-cursor" => ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--cursor", "abc"],
            "platform-rejects-project-id" => ["decisions", "--scope", "platform", "--project-id", ProjectId],
            "platform-rejects-resource-type" => ["decisions", "--scope", "platform", "--resource-type", "skill"],
            _ => ["decisions", "--scope", "platform", "--resource-id", "skill-1"],
        };

        Assert.Equal(1, await fixture.Run(args));
        Assert.Equal(0, fixture.RequestCount);
        Assert.Empty(fixture.Output.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrongPath")]
    [InlineData("postMethod")]
    [InlineData("requestBody")]
    [InlineData("secondaryProjectAuthHeader")]
    [InlineData("oversizedResponse")]
    public async Task ProjectSkillDecisionsRejectsIncompatibleDiscoveryBeforeDispatch(string mode)
    {
        var fixture = new Fixture
        {
            UseGatewayContract = mode != "missing",
            MutateOpenApi = doc =>
            {
                const string pathKey = "/api/v1/policies/projects/{projectId}/decisions";
                var pathItem = doc["paths"]![pathKey]!.AsObject();
                var op = pathItem["get"]!.AsObject();
                switch (mode)
                {
                    case "wrongPath":
                        doc["paths"]!.AsObject().Remove(pathKey);
                        doc["paths"]!["/api/v1/policies/projects/{projectId}/other"] = pathItem;
                        break;
                    case "postMethod":
                        pathItem.Remove("get");
                        pathItem["post"] = op;
                        break;
                    case "requestBody":
                        op["requestBody"] = JsonNode.Parse("""{"required":false,"content":{"application/json":{"schema":{"type":"object"}}}}""");
                        break;
                    case "secondaryProjectAuthHeader":
                        op["parameters"]!.AsArray().Add(JsonNode.Parse("""{"name":"X-Agentweaver-Project-Authorization","in":"header","required":false,"schema":{"type":"string"}}"""));
                        break;
                    case "oversizedResponse":
                        op["x-agentweaver-max-response-bytes"] = 5 * 1024 * 1024 + 1;
                        break;
                }
            },
        };

        Assert.Equal(1, await fixture.Run(
            "decisions", "--scope", "project", "--project-id", ProjectId,
            "--resource-type", "skill", "--resource-id", "skill-1"));
        Assert.Equal(0, fixture.PolicyRequestCount);
        Assert.Empty(fixture.Output.ToString());
    }

    [Theory]
    [InlineData("platform", false)]
    [InlineData("platform", true)]
    [InlineData("project", false)]
    [InlineData("project", true)]
    public async Task DecisionExportMatchesStdoutBytesRedactsAndPreservesLargeCursorSilently(string scope, bool empty)
    {
        const long largeCursor = 9_007_199_254_740_993L;
        var resourceId = scope == "project" ? "Skill.Alpha/v1" : "canonical";
        var resourceType = scope == "project" ? "skill" : "platform_policy";
        var scopeId = scope == "project" ? ProjectId : "default";
        var body = empty
            ? DecisionHistoryBody(resourceId, null, [], scopeType: scope, scopeId: scopeId, resourceType: resourceType)
            : DecisionHistoryBody(
                resourceId,
                largeCursor,
                [
                    DecisionHistoryItemJson(
                        sequence: 9_007_199_254_740_994L,
                        decisionType: scope == "project" ? "skill.trust.evidence.v1" : "platform_policy_preparation",
                        outcome: "allow",
                        approvalState: "approved",
                        policyRevision: 4,
                        includeSensitive: true),
                    DecisionHistoryItemJson(
                        sequence: largeCursor,
                        decisionType: scope == "project" ? "skill.trust.evidence.v1" : "platform_policy_preparation",
                        outcome: "pending",
                        approvalState: "pending",
                        policyRevision: null,
                        includeSensitive: true),
                ],
                scopeType: scope,
                scopeId: scopeId,
                resourceType: resourceType);

        var stdoutFixture = new Fixture { UseGatewayContract = true, Body = body };
        string[] baseArgs = scope == "project"
            ? ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", resourceId, "--page-size", "2"]
            : ["decisions", "--scope", "platform", "--page-size", "2"];
        Assert.Equal(0, await stdoutFixture.Run(baseArgs));
        var expectedBytes = Encoding.UTF8.GetBytes(stdoutFixture.Output.ToString());

        var tempDir = Path.Combine(Path.GetTempPath(), $"aw-decision-export-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(tempDir, "nested", "decisions-export.json");
        try
        {
            var fileFixture = new Fixture { UseGatewayContract = true, Body = body };
            Assert.Equal(0, await fileFixture.Run([.. baseArgs, "--output", outputPath]));
            Assert.Empty(fileFixture.Output.ToString());
            Assert.Empty(fileFixture.Error.ToString());
            Assert.True(File.Exists(outputPath));

            var actualBytes = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(expectedBytes, actualBytes);
            // UTF-8 without BOM
            Assert.False(actualBytes.Length >= 3 && actualBytes[0] == 0xEF && actualBytes[1] == 0xBB && actualBytes[2] == 0xBF);

            var fileText = Encoding.UTF8.GetString(actualBytes);
            foreach (var forbidden in new[]
            {
                "brokerIssuer", "brokerSubject", "humanEntraOid", "tenantId",
                "effectiveRoleRevisions", "assignmentId", Canary, "https://sensitive.example/",
            })
            {
                Assert.DoesNotContain(forbidden, fileText, StringComparison.Ordinal);
            }

            if (!empty)
                Assert.Contains($"\"nextBeforeSequence\": {largeCursor}", fileText, StringComparison.Ordinal);

            Assert.DoesNotContain(outputPath, fileFixture.PolicyQuery ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("decisions-export.json", fileFixture.Client!.Calls.Last().ToJsonString(), StringComparison.Ordinal);
            if (OperatingSystem.IsLinux())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(outputPath));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("platform", false)]
    [InlineData("platform", true)]
    [InlineData("project", false)]
    [InlineData("project", true)]
    public async Task DecisionDispatchExceptionRedactsEvidenceForBothScopesWithAndWithoutOutput(string scope, bool withOutput)
    {
        const string unredactedCanary = "unredacted-dispatch-canary-42";
        var tempDir = Path.Combine(Path.GetTempPath(), $"aw-decision-dispatch-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(tempDir, "decisions.json");
        try
        {
            var fixture = new Fixture
            {
                UseGatewayContract = true,
                PolicyException = new HttpRequestException($"upstream failure {unredactedCanary}"),
            };
            string[] args = scope == "project"
                ? ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1"]
                : ["decisions", "--scope", "platform"];
            if (withOutput)
                args = [.. args, "--output", outputPath];

            Assert.Equal(1, await fixture.Run(args));
            Assert.Empty(fixture.Output.ToString());
            Assert.DoesNotContain(unredactedCanary, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["error"]!.GetValue<string>());
            Assert.False(File.Exists(outputPath));
            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("platform", HttpStatusCode.Forbidden, "native_acs_forbidden", "denied (HTTP 403)")]
    [InlineData("project", HttpStatusCode.NotFound, "project_not_found", "HTTP 404")]
    [InlineData("platform", HttpStatusCode.OK, "malformed", "invalid policy response")]
    [InlineData("project", HttpStatusCode.OK, "malformed", "invalid policy response")]
    public async Task DecisionExportFailureOrMalformedRowCreatesNoFileAndNoStdout(
        string scope, HttpStatusCode status, string mode, string expectedError)
    {
        var body = mode == "malformed"
            ? DecisionHistoryBody(
                scope == "project" ? "skill-1" : "canonical",
                null,
                [
                    DecisionHistoryItemJson(10),
                    DecisionHistoryItemJson(11), // out of order on later row
                ],
                scopeType: scope,
                scopeId: scope == "project" ? ProjectId : "default",
                resourceType: scope == "project" ? "skill" : "platform_policy")
            : $$"""{"code":"{{mode}}","detail":"{{Canary}}"}""";

        var tempDir = Path.Combine(Path.GetTempPath(), $"aw-decision-fail-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(tempDir, "nested", "decisions.json");
        try
        {
            var fixture = new Fixture
            {
                UseGatewayContract = true,
                Status = status,
                MediaType = status == HttpStatusCode.OK ? "application/json" : "application/problem+json",
                Body = body,
            };
            string[] args = scope == "project"
                ? ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--output", outputPath]
                : ["decisions", "--scope", "platform", "--output", outputPath];

            Assert.Equal(1, await fixture.Run(args));
            Assert.Empty(fixture.Output.ToString());
            Assert.Contains(expectedError, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Canary, fixture.Error.ToString(), StringComparison.Ordinal);
            Assert.Equal("[REDACTED]", fixture.Client!.Calls.Last()["responseBody"]!.GetValue<string>());
            Assert.False(File.Exists(outputPath));
            Assert.False(Directory.Exists(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("missing-value")]
    [InlineData("repeated")]
    public async Task DecisionExportRejectsInvalidOutputFlagSyntaxBeforeDiscovery(string mode)
    {
        var fixture = new Fixture();
        string[] args = mode switch
        {
            "empty" => ["decisions", "--scope", "platform", "--output", ""],
            "blank" => ["decisions", "--scope", "platform", "--output", "   "],
            "missing-value" => ["decisions", "--scope", "platform", "--output"],
            _ => ["decisions", "--scope", "platform", "--output", "a.json", "--output", "b.json"],
        };

        Assert.Equal(1, await fixture.Run(args));
        Assert.Equal(0, fixture.RequestCount);
        Assert.Empty(fixture.Output.ToString());
        if (mode is "empty" or "blank")
            Assert.Equal("Choose a valid policy decision export file path." + Environment.NewLine, fixture.Error.ToString());
    }

    [Theory]
    [InlineData("report.json:notes")]
    [InlineData(@"C:\reports\report.json:notes")]
    [InlineData("C:report.json:notes")]
    [InlineData("report.json::$DATA")]
    [InlineData("report.json:")]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"\\.\C:\report.json")]
    [InlineData(@"\\?\C:\report.json")]
    [InlineData(@"\\?\UNC\server\share\report.json")]
    [InlineData(@"\??\C:\report.json")]
    [InlineData("//./NUL")]
    [InlineData("//?/C:/report.json")]
    [InlineData("/??/C:/report.json")]
    [InlineData(@"\/./report.json")]
    [InlineData("NUL")]
    [InlineData("NUL.json")]
    [InlineData(@"subdir\cOn.json")]
    [InlineData("subdir/cOn.json")]
    [InlineData("CONOUT$")]
    [InlineData("COM1.log")]
    [InlineData("LPT\u00B2.json")]
    [InlineData("AUX .json")]
    [InlineData("report?.json")]
    [InlineData(@"folder|name\output.json")]
    [InlineData("folder<name>/output*.json")]
    public async Task DecisionExportRejectsUnsafeWindowsPathsBeforeDiscovery(string badPath)
    {
        if (!OperatingSystem.IsWindows())
            return;

        foreach (var scope in new[] { "platform", "project" })
        {
            var fixture = new Fixture();
            string[] args = scope == "project"
                ? ["decisions", "--scope", "project", "--project-id", ProjectId, "--resource-type", "skill", "--resource-id", "skill-1", "--output", badPath]
                : ["decisions", "--scope", "platform", "--output", badPath];

            Assert.Equal(1, await fixture.Run(args));
            Assert.Equal(0, fixture.RequestCount);
            Assert.Empty(fixture.Output.ToString());
            Assert.Equal("Choose a valid policy decision export file path." + Environment.NewLine, fixture.Error.ToString());
        }
    }

    [Fact]
    public async Task DecisionExportAcceptsWindowsNearMatchesNormalDriveAndLexicalUnc()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var unc = PolicyCommands.NormalizeDecisionOutputPath(@"\\nonexistent-server.invalid\share\report.json");
        Assert.Equal(@"\\nonexistent-server.invalid\share\report.json", unc);

        var tempDir = Path.Combine(Path.GetTempPath(), $"aw-win-paths-{Guid.NewGuid():N}");
        try
        {
            foreach (var fileName in new[] { "NULL.json", "COM10.json" })
            {
                var target = Path.Combine(tempDir, fileName);
                var fixture = new Fixture
                {
                    UseGatewayContract = true,
                    Body = DecisionHistoryBody("canonical", null, []),
                };
                Assert.Equal(0, await fixture.Run("decisions", "--scope", "platform", "--output", target));
                Assert.Empty(fixture.Output.ToString());
                Assert.Empty(fixture.Error.ToString());
                Assert.True(File.Exists(target));
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DecisionExportOnUnixAllowsColonsBackslashesAndReservedWindowsNames()
    {
        if (OperatingSystem.IsWindows())
            return;

        var tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"aw-unix-paths-{Guid.NewGuid():N}")).FullName;
        try
        {
            foreach (var name in new[] { "NUL.json", @"report:1\file.json" })
            {
                var target = Path.Combine(tempDir, name);
                var fixture = new Fixture
                {
                    UseGatewayContract = true,
                    Body = DecisionHistoryBody("canonical", null, []),
                };
                Assert.Equal(0, await fixture.Run("decisions", "--scope", "platform", "--output", target));
                Assert.Empty(fixture.Output.ToString());
                Assert.Empty(fixture.Error.ToString());
                Assert.True(File.Exists(target));
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DecisionExportRefusesExistingFileDirectoryLateCollisionAndSymlinks()
    {
        var tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"aw-decision-collision-{Guid.NewGuid():N}")).FullName;
        try
        {
            var existingFile = Path.Combine(tempDir, "existing.json");
            await File.WriteAllTextAsync(existingFile, "keep-me");
            var preflightFileFixture = new Fixture();
            Assert.Equal(1, await preflightFileFixture.Run("decisions", "--scope", "platform", "--output", existingFile));
            Assert.Equal(0, preflightFileFixture.RequestCount);
            Assert.Empty(preflightFileFixture.Output.ToString());
            Assert.Equal("The policy decision export output file already exists." + Environment.NewLine, preflightFileFixture.Error.ToString());
            Assert.Equal("keep-me", await File.ReadAllTextAsync(existingFile));

            var existingDir = Directory.CreateDirectory(Path.Combine(tempDir, "existing-dir")).FullName;
            var preflightDirFixture = new Fixture();
            Assert.Equal(1, await preflightDirFixture.Run("decisions", "--scope", "platform", "--output", existingDir));
            Assert.Equal(0, preflightDirFixture.RequestCount);
            Assert.Empty(preflightDirFixture.Output.ToString());
            Assert.Equal("The policy decision export output file already exists." + Environment.NewLine, preflightDirFixture.Error.ToString());
            Assert.True(Directory.Exists(existingDir));

            var lateCollisionPath = Path.Combine(tempDir, "late-collision.json");
            var lateFixture = new Fixture
            {
                UseGatewayContract = true,
                Body = DecisionHistoryBody("canonical", null, []),
                OnPolicyRequest = () => File.WriteAllText(lateCollisionPath, "created-during-request"),
            };
            Assert.Equal(1, await lateFixture.Run("decisions", "--scope", "platform", "--output", lateCollisionPath));
            Assert.Empty(lateFixture.Output.ToString());
            Assert.Equal("The policy decision export file could not be created." + Environment.NewLine, lateFixture.Error.ToString());
            Assert.Equal("created-during-request", await File.ReadAllTextAsync(lateCollisionPath));

            // Symlink checks (live symlink to sentinel, and dangling symlink)
            var sentinelPath = Path.Combine(tempDir, "sentinel.json");
            await File.WriteAllTextAsync(sentinelPath, "sentinel-content");
            var liveLinkPath = Path.Combine(tempDir, "live-link.json");
            var danglingTarget = Path.Combine(tempDir, "missing-target.json");
            var danglingLinkPath = Path.Combine(tempDir, "dangling-link.json");
            bool symlinksCreated;
            try
            {
                File.CreateSymbolicLink(liveLinkPath, sentinelPath);
                File.CreateSymbolicLink(danglingLinkPath, danglingTarget);
                symlinksCreated = true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                symlinksCreated = false;
            }

            if (symlinksCreated)
            {
                var liveLinkFixture = new Fixture
                {
                    UseGatewayContract = true,
                    Body = DecisionHistoryBody("canonical", null, []),
                };
                Assert.Equal(1, await liveLinkFixture.Run("decisions", "--scope", "platform", "--output", liveLinkPath));
                Assert.Empty(liveLinkFixture.Output.ToString());
                Assert.Equal("sentinel-content", await File.ReadAllTextAsync(sentinelPath));
                Assert.True(new FileInfo(liveLinkPath).LinkTarget is not null);

                var danglingFixture = new Fixture
                {
                    UseGatewayContract = true,
                    Body = DecisionHistoryBody("canonical", null, []),
                };
                Assert.Equal(1, await danglingFixture.Run("decisions", "--scope", "platform", "--output", danglingLinkPath));
                Assert.Empty(danglingFixture.Output.ToString());
                Assert.False(File.Exists(danglingTarget));
                Assert.True(new FileInfo(danglingLinkPath).LinkTarget is not null);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("cancel-before")]
    [InlineData("fail-after-create")]
    [InlineData("cancel-after-create")]
    [InlineData("fail-on-close")]
    [InlineData("fail-on-close-after-cancel")]
    public async Task DecisionExportHandlesCancellationAndPostCreationFailuresWithoutDeletingDestination(string mode)
    {
        var tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"aw-decision-faults-{Guid.NewGuid():N}")).FullName;
        var outputPath = Path.Combine(tempDir, "fault-output.json");
        try
        {
            using var cts = new CancellationTokenSource();
            var fixture = new Fixture
            {
                UseGatewayContract = true,
                Body = DecisionHistoryBody("canonical", null, []),
            };

            switch (mode)
            {
                case "cancel-before":
                    fixture.DecisionExportStageHook = stage =>
                    {
                        if (stage == "before-create")
                            cts.Cancel();
                    };
                    break;
                case "fail-after-create":
                    fixture.DecisionExportStageHook = stage =>
                    {
                        if (stage == "created")
                            throw new IOException("simulated write failure");
                    };
                    break;
                case "cancel-after-create":
                    fixture.DecisionExportStageHook = stage =>
                    {
                        if (stage == "created")
                            cts.Cancel();
                    };
                    break;
                case "fail-on-close":
                    fixture.DecisionExportStageHook = stage =>
                    {
                        if (stage == "closing")
                            throw new IOException("simulated close failure");
                    };
                    break;
                case "fail-on-close-after-cancel":
                    fixture.DecisionExportStageHook = stage =>
                    {
                        if (stage == "created")
                            cts.Cancel();
                        if (stage == "closing")
                            throw new IOException("simulated close failure after cancel");
                    };
                    break;
            }

            var exitCode = await fixture.RunWithCancellation(
                cts.Token, "decisions", "--scope", "platform", "--output", outputPath);

            Assert.Empty(fixture.Output.ToString());
            if (mode == "cancel-before")
            {
                Assert.Equal(130, exitCode);
                Assert.Equal("Operation cancelled." + Environment.NewLine, fixture.Error.ToString());
                Assert.False(File.Exists(outputPath));
            }
            else if (mode is "cancel-after-create" or "fail-on-close-after-cancel")
            {
                Assert.Equal(130, exitCode);
                Assert.Equal(
                    "Policy decision export cancelled. Treat the output file as incomplete. Retry with a new file name." + Environment.NewLine,
                    fixture.Error.ToString());
                Assert.True(File.Exists(outputPath));
            }
            else
            {
                Assert.Equal(1, exitCode);
                Assert.Equal(
                    "The policy decision export did not finish. Treat the output file as incomplete. Retry with a new file name." + Environment.NewLine,
                    fixture.Error.ToString());
                Assert.True(File.Exists(outputPath));
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string DecisionHistoryBody(
        string resourceId,
        long? nextBeforeSequence,
        string[] items,
        string scopeType = "platform",
        string scopeId = "default",
        string resourceType = "platform_policy")
    {
        var next = nextBeforeSequence is null ? "null" : nextBeforeSequence.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $$"""{"scopeType":"{{scopeType}}","scopeId":"{{scopeId}}","resourceType":"{{resourceType}}","resourceId":"{{resourceId}}","items":[{{string.Join(",", items)}}],"nextBeforeSequence":{{next}}}""";
    }

    private static string DecisionHistoryItemJson(
        long sequence,
        string decisionType = "platform_policy_preparation",
        string outcome = "allow",
        string approvalState = "notRequired",
        long? policyRevision = 4,
        string? policyArtifactSha256 = null,
        bool includeSensitive = false,
        string selectedRuleJson = "\"platform.allow\"",
        long? ownerReceiptPosition = null)
    {
        var rev = policyRevision is null ? "null" : policyRevision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var artifactSha = policyArtifactSha256 ?? (policyRevision is null ? null : Hash);
        var artifactShaJson = artifactSha is null ? "null" : $"\"{artifactSha}\"";
        var receiptPosJson = ownerReceiptPosition is null ? "null" : ownerReceiptPosition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sensitive = includeSensitive
            ? $$"""
                ,"humanEntraOid":"00000000-0000-0000-0000-000000000099","tenantId":"00000000-0000-0000-0000-000000000088","brokerIssuer":"https://sensitive.example/","brokerSubject":"{{Canary}}","effectiveRoleRevisions":[{"assignmentId":"00000000-0000-0000-0000-000000000077","revision":2}]
                """
            : string.Empty;
        return $$$"""{"decisionId":"00000000-0000-0000-0000-00000000000{{{sequence % 10}}}","sequence":{{{sequence}}},"previousHash":"{{{ArtifactHash(1)}}}","recordHash":"{{{ArtifactHash(2)}}}","recordedAt":"{{{Time}}}","decision":{"decisionType":"{{{decisionType}}}","outcome":"{{{outcome}}}","approvalState":"{{{approvalState}}}","policyRevision":{{{rev}}},"policyArtifactSha256":{{{artifactShaJson}}},"selectedRule":{{{selectedRuleJson}}},"interventionPoint":null,"requestId":"req-1","actionId":null,"runId":null,"agentId":null,"roleId":null,"ownerReceiptId":"prep-1","ownerReceiptPosition":{{{receiptPosJson}}},"compilerGeneration":null,"applicationGeneration":null,"correlationId":"corr-1","decidedAt":"{{{Time}}}"{{{sensitive}}}}}""";
    }

    private sealed class FailingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value) => throw new IOException("simulated output failure");
    }

    private static string ApplyReceipt(string requestBody, bool replayed)
    {
        var request = JsonNode.Parse(requestBody)!.AsObject();
        var expectedRevision = request["expectedRevision"]?.GetValue<long>();
        var revision = expectedRevision is null ? 1 : expectedRevision.Value + 1;
        var hash = request["artifact"]!["contentSha256"]!.GetValue<string>();
        return $$"""{"scopeType":"platform","scopeId":"default","policyId":"canonical","revision":{{revision}},"contentSha256":"{{hash}}","auditId":"00000000-0000-0000-0000-000000000001","outboxId":"00000000-0000-0000-0000-000000000002","replayed":{{replayed.ToString().ToLowerInvariant()}}}""";
    }

    private static HttpResponseMessage JsonResponse(
        HttpStatusCode status,
        string body,
        string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class Fixture : HttpMessageHandler, IGatewayClientFactory
    {
        public readonly StringWriter Output = new();
        public readonly StringWriter Error = new();
        public readonly Authentication Authentication = new();
        public GatewayClient? Client;
        public Func<int, string, CancellationToken, Task<HttpResponseMessage>>? ApplyResponse;
        public Action<string>? BeforePolicyApply;
        public Action? OnPolicyRequest;
        public Action<string>? DecisionExportStageHook;
        public string PolicyApplyStateDirectory = Path.Combine(
            AppContext.BaseDirectory, $"policy-apply-state-{Guid.NewGuid():N}");
        public bool UnsafePolicyApplyRequestBound;
        public bool UnsafePolicyApplyResponseBound;
        public bool WrongPolicyApplyPath;
        public bool WrongPolicyApplyMethod;
        public string Body = ShowBody(false);
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string MediaType = "application/json";
        public string? UnsafeOperation;
        public bool UnsafeValidationRequestBound;
        public bool WrongValidationPath;
        public int? ProjectValidationRequestBound;
        public string? ProjectValidationSchemaMutation;
        public bool WrongPath;
        public bool UseGatewayContract;
        public Action<JsonObject>? MutateOpenApi;
        public Exception? PolicyException;
        public int RequestCount;
        public string? PolicyPath;
        public string? PolicyQuery;
        public string? PolicyMethod;
        public string? PolicyBody;
        public string? PolicyProjectAuthorizationHeader;
        public int PolicyRequestCount;
        private int _applyRequestCount;
        public int ApplyRequestCount => Volatile.Read(ref _applyRequestCount);

        public IGatewayClient Create(CliIdentity identity) =>
            Client = new GatewayClient(new HttpClient(this, disposeHandler: false), identity, Authentication);

        public Task<int> Run(params string[] args) => RunWithCancellation(CancellationToken.None, args);

        public Task<int> RunWithCancellation(CancellationToken ct, params string[] args) => CliApplication.RunAsync(
            ["policy", .. args, .. Target],
            new CliRuntimeOptions(GatewayClientFactory: this, Authentication: Authentication, Output: Output, Error: Error)
            {
                PolicyApplyStateDirectory = this.PolicyApplyStateDirectory,
                DecisionExportStageHook = this.DecisionExportStageHook,
            },
            ct);

        public void CleanupPolicyState()
        {
            if (Directory.Exists(PolicyApplyStateDirectory))
                Directory.Delete(PolicyApplyStateDirectory, recursive: true);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/openapi/v1.json")
                return await Response(HttpStatusCode.OK, OpenApi(), "application/json").ConfigureAwait(false);
            if (path == "/api/v1/authorization/context")
                return await Response(HttpStatusCode.OK,
                    """{"contractVersion":1,"issuer":"https://identity.example/","actorId":"actor-1","tenantId":"tenant-1","membershipRevision":1,"boundProjectId":null,"boundRunId":null,"effectiveAuthority":[]}""",
                    "application/json").ConfigureAwait(false);
            PolicyRequestCount++;
            PolicyPath = path;
            PolicyQuery = request.RequestUri.Query;
            PolicyMethod = request.Method.Method;
            PolicyBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            PolicyProjectAuthorizationHeader =
                request.Headers.TryGetValues("X-Agentweaver-Project-Authorization", out var projectBearer)
                    ? string.Join(',', projectBearer)
                    : null;
            if (PolicyException is not null)
                throw PolicyException;
            OnPolicyRequest?.Invoke();
            if (path == "/api/v1/policies/platform" && request.Method == HttpMethod.Put)
            {
                BeforePolicyApply?.Invoke(PolicyBody!);
                var attempt = Interlocked.Increment(ref _applyRequestCount);
                if (ApplyResponse is not null)
                    return await ApplyResponse(attempt, PolicyBody!, ct).ConfigureAwait(false);
            }
            return await Response(Status, Body, MediaType).ConfigureAwait(false);
        }

        private static Task<HttpResponseMessage> Response(HttpStatusCode status, string body, string mediaType) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

        private string OpenApi()
        {
            if (UseGatewayContract)
            {
                var type = typeof(GatewayEndpoints).Assembly.GetType("Agentweaver.Gateway.GatewayOpenApi")!;
                var emitted = type.GetMethod("CreateDocument", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                var publishedDocument = JsonNode.Parse(
                    JsonSerializer.Serialize(emitted, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
                MutateOpenApi?.Invoke(publishedDocument);
                if (ProjectValidationSchemaMutation is not null)
                {
                    var schema = publishedDocument["paths"]!["/api/v1/policies/projects/{projectId}/validate"]!
                        ["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject();
                    switch (ProjectValidationSchemaMutation)
                    {
                        case "wrong-reference":
                            schema["$ref"] = "#/components/schemas/NativeAcsArtifact";
                            break;
                        case "inline":
                            schema.Remove("$ref");
                            schema["type"] = "object";
                            break;
                        case "missing":
                            schema.Parent!.AsObject().Remove("schema");
                            break;
                        case "missing-component":
                            publishedDocument["components"]!["schemas"]!.AsObject()
                                .Remove("NativeAcsPolicyValidationRequest");
                            break;
                        case "optional-artifact":
                            publishedDocument["components"]!["schemas"]!["NativeAcsPolicyValidationRequest"]!
                                ["required"] = new JsonArray();
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }
                return publishedDocument.ToJsonString();
            }
            var response = """{"200":{"content":{"application/json":{"schema":{"type":"object"}}}},"403":{"content":{"application/problem+json":{"schema":{"type":"object"}}}}}""";
            var tenant = """{"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}}""";
            string Operation(string id, string parameters) =>
                $$"""{"operationId":"{{id}}","x-agentweaver-max-response-bytes":{{(UnsafeOperation == id ? 6000000 : 5242880)}},"parameters":[{{parameters}}],"responses":{{response}}}""";
            var headPath = WrongPath ? "/api/v1/policies/other" : "/api/v1/policies/platform";
            var historyParameters = """{"name":"pageSize","in":"query","schema":{"type":"integer"}},{"name":"cursor","in":"query","schema":{"type":"string"}},""" + tenant;
            var revisionParameters = """{"name":"revision","in":"path","required":true,"schema":{"type":"string"}},""" + tenant;
            var validationResponse = """{"200":{"content":{"application/json":{"schema":{"type":"object"}}}},"403":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"422":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"429":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"503":{"content":{"application/problem+json":{"schema":{"type":"object"}}}}}""";
            var validationRequestBody = """{"required":true,"content":{"application/json":{"schema":{"$ref":"#/components/schemas/NativeAcsPolicyValidationRequest"}}}}""";
            var projectValidationParameters =
                """{"name":"projectId","in":"path","required":true,"schema":{"type":"string"}},""" + tenant;
            var validationPath = WrongValidationPath
                ? "/api/v1/policies/platform/invalid"
                : "/api/v1/policies/platform/validate";
            var requestLimit = UnsafeValidationRequestBound ? 6000000 : 5242880;
            var projectRequestLimit = ProjectValidationRequestBound ?? 5242880;
            var applyPath = WrongPolicyApplyPath
                ? "/api/v1/policies/platform/invalid-write"
                : "/api/v1/policies/platform";
            var applyMethod = WrongPolicyApplyMethod ? "post" : "put";
            var applyRequestLimit = UnsafePolicyApplyRequestBound ? 6000000 : 5242880;
            var applyResponseLimit = UnsafePolicyApplyResponseBound ? 6000000 : 5242880;
            var applyRequestBody = """{"required":true,"content":{"application/json":{"schema":{"type":"object"}}}}""";
            var applyResponse = """{"200":{"content":{"application/json":{"schema":{"type":"object"}}}},"403":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"409":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"422":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"429":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"503":{"content":{"application/problem+json":{"schema":{"type":"object"}}}}}""";
            var document = JsonNode.Parse(
                """{"openapi":"3.1.0","security":[{"Bearer":[]}],"components":{"securitySchemes":{"Bearer":{"type":"http","scheme":"bearer"}},"schemas":{"NativeAcsArtifact":{"type":"object"},"NativeAcsPolicyValidationRequest":{"type":"object","additionalProperties":false,"required":["artifact"],"properties":{"artifact":{"$ref":"#/components/schemas/NativeAcsArtifact"}}}}}}""")!.AsObject();
            var paths = new JsonObject
            {
                ["/api/v1/authorization/context"] = new JsonObject { ["get"] = JsonNode.Parse(Operation("getAuthorizationContext", tenant)) },
                [headPath] = new JsonObject { ["get"] = JsonNode.Parse(Operation("readPlatformNativeAcsPolicy", tenant)) },
                ["/api/v1/policies/platform/revisions"] = new JsonObject { ["get"] = JsonNode.Parse(Operation("listPlatformNativeAcsPolicyRevisions", historyParameters)) },
                ["/api/v1/policies/platform/revisions/{revision}"] = new JsonObject { ["get"] = JsonNode.Parse(Operation("readPlatformNativeAcsPolicyRevision", revisionParameters)) },
                [validationPath] = new JsonObject
                {
                    ["post"] = JsonNode.Parse(
                        $$"""{"operationId":"validatePlatformNativeAcsPolicy","x-agentweaver-max-request-body-bytes":{{requestLimit}},"x-agentweaver-max-response-bytes":5242880,"parameters":[{{tenant}}],"requestBody":{{validationRequestBody}},"responses":{{validationResponse}}}"""),
                },
                ["/api/v1/policies/projects/{projectId}/validate"] = new JsonObject
                {
                    ["post"] = JsonNode.Parse(
                        $$"""{"operationId":"validateProjectNativeAcsPolicy","x-agentweaver-max-request-body-bytes":{{projectRequestLimit}},"x-agentweaver-max-response-bytes":5242880,"parameters":[{{projectValidationParameters}}],"requestBody":{{validationRequestBody}},"responses":{{validationResponse}}}"""),
                },
            };
            if (paths[applyPath] is not JsonObject applyPathItem)
                paths[applyPath] = applyPathItem = [];
            applyPathItem[applyMethod] = JsonNode.Parse(
                $$"""{"operationId":"writePlatformNativeAcsPolicy","x-agentweaver-max-request-body-bytes":{{applyRequestLimit}},"x-agentweaver-max-response-bytes":{{applyResponseLimit}},"parameters":[{{tenant}}],"requestBody":{{applyRequestBody}},"responses":{{applyResponse}}}""");
            document["paths"] = paths;
            return document.ToJsonString();
        }
    }

    private sealed class Authentication : ICliAuthentication
    {
        public CliIdentity? Identity;
        public InteractionPolicy Interaction;
        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct)
        {
            Identity = identity;
            Interaction = interaction;
            return Task.FromResult("safe-bearer");
        }
        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => throw new NotSupportedException();
    }
}
