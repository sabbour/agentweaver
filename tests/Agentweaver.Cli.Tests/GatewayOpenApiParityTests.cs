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
        JsonObject createProject = FindOperation(discovered, "createProject");
        Assert.Contains("503", Assert.IsType<JsonObject>(createProject["responses"]).Select(item => item.Key));
        Assert.Contains("role_assignment_unavailable",
            createProject["responses"]?["503"]?["description"]?.GetValue<string>());
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
