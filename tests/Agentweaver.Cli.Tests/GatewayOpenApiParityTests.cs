using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Agentweaver.Cli;
using Agentweaver.Cli.Api;
using Agentweaver.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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

    private static async Task<WebApplication> StartGatewayAsync()
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
