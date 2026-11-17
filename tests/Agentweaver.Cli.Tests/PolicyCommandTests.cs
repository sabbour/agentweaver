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

    private static string ShowBodyWithCanary(bool exact)
    {
        var body = JsonNode.Parse(ShowBody(exact))!.AsObject();
        body["artifact"]!["manifestBytes"] = new JsonArray(67, 65, 78, 65, 82, 89);
        RehashExport(body);
        return body.ToJsonString();
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

    private sealed class Fixture : HttpMessageHandler, IGatewayClientFactory
    {
        public readonly StringWriter Output = new();
        public readonly StringWriter Error = new();
        public readonly Authentication Authentication = new();
        public GatewayClient? Client;
        public string Body = ShowBody(false);
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string MediaType = "application/json";
        public string? UnsafeOperation;
        public bool UnsafeValidationRequestBound;
        public bool WrongValidationPath;
        public bool WrongPath;
        public bool UseGatewayContract;
        public int RequestCount;
        public string? PolicyPath;
        public string? PolicyQuery;
        public string? PolicyMethod;
        public string? PolicyBody;

        public IGatewayClient Create(CliIdentity identity) =>
            Client = new GatewayClient(new HttpClient(this, disposeHandler: false), identity, Authentication);

        public Task<int> Run(params string[] args) => CliApplication.RunAsync(
            ["policy", .. args, .. Target],
            new CliRuntimeOptions(GatewayClientFactory: this, Authentication: Authentication, Output: Output, Error: Error));

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
            PolicyPath = path;
            PolicyQuery = request.RequestUri.Query;
            PolicyMethod = request.Method.Method;
            PolicyBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
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
                return JsonSerializer.Serialize(emitted, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            var response = """{"200":{"content":{"application/json":{"schema":{"type":"object"}}}},"403":{"content":{"application/problem+json":{"schema":{"type":"object"}}}}}""";
            var tenant = """{"name":"X-Agentweaver-Tenant","in":"header","required":true,"schema":{"type":"string"}}""";
            string Operation(string id, string parameters) =>
                $$"""{"operationId":"{{id}}","x-agentweaver-max-response-bytes":{{(UnsafeOperation == id ? 6000000 : 5242880)}},"parameters":[{{parameters}}],"responses":{{response}}}""";
            var headPath = WrongPath ? "/api/v1/policies/other" : "/api/v1/policies/platform";
            var historyParameters = """{"name":"pageSize","in":"query","schema":{"type":"integer"}},{"name":"cursor","in":"query","schema":{"type":"string"}},""" + tenant;
            var revisionParameters = """{"name":"revision","in":"path","required":true,"schema":{"type":"string"}},""" + tenant;
            var validationResponse = """{"200":{"content":{"application/json":{"schema":{"type":"object"}}}},"403":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"422":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"429":{"content":{"application/problem+json":{"schema":{"type":"object"}}}},"503":{"content":{"application/problem+json":{"schema":{"type":"object"}}}}}""";
            var validationRequestBody = """{"required":true,"content":{"application/json":{"schema":{"type":"object"}}}}""";
            var validationPath = WrongValidationPath
                ? "/api/v1/policies/platform/invalid"
                : "/api/v1/policies/platform/validate";
            var requestLimit = UnsafeValidationRequestBound ? 6000000 : 5242880;
            var document = JsonNode.Parse(
                """{"openapi":"3.1.0","security":[{"Bearer":[]}],"components":{"securitySchemes":{"Bearer":{"type":"http","scheme":"bearer"}}}}""")!.AsObject();
            document["paths"] = new JsonObject
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
            };
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
