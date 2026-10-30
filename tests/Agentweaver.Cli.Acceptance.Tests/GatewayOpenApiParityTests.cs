using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Net;
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
using Xunit;

namespace Agentweaver.Cli.Acceptance.Tests;

public sealed class GatewayOpenApiParityTests
{
    [Fact]
    public async Task CliDiscoveryParsesEmittedGatewayDocumentAndJournalGateRejectsContractMutation()
    {
        await using var app = await StartGatewayAsync();
        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using HttpResponseMessage response = await http.GetAsync(new Uri(new Uri(address), "/openapi/v1.json"));
        string artifact = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Gateway OpenAPI returned {(int)response.StatusCode}: {artifact}");
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
            "acceptance-tests",
            "agentweaver",
            "tenant-test");
        var api = new GatewayClient(http, new CliIdentity(target, ["openid", "api.read", "offline_access"]));

        JsonObject discovered = await api.DiscoverAsync(CancellationToken.None);
        Assert.True(JsonNode.DeepEquals(emitted, discovered));
        ApiAcceptanceFixtureSetup.ValidateJournalMessageOperation(discovered);

        JsonObject mutatedBody = (JsonObject)discovered.DeepClone();
        JsonObject message = FindOperation(mutatedBody, "sendProjectRunMessage");
        message["requestBody"]!["required"] = false;
        AcceptanceBlockedException bodyFailure = Assert.Throws<AcceptanceBlockedException>(
            () => ApiAcceptanceFixtureSetup.ValidateJournalMessageOperation(mutatedBody));
        Assert.Contains("required owner contract", bodyFailure.Message, StringComparison.Ordinal);

        JsonObject mutatedHeader = (JsonObject)discovered.DeepClone();
        message = FindOperation(mutatedHeader, "sendProjectRunMessage");
        JsonArray parameters = message["parameters"] as JsonArray ?? [];
        JsonObject idempotencyKey = Assert.Single(parameters.OfType<JsonObject>(), parameter =>
            parameter["in"]?.GetValue<string>() == "header" &&
            parameter["name"]?.GetValue<string>() == "Idempotency-Key");
        idempotencyKey["required"] = false;
        AcceptanceBlockedException headerFailure = Assert.Throws<AcceptanceBlockedException>(
            () => ApiAcceptanceFixtureSetup.ValidateJournalMessageOperation(mutatedHeader));
        Assert.Contains("required owner contract", headerFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfiguredApphostDiscoversEmittedGatewayArtifactOverControlledHttp()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("AGENTWEAVER_CLI_PATH");
        Assert.False(string.IsNullOrWhiteSpace(configuredPath),
            "Set AGENTWEAVER_CLI_PATH to the exact built agentweaver apphost for process integration tests.");
        string executable = Path.GetFullPath(configuredPath!);
        Assert.True(File.Exists(executable), $"AGENTWEAVER_CLI_PATH does not exist: {executable}");
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
            "--client-id", "acceptance-tests",
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
