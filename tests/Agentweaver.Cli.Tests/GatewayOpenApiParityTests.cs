using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;
using Agentweaver.Cli.Auth;
using Agentweaver.Cli.Vault;
using Agentweaver.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentweaver.Cli.Tests;

public sealed class GatewayOpenApiParityTests
{
    [Fact]
    public async Task CliDiscoveryParsesTheEmittedGatewayDocument()
    {
        await using var app = await StartGatewayAsync();
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage response = await http.GetAsync(new Uri(new Uri(address), "/openapi/v1.json"));
        string artifact = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        JsonObject emitted = JsonNode.Parse(artifact)!.AsObject();
        Assert.Equal("3.1.0", emitted["openapi"]?.GetValue<string>());
        JsonObject securitySchemes = Assert.IsType<JsonObject>(emitted["components"]?["securitySchemes"]);
        JsonObject bearer = Assert.IsType<JsonObject>(securitySchemes.First(entry =>
            entry.Key.Equals("Bearer", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.Equal("http", bearer["type"]?.GetValue<string>());
        Assert.Equal("bearer", bearer["scheme"]?.GetValue<string>());
        Assert.NotNull(emitted["security"]);

        var target = new CliTarget(
            new Uri(address),
            new Uri("https://issuer.example/"),
            "cli-tests",
            "agentweaver",
            "tenant-test");
        var api = new GatewayClient(http, new CliIdentity(target, ["openid", "api.read", "offline_access"]));
        JsonObject discovered = await api.DiscoverAsync(CancellationToken.None);

        Assert.True(JsonNode.DeepEquals(emitted, discovered));
        JsonObject targetContext = FindOperation(discovered, "getAuthorizationTargetContext");
        Assert.True(JsonNode.DeepEquals(targetContext,
            discovered["paths"]?["/api/v1/authorization/target-context"]?["get"]));
        Assert.Equal("#/components/schemas/AuthorizationTargetContext",
            targetContext["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?
                .GetValue<string>());
        Assert.True(targetContext["x-agentweaver-mcp-excluded"]?.GetValue<bool>());
        Assert.Equal(4096, CursorMaximum(FindOperation(discovered, "listProjectNativeAcsPolicies")));
        Assert.Equal(2048, CursorMaximum(FindOperation(discovered, "listProjectNativeAcsPolicyRevisions")));
        Assert.Equal(2048, CursorMaximum(FindOperation(discovered, "listPlatformNativeAcsPolicyRevisions")));
        JsonObject createProject = FindOperation(discovered, "createProject");
        Assert.Contains("503", Assert.IsType<JsonObject>(createProject["responses"]).Select(item => item.Key));
        Assert.Contains("role_assignment_unavailable",
            createProject["responses"]?["503"]?["description"]?.GetValue<string>());

        JsonObject recoverRun = FindOperation(discovered, "recoverRun");
        Assert.Contains("202", Assert.IsType<JsonObject>(recoverRun["responses"]).Select(item => item.Key));
        Assert.Equal("#/components/schemas/RecoverRunExecutionRequest",
            recoverRun["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>());
        JsonObject schemas = Assert.IsType<JsonObject>(discovered["components"]?["schemas"]);
        foreach (string requestSchema in new[]
        {
            "ModelProviderDraftPlatformCreateRequest",
            "ModelProviderDraftProjectCreateRequest",
            "ModelProviderDraftSaveRequest",
        })
        {
            JsonObject schema = Assert.IsType<JsonObject>(schemas[requestSchema]);
            JsonObject properties = Assert.IsType<JsonObject>(schema["properties"]);
            Assert.Equal("string", properties["rate_card_id"]?["anyOf"]?[0]?["type"]?.GetValue<string>());
            Assert.Equal("null", properties["rate_card_id"]?["anyOf"]?[1]?["type"]?.GetValue<string>());
            Assert.Equal("string", properties["rate_card_version"]?["anyOf"]?[0]?["type"]?.GetValue<string>());
            Assert.Equal("null", properties["rate_card_version"]?["anyOf"]?[1]?["type"]?.GetValue<string>());
            Assert.DoesNotContain("rate_card_id",
                Assert.IsType<JsonArray>(schema["required"]).Select(value => value!.GetValue<string>()));
            Assert.DoesNotContain("rate_card_version",
                Assert.IsType<JsonArray>(schema["required"]).Select(value => value!.GetValue<string>()));
        }
        var recoveryEvents = FindOperation(discovered, "readCheckpointRecoveryEvents");
        Assert.Equal("#/components/schemas/CheckpointRecoveryEventPage",
            recoveryEvents["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>());
        Assert.NotNull(recoveryEvents["responses"]?["200"]?["headers"]?["Cache-Control"]);
        Assert.NotNull(recoveryEvents["responses"]?["503"]?["content"]?["application/json"]?["schema"]?["properties"]?["error"]);
        var eventParameters = Assert.IsType<JsonArray>(recoveryEvents["parameters"]);
        var limitParameter = eventParameters.Single(value => value?["name"]?.GetValue<string>() == "limit");
        Assert.Equal(200, limitParameter?["schema"]?["maximum"]?.GetValue<int>());
        Assert.Equal(100, limitParameter?["schema"]?["default"]?.GetValue<int>());
        var eventStatus = Assert.IsType<JsonObject>(schemas["CheckpointRecoveryEventStatus"]);
        var requiredEventFields = Assert.IsType<JsonArray>(eventStatus["required"])
            .Select(value => value!.GetValue<string>()).ToArray();
        Assert.Contains("sourceCheckpointId", requiredEventFields);
        Assert.DoesNotContain("currentStep", requiredEventFields);

        foreach (string opId in new[]
        {
            "readPlatformByokProviderCredential",
            "readProjectByokProviderCredential",
        })
        {
            JsonObject statusOp = FindOperation(discovered, opId);
            Assert.Equal("#/components/schemas/ModelProviderCredentialStatusResponse",
                statusOp["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?
                    .GetValue<string>());
            Assert.NotNull(statusOp["responses"]?["200"]?["headers"]?["Cache-Control"]);
            Assert.True(statusOp["x-agentweaver-mcp-excluded"]?.GetValue<bool>());
            JsonObject statusResponses = Assert.IsType<JsonObject>(statusOp["responses"]);
            Assert.Contains("400", statusResponses.Select(item => item.Key));
            Assert.Contains("403", statusResponses.Select(item => item.Key));
            Assert.Contains("404", statusResponses.Select(item => item.Key));
        }

        JsonObject credStatusSchema = Assert.IsType<JsonObject>(schemas["ModelProviderCredentialStatusResponse"]);
        var requiredCredStatusFields = Assert.IsType<JsonArray>(credStatusSchema["required"])
            .Select(value => value!.GetValue<string>()).ToArray();
        Assert.Equal(
            ["scope", "projectId", "providerId", "providerRevision", "state", "credentialRevision"],
            requiredCredStatusFields);
        JsonObject credStatusProps = Assert.IsType<JsonObject>(credStatusSchema["properties"]);
        Assert.Equal(
            ["not_set", "enabled", "disabled", "pending", "quarantined"],
            Assert.IsType<JsonArray>(credStatusProps["state"]?["enum"])
                .Select(value => value!.GetValue<string>()).ToArray());
        Assert.DoesNotContain("decisionStateVersion", requiredEventFields);
        Assert.Equal("#/components/schemas/CheckpointRecoveryRequest",
            schemas["RecoverRunExecutionRequest"]?["properties"]?["checkpointRecovery"]?["anyOf"]?[0]?["$ref"]?.GetValue<string>());
        Assert.NotNull(schemas["CheckpointRecoveryStatus"]?["properties"]?["currentStep"]);
        Assert.NotNull(schemas["CheckpointRecoveryStatus"]?["properties"]?["recoveryMaterialMode"]);
        Assert.Equal("uuid", schemas["CheckpointRecoveryStatus"]?["properties"]?["operationId"]?
            ["anyOf"]?[0]?["format"]?.GetValue<string>());
        Assert.DoesNotContain("operationId",
            schemas["CheckpointRecoveryStatus"]?["required"]?.AsArray().Select(value => value!.GetValue<string>())
            ?? []);
        Assert.Equal("childJoin", schemas["CheckpointRecoveryStatus"]?["properties"]?["boundaryKind"]?
            ["anyOf"]?[0]?["enum"]?[0]?.GetValue<string>());
        Assert.Equal("boolean", schemas["CheckpointRecoveryStatus"]?["properties"]?["dispatchHeld"]?
            ["anyOf"]?[0]?["type"]?.GetValue<string>());
        Assert.Equal("string", schemas["CheckpointRecoveryStatus"]?["properties"]?["childManifestSha256"]?
            ["anyOf"]?[0]?["type"]?.GetValue<string>());
        Assert.NotNull(schemas["OwnerRunStatus"]?["properties"]?["checkpointRecovery"]?["anyOf"]?[0]?["$ref"]);
        Assert.NotNull(schemas["SessionStatusSnapshot"]?["properties"]?["checkpointRecovery"]?["anyOf"]?[0]?["$ref"]);
        Assert.Contains("503", Assert.IsType<JsonObject>(FindOperation(discovered, "readRunStatus")["responses"])
            .Select(item => item.Key));
        Assert.Contains("503", Assert.IsType<JsonObject>(FindOperation(discovered, "readSessionStatus")["responses"])
            .Select(item => item.Key));

        const string executionPlanPath =
            "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/execution-plan";
        JsonObject executionPlanPathItem = Assert.IsType<JsonObject>(discovered["paths"]?[executionPlanPath]);
        JsonObject executionPlanRoute = Assert.IsType<JsonObject>(executionPlanPathItem["get"]);
        JsonObject executionPlan = FindOperation(discovered, "readExecutionPlan");
        Assert.True(JsonNode.DeepEquals(executionPlanRoute, executionPlan));
        JsonObject executionPlanResponses = Assert.IsType<JsonObject>(executionPlan["responses"]);
        Assert.Contains("404", executionPlanResponses);
        Assert.Contains("409", executionPlanResponses);
        foreach (string status in new[] { "404", "409" })
        {
            Assert.NotNull(executionPlanResponses[status]?["content"]?["application/json"]?
                ["schema"]?["properties"]?["error"]);
        }
        Assert.Equal("#/components/schemas/CoordinationExecutionPlanSnapshot",
            executionPlan["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?.GetValue<string>());
        JsonObject executionPlanHeaders = Assert.IsType<JsonObject>(executionPlan["responses"]?["200"]?["headers"]);
        Assert.Contains("Cache-Control", executionPlanHeaders.Select(item => item.Key));
        Assert.Contains("not cacheable",
            executionPlanHeaders["Cache-Control"]?["description"]?.GetValue<string>());

        const string runEgressPath = "/api/v1/runs/{runId}/egress";
        JsonObject runEgressRoute = Assert.IsType<JsonObject>(
            discovered["paths"]?[runEgressPath]?["get"]);
        JsonObject runEgress = FindOperation(discovered, "readRunEgress");
        Assert.True(JsonNode.DeepEquals(runEgressRoute, runEgress));
        Assert.Null(runEgress["requestBody"]);
        Assert.DoesNotContain(
            Assert.IsType<JsonArray>(runEgress["parameters"]).OfType<JsonObject>(),
            parameter => parameter["in"]?.GetValue<string>() == "header" &&
                         parameter["name"]?.GetValue<string>() == "X-Agentweaver-Project-Authorization");
        JsonObject runEgressLimit = Assert.Single(
            Assert.IsType<JsonArray>(runEgress["parameters"]).OfType<JsonObject>(),
            parameter => parameter["name"]?.GetValue<string>() == "limit");
        Assert.Equal(1, runEgressLimit["schema"]?["minimum"]?.GetValue<int>());
        Assert.Equal(100, runEgressLimit["schema"]?["maximum"]?.GetValue<int>());
        Assert.Equal(50, runEgressLimit["schema"]?["default"]?.GetValue<int>());

        JsonObject getProjectConfig = FindOperation(discovered, "getProjectConfiguration");
        Assert.Equal("#/components/schemas/VersionedProjectConfiguration",
            getProjectConfig["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?
                .GetValue<string>());

        var platformDecisions = FindOperation(discovered, "listPlatformGovernanceDecisions");
        Assert.Equal("#/components/schemas/GovernanceDecisionHistoryPage",
            platformDecisions["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?
                .GetValue<string>());
        Assert.DoesNotContain("404", Assert.IsType<JsonObject>(platformDecisions["responses"]).Select(item => item.Key));
        var platformParams = Assert.IsType<JsonArray>(platformDecisions["parameters"]).OfType<JsonObject>().ToArray();
        Assert.True(platformParams.Single(p => p["name"]?.GetValue<string>() == "resourceType")["required"]?.GetValue<bool>());
        Assert.True(platformParams.Single(p => p["name"]?.GetValue<string>() == "resourceId")["required"]?.GetValue<bool>());
        Assert.Equal(100, platformParams.Single(p => p["name"]?.GetValue<string>() == "pageSize")["schema"]?["maximum"]?.GetValue<int>());
        Assert.Equal(1, platformParams.Single(p => p["name"]?.GetValue<string>() == "beforeSequence")["schema"]?["minimum"]?.GetValue<int>());

        var projectDecisions = FindOperation(discovered, "listProjectGovernanceDecisions");
        Assert.Equal("#/components/schemas/GovernanceDecisionHistoryPage",
            projectDecisions["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?
                .GetValue<string>());
        Assert.Contains("404", Assert.IsType<JsonObject>(projectDecisions["responses"]).Select(item => item.Key));
        var projectParams = Assert.IsType<JsonArray>(projectDecisions["parameters"]).OfType<JsonObject>().ToArray();
        Assert.True(projectParams.Single(p => p["name"]?.GetValue<string>() == "projectId")["required"]?.GetValue<bool>());
        Assert.True(projectParams.Single(p => p["name"]?.GetValue<string>() == "resourceType")["required"]?.GetValue<bool>());
        Assert.True(projectParams.Single(p => p["name"]?.GetValue<string>() == "resourceId")["required"]?.GetValue<bool>());
        Assert.Equal(100, projectParams.Single(p => p["name"]?.GetValue<string>() == "pageSize")["schema"]?["maximum"]?.GetValue<int>());
        Assert.Equal(1, projectParams.Single(p => p["name"]?.GetValue<string>() == "beforeSequence")["schema"]?["minimum"]?.GetValue<int>());
        Assert.Equal("int64", projectParams.Single(p => p["name"]?.GetValue<string>() == "beforeSequence")["schema"]?["format"]?.GetValue<string>());
        Assert.DoesNotContain(projectParams,
            p => p["in"]?.GetValue<string>() == "header" &&
                 p["name"]?.GetValue<string>() == "X-Agentweaver-Project-Authorization");
        Assert.Null(schemas["VersionedProjectConfiguration"]?["properties"]?["charterScan"]);

        JsonObject updateProjectConfig = FindOperation(discovered, "updateProjectConfiguration");
        Assert.Equal("#/components/schemas/UpdateProjectConfigurationRequest",
            updateProjectConfig["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?
                .GetValue<string>());
        Assert.Equal("#/components/schemas/UpdateProjectConfigurationResponse",
            updateProjectConfig["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["$ref"]?
                .GetValue<string>());
        JsonObject updateConfigRequest = Assert.IsType<JsonObject>(schemas["UpdateProjectConfigurationRequest"]);
        Assert.Equal("string", updateConfigRequest["properties"]?["scanCharterAgentId"]?["anyOf"]?[0]?["type"]?.GetValue<string>());
        Assert.Equal("null", updateConfigRequest["properties"]?["scanCharterAgentId"]?["anyOf"]?[1]?["type"]?.GetValue<string>());
        Assert.DoesNotContain("scanCharterAgentId",
            Assert.IsType<JsonArray>(updateConfigRequest["required"]).Select(value => value!.GetValue<string>()));
        JsonObject updateConfigResponse = Assert.IsType<JsonObject>(schemas["UpdateProjectConfigurationResponse"]);
        Assert.Equal("#/components/schemas/ProjectCharterScanResultV1",
            updateConfigResponse["properties"]?["charterScan"]?["$ref"]?.GetValue<string>());
        Assert.DoesNotContain("charterScan",
            Assert.IsType<JsonArray>(updateConfigResponse["required"]).Select(value => value!.GetValue<string>()));
        Assert.Equal(2048, CursorMaximum(runEgress));

        JsonObject executionPlanSchema =
            Assert.IsType<JsonObject>(schemas["CoordinationExecutionPlanSnapshot"]);
        Assert.Equal(
            "#/components/schemas/WorkPlanSnapshot",
            executionPlanSchema["properties"]?["workPlan"]?["$ref"]?.GetValue<string>());
        Assert.Equal(
            "#/components/schemas/CoordinationSavedPlanFingerprint",
            executionPlanSchema["properties"]?["savedPlanFingerprint"]?["$ref"]?.GetValue<string>());
        Assert.Contains("savedPlanFingerprint",
            executionPlanSchema["required"]!.AsArray().Select(value => value!.GetValue<string>()));
        JsonObject fingerprintSchema =
            Assert.IsType<JsonObject>(schemas["CoordinationSavedPlanFingerprint"]);
        Assert.Equal(1, fingerprintSchema["properties"]?["version"]?["maximum"]?.GetValue<int>());
        Assert.Equal("^[0-9a-f]{64}$",
            fingerprintSchema["properties"]?["sha256"]?["pattern"]?.GetValue<string>());
        var childSchema = Assert.IsType<JsonObject>(schemas["CoordinationExecutionChildSnapshot"]);
        Assert.Equal("#/components/schemas/CoordinationCompletedReceiptLink",
            childSchema["properties"]?["completedReceiptLink"]?["$ref"]?.GetValue<string>());
        Assert.DoesNotContain("completedReceiptLink",
            childSchema["required"]!.AsArray().Select(value => value!.GetValue<string>()));
        var linkSchema = Assert.IsType<JsonObject>(schemas["CoordinationCompletedReceiptLink"]);
        Assert.Equal(["messageId", "receiptHash"],
            linkSchema["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray());
        Assert.Equal("uuid", linkSchema["properties"]?["messageId"]?["format"]?.GetValue<string>());
        Assert.Equal("^[0-9a-f]{64}$",
            linkSchema["properties"]?["receiptHash"]?["pattern"]?.GetValue<string>());
        Assert.Equal(
            "#/components/schemas/CoordinationExecutionCheckpointSnapshot",
            executionPlanSchema["properties"]?["checkpoint"]?["anyOf"]?[0]?["$ref"]?.GetValue<string>());
        Assert.Equal(
            "#/components/schemas/CoordinationExecutionRecoverySnapshot",
            executionPlanSchema["properties"]?["recovery"]?["anyOf"]?[0]?["$ref"]?.GetValue<string>());

        JsonObject selectWorkflow = FindOperation(discovered, "selectWorkflow");
        Assert.Equal("#/components/schemas/SelectCoordinatorWorkflowRequest",
            selectWorkflow["requestBody"]?["content"]?["application/json"]?["schema"]?["$ref"]?
                .GetValue<string>());
        Assert.Equal("#/components/schemas/WorkflowDefinition",
            schemas["SelectCoordinatorWorkflowRequest"]?["properties"]?["proposedDefinition"]?
                ["anyOf"]?[0]?["$ref"]?.GetValue<string>());
        JsonArray workflowRequestStepRequired = Assert.IsType<JsonArray>(
            schemas["WorkflowStepDefinition"]?["required"]);
        Assert.DoesNotContain("buildTestCommand",
            workflowRequestStepRequired.Select(value => value!.GetValue<string>()));
        Assert.False(Assert.IsType<JsonObject>(schemas["WorkflowStepDefinition"]?["properties"])
            .ContainsKey("buildTestCommand"));

        JsonObject executionPlanWorkflowSchema =
            Assert.IsType<JsonObject>(schemas["ExecutionPlanWorkflowDefinition"]);
        Assert.Equal("#/components/schemas/ExecutionPlanWorkflowStepDefinition",
            executionPlanWorkflowSchema["properties"]?["steps"]?["items"]?["$ref"]?.GetValue<string>());
        JsonObject executionPlanWorkflowStepSchema =
            Assert.IsType<JsonObject>(schemas["ExecutionPlanWorkflowStepDefinition"]);
        Assert.Contains("buildTestCommand",
            Assert.IsType<JsonArray>(executionPlanWorkflowStepSchema["required"])
                .Select(value => value!.GetValue<string>()));
        JsonArray buildTestCommandSchema = Assert.IsType<JsonArray>(
            executionPlanWorkflowStepSchema["properties"]?["buildTestCommand"]?["anyOf"]);
        Assert.Equal("#/components/schemas/ExecutionPlanWorkflowBuildTestCommand",
            buildTestCommandSchema[0]?["$ref"]?.GetValue<string>());
        Assert.Equal("null", buildTestCommandSchema[1]?["type"]?.GetValue<string>());
        Assert.Equal("#/components/schemas/SandboxBuildTestWorkspaceSource",
            schemas["ExecutionPlanWorkflowBuildTestCommand"]?["properties"]?["workspaceSource"]?["$ref"]?
                .GetValue<string>());
        Assert.Equal(
            "#/components/schemas/ExecutionPlanWorkflowDefinition",
            schemas["ExecutionPlanWorkflowDefinitionSnapshot"]?["properties"]?["definition"]?["$ref"]?
                .GetValue<string>());

        JsonObject workspaceSourceSchema =
            Assert.IsType<JsonObject>(schemas["SandboxBuildTestWorkspaceSource"]);
        Assert.Equal(
            new[]
            {
                "contractVersion", "identity", "tenantId", "actorIssuer", "actorSubject",
                "sourceControlPinId", "acceptedSelectionHash", "sourceControlResource", "workspaceId",
                "workspaceIncarnationId", "branchName", "baseSha", "baselineTreeSha", "seedReference",
                "seedSha256", "seedByteLength", "bundleSha256", "bundleByteLength",
                "baselineManifestSha256", "baselineManifestByteLength",
            },
            Assert.IsType<JsonArray>(workspaceSourceSchema["required"])
                .Select(value => value!.GetValue<string>()));
        Assert.False(Assert.IsType<JsonObject>(workspaceSourceSchema["properties"])
            .ContainsKey("preparedRepositoryRelativePath"));
    }

    [Fact]
    public async Task BuiltApphostDiscoversTheEmittedGatewayArtifactOverControlledHttp()
    {
        string executable = ResolveCliApphost();
        Assert.True(File.Exists(executable), $"The built apphost does not exist: {executable}");

        await using var app = await StartGatewayAsync();
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (string argument in new[]
        {
            "api", "operations",
            "--gateway-url", address,
            "--broker-issuer", "https://issuer.example/",
            "--client-id", "cli-tests",
            "--api-audience", "agentweaver",
            "--tenant-id", "tenant-test",
        })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment.Remove("AGENTWEAVER_P1_SETUP_TOKEN");

        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string output = await stdout;
        string errors = await stderr;

        Assert.Equal(0, process.ExitCode);
        Assert.Empty(errors);
        JsonArray operations = Assert.IsType<JsonArray>(JsonNode.Parse(output));
        string[] operationIds = operations.OfType<JsonObject>()
            .Select(item => item["operationId"]?.GetValue<string>() ?? "")
            .ToArray();
        Assert.Contains("sendProjectRunMessage", operationIds);
        Assert.Contains("sendMessage", operationIds);
        Assert.Contains("readRunEgress", operationIds);
        Assert.Contains("listProjectGovernanceDecisions", operationIds);
    }

    [Fact]
    public async Task PublishedLinuxApphostReadsProjectSkillDecisionsOverHttpsWithBrokerAndSecretService()
    {
        if (!OperatingSystem.IsLinux() ||
            Environment.GetEnvironmentVariable("AGENTWEAVER_TEST_SECRET_SERVICE") != "1")
            return;

        string executable = Environment.GetEnvironmentVariable("AGENTWEAVER_TEST_PUBLISHED_APPHOST")
            ?? ResolveCliApphost();
        Assert.True(File.Exists(executable), $"The published apphost does not exist: {executable}");

        using var caKey = RSA.Create(2048);
        using var leafKey = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        var caRequest = new CertificateRequest(
            "CN=Agentweaver Test Root CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var caCert = caRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));

        var leafRequest = new CertificateRequest(
            "CN=127.0.0.1", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        leafRequest.CertificateExtensions.Add(san.Build(true));
        using var unsignedLeaf = leafRequest.Create(
            caCert, now.AddMinutes(-2), now.AddMinutes(30), Guid.NewGuid().ToByteArray());
        using var leafWithKey = unsignedLeaf.CopyWithPrivateKey(leafKey);

        string tempDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"aw-apphost-tls-{Guid.NewGuid():N}")).FullName;
        string caFile = Path.Combine(tempDir, "test-root.pem");
        await File.WriteAllTextAsync(caFile, caCert.ExportCertificatePem(), Encoding.ASCII);

        const string allowedProject = "0123456789abcdef0123456789abcdef";
        const string deniedProject = "fedcba9876543210fedcba9876543210";
        const string skillId = "Skill.Alpha/v1";
        string tenantId = $"fixture-tenant-{Guid.NewGuid():N}";
        string initialRefresh = $"fixture-refresh-1-{Guid.NewGuid():N}";
        string rotatedRefresh = $"fixture-refresh-2-{Guid.NewGuid():N}";
        string accessToken = $"fixture-access-{Guid.NewGuid():N}";
        var observedProjectAuthHeaders = new List<string>();
        var observedScopes = new List<string>();
        var requestTrace = new List<string>();

        var brokerBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        brokerBuilder.WebHost.UseKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(leafWithKey)));
        await using var brokerApp = brokerBuilder.Build();
        brokerApp.MapGet("/.well-known/openid-configuration", (HttpContext ctx) =>
        {
            requestTrace.Add("broker:discovery");
            string baseUri = $"https://127.0.0.1:{ctx.Connection.LocalPort}/";
            return Results.Json(new
            {
                issuer = baseUri,
                authorization_endpoint = baseUri + "connect/authorize",
                token_endpoint = baseUri + "connect/token",
                jwks_uri = baseUri + ".well-known/jwks.json",
            });
        });
        int refreshCounter = 0;
        var currentRefresh = initialRefresh;
        brokerApp.MapPost("/connect/token", async (HttpContext ctx) =>
        {
            requestTrace.Add("broker:token");
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            observedScopes.Add(form["scope"].ToString());
            string token = form["refresh_token"].ToString();
            if (form["grant_type"] != "refresh_token" || token != currentRefresh)
                return Results.BadRequest(new { error = "invalid_grant" });
            currentRefresh = $"fixture-refresh-{Interlocked.Increment(ref refreshCounter)}-{Guid.NewGuid():N}";
            return Results.Json(new
            {
                access_token = accessToken,
                refresh_token = currentRefresh,
                token_type = "Bearer",
                expires_in = 3600,
            });
        });
        await brokerApp.StartAsync();
        string brokerAddress = brokerApp.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        var gatewayBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GatewayEndpoints).Assembly.FullName,
            EnvironmentName = Environments.Development,
        });
        gatewayBuilder.WebHost.UseKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(leafWithKey)));
        gatewayBuilder.Services.AddSingleton<GatewayOwnerClient>(_ => null!);
        await using var gatewayApp = gatewayBuilder.Build();
        gatewayApp.MapGet("/openapi/v1.json", () =>
        {
            requestTrace.Add("gateway:openapi");
            var method = typeof(GatewayEndpoints).Assembly.GetType("Agentweaver.Gateway.GatewayOpenApi")!
                .GetMethod("CreateDocument", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var doc = method.Invoke(null, null);
            return Results.Json(doc, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        });
        gatewayApp.MapGet("/api/v1/authorization/context", (HttpContext ctx) =>
        {
            requestTrace.Add("gateway:auth-context");
            if (ctx.Request.Headers.Authorization.ToString() != $"Bearer {accessToken}" ||
                ctx.Request.Headers["X-Agentweaver-Tenant"].ToString() != tenantId)
                return Results.Unauthorized();
            return Results.Json(new
            {
                contractVersion = 1,
                issuer = brokerAddress + "/",
                actorId = "actor-1",
                tenantId,
                membershipRevision = 1,
                boundProjectId = (string?)null,
                boundRunId = (string?)null,
                effectiveAuthority = Array.Empty<object>(),
            });
        });
        gatewayApp.MapGet("/api/v1/policies/projects/{projectId}/decisions", (HttpContext ctx, string projectId) =>
        {
            requestTrace.Add($"gateway:decisions:{projectId}");
            if (ctx.Request.Headers.TryGetValue("X-Agentweaver-Project-Authorization", out var projAuth))
                observedProjectAuthHeaders.Add(projAuth.ToString());
            if (ctx.Request.Headers.Authorization.ToString() != $"Bearer {accessToken}" ||
                ctx.Request.Headers["X-Agentweaver-Tenant"].ToString() != tenantId)
                return Results.Unauthorized();
            if (projectId == deniedProject)
                return Results.Json(
                    new { code = "native_acs_forbidden", detail = "fixture-canary-secret" },
                    statusCode: 403,
                    contentType: "application/problem+json");
            if (ctx.Request.Query["resourceType"] != "skill" ||
                ctx.Request.Query["resourceId"] != skillId)
                return Results.BadRequest(new { code = "governance_decision_history_request_invalid" });

            var before = ctx.Request.Query["beforeSequence"].ToString();
            string hash1 = new('a', 64);
            string hash2 = new('b', 64);
            if (string.IsNullOrEmpty(before))
            {
                return Results.Json(new
                {
                    scopeType = "project",
                    scopeId = allowedProject,
                    resourceType = "skill",
                    resourceId = skillId,
                    items = new object[]
                    {
                        new
                        {
                            decisionId = "00000000-0000-0000-0000-000000000002",
                            sequence = 9_007_199_254_740_994L,
                            previousHash = hash1,
                            recordHash = hash2,
                            recordedAt = "2026-11-27T00:00:00Z",
                            decision = new
                            {
                                decisionType = "skill.trust.evidence.v1",
                                outcome = "pending",
                                approvalState = "pending",
                                policyRevision = (long?)null,
                                policyArtifactSha256 = (string?)null,
                                selectedRule = (string?)null,
                                interventionPoint = (string?)null,
                                requestId = (string?)null,
                                actionId = (string?)null,
                                runId = (string?)null,
                                agentId = (string?)null,
                                roleId = (string?)null,
                                ownerReceiptId = "receipt-2",
                                ownerReceiptPosition = (long?)null,
                                compilerGeneration = (string?)null,
                                applicationGeneration = (string?)null,
                                correlationId = "corr-2",
                                decidedAt = "2026-11-27T00:00:00Z",
                            },
                        },
                        new
                        {
                            decisionId = "00000000-0000-0000-0000-000000000001",
                            sequence = 9_007_199_254_740_993L,
                            previousHash = hash1,
                            recordHash = hash2,
                            recordedAt = "2026-11-27T00:00:00Z",
                            decision = new
                            {
                                decisionType = "skill.trust.evidence.v1",
                                outcome = "allow",
                                approvalState = "approved",
                                policyRevision = (long?)3,
                                policyArtifactSha256 = hash1,
                                selectedRule = "skill.allow",
                                interventionPoint = (string?)null,
                                requestId = (string?)null,
                                actionId = (string?)null,
                                runId = (string?)null,
                                agentId = (string?)null,
                                roleId = (string?)null,
                                ownerReceiptId = "receipt-1",
                                ownerReceiptPosition = (long?)1,
                                compilerGeneration = (string?)null,
                                applicationGeneration = (string?)null,
                                correlationId = "corr-1",
                                decidedAt = "2026-11-27T00:00:00Z",
                            },
                        },
                    },
                    nextBeforeSequence = 9_007_199_254_740_993L,
                });
            }
            Assert.Equal("9007199254740993", before);
            return Results.Json(new
            {
                scopeType = "project",
                scopeId = allowedProject,
                resourceType = "skill",
                resourceId = skillId,
                items = new object[]
                {
                    new
                    {
                        decisionId = "00000000-0000-0000-0000-000000000000",
                        sequence = 1L,
                        previousHash = new string('0', 64),
                        recordHash = hash1,
                        recordedAt = "2026-11-27T00:00:00Z",
                        decision = new
                        {
                            decisionType = "skill.trust.evidence.v1",
                            outcome = "pending",
                            approvalState = "pending",
                            policyRevision = (long?)null,
                            policyArtifactSha256 = (string?)null,
                            selectedRule = (string?)null,
                            interventionPoint = (string?)null,
                            requestId = (string?)null,
                            actionId = (string?)null,
                            runId = (string?)null,
                            agentId = (string?)null,
                            roleId = (string?)null,
                            ownerReceiptId = "receipt-0",
                            ownerReceiptPosition = (long?)null,
                            compilerGeneration = (string?)null,
                            applicationGeneration = (string?)null,
                            correlationId = "corr-0",
                            decidedAt = "2026-11-27T00:00:00Z",
                        },
                    },
                },
                nextBeforeSequence = (long?)null,
            });
        });
        await gatewayApp.StartAsync();
        string gatewayAddress = gatewayApp.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();

        var authIdentity = CliAuthIdentity.Create(new CliIdentity(
            new CliTarget(
                new Uri(gatewayAddress + "/"),
                new Uri(brokerAddress + "/"),
                "agentweaver-cli",
                "agentweaver",
                tenantId),
            ["openid", "api.read", "offline_access"]));

        var vault = new LinuxSecretServiceVault();
        vault.Delete(authIdentity.Account);
        vault.Set(authIdentity.Account, initialRefresh);
        try
        {
            async Task<(int ExitCode, string Stdout, string Stderr)> RunApphostAsync(params string[] extraArgs)
            {
                using Process process = new()
                {
                    StartInfo = new ProcessStartInfo(executable)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    },
                };
                process.StartInfo.Environment["SSL_CERT_FILE"] = caFile;
                foreach (string arg in new[]
                {
                    "policy", "decisions",
                    "--scope", "project",
                    "--resource-type", "skill",
                    "--resource-id", skillId,
                    "--gateway-url", gatewayAddress,
                    "--broker-issuer", brokerAddress + "/",
                    "--client-id", "agentweaver-cli",
                    "--api-audience", "agentweaver",
                    "--tenant-id", tenantId,
                }.Concat(extraArgs))
                    process.StartInfo.ArgumentList.Add(arg);

                Assert.True(process.Start());
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                return (process.ExitCode, await stdout, await stderr);
            }

            var page1 = await RunApphostAsync("--project-id", allowedProject, "--page-size", "2");
            Assert.True(page1.ExitCode == 0, $"Exit={page1.ExitCode} Trace=[{string.Join(",", requestTrace)}] Stderr={page1.Stderr} Stdout={page1.Stdout}");
            Assert.Empty(page1.Stderr);
            var json1 = JsonNode.Parse(page1.Stdout)!.AsObject();
            Assert.Equal(9_007_199_254_740_993L, json1["nextBeforeSequence"]!.GetValue<long>());
            Assert.Equal("pending", json1["items"]![0]!["decision"]!["outcome"]!.GetValue<string>());
            Assert.Null(json1["items"]![0]!["decision"]!["policyRevision"]);

            var page2 = await RunApphostAsync(
                "--project-id", allowedProject, "--page-size", "2", "--before-sequence", "9007199254740993");
            Assert.Equal(0, page2.ExitCode);
            Assert.Empty(page2.Stderr);
            var json2 = JsonNode.Parse(page2.Stdout)!.AsObject();
            Assert.Single(json2["items"]!.AsArray());
            Assert.Null(json2["nextBeforeSequence"]);

            var denied = await RunApphostAsync("--project-id", deniedProject, "--page-size", "2");
            Assert.Equal(1, denied.ExitCode);
            Assert.Empty(denied.Stdout);
            Assert.Contains("HTTP 403", denied.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-canary-secret", denied.Stderr, StringComparison.Ordinal);

            Assert.Empty(observedProjectAuthHeaders);
            Assert.All(observedScopes, s => Assert.DoesNotContain("projects.admin", s, StringComparison.Ordinal));
        }
        finally
        {
            vault.Delete(authIdentity.Account);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string ResolveCliApphost()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Agentweaver.slnx")))
            root = root.Parent;
        Assert.NotNull(root);

        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        string apphost = OperatingSystem.IsWindows() ? "agentweaver.exe" : "agentweaver";
        return Path.Combine(root!.FullName, "tools", "Agentweaver.Cli", "bin", configuration, "net10.0", apphost);
    }

    private static JsonObject FindOperation(JsonObject document, string operationId)
    {
        JsonObject paths = Assert.IsType<JsonObject>(document["paths"]);
        foreach (JsonObject path in paths.Select(entry => entry.Value).OfType<JsonObject>())
        foreach (JsonObject operation in path.Select(entry => entry.Value).OfType<JsonObject>())
        {
            if (operation["operationId"]?.GetValue<string>() == operationId)
                return operation;
        }
        throw new Xunit.Sdk.XunitException($"The emitted Gateway artifact has no {operationId} operation.");
    }

    private static int CursorMaximum(JsonObject operation)
    {
        JsonArray parameters = Assert.IsType<JsonArray>(operation["parameters"]);
        JsonObject cursor = Assert.Single(
            parameters.OfType<JsonObject>(),
            parameter => parameter["name"]?.GetValue<string>() == "cursor");
        return cursor["schema"]?["maxLength"]?.GetValue<int>()
            ?? throw new Xunit.Sdk.XunitException("The cursor parameter has no maximum length.");
    }

    internal static async Task<WebApplication> StartGatewayAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(GatewayEndpoints).Assembly.FullName,
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<GatewayOwnerClient>(_ => null!);
        builder.Services.AddCors(options => options.AddPolicy(
            GatewayWebCors.OpenApiPolicyName,
            policy => policy.AllowAnyOrigin().WithMethods("GET").WithHeaders("Accept")));
        var app = builder.Build();
        app.UseCors();
        app.MapGatewayEndpoints();
        await app.StartAsync();
        return app;
    }
}
