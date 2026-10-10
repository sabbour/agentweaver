extern alias WebHost;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class WebHostCallbackTests
{
    [Theory]
    [InlineData("https://web.test")]
    [InlineData("https://attacker.test")]
    [InlineData("null")]
    public async Task OAuthCallbackServesTheScrubbedAppShellWithoutAQueryRelayOrCors(string origin)
    {
        using var factory = new WebApplicationFactory<WebHost::Program>()
            .WithWebHostBuilder(builder =>
                builder.UseWebRoot(Path.Combine(FindRepositoryRoot(), "apps", "web")));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://web.test")
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/auth/callback?code=must-not-be-embedded&state=state-value");
        request.Headers.Add("Origin", origin);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Contains(
            "window.__AGENTWEAVER_IDENTITY_CALLBACK__ = window.location.search;",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.history.replaceState(null, '', identityCallbackPath);",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("returnUrl.search", body, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-be-embedded", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeConfigurationPublishesTheConfiguredBrokerIssuer()
    {
        const string issuer = "https://identity.example.test/";
        using var factory = new WebApplicationFactory<WebHost::Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseWebRoot(Path.Combine(FindRepositoryRoot(), "apps", "web"));
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["VITE_IDENTITY_BROKER_ISSUER"] = issuer,
                    }));
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://web.test")
        });

        var body = await client.GetStringAsync("/env-config.js");
        var jsonStart = body.IndexOf('{');
        var jsonEnd = body.LastIndexOf('}');
        using var configuration = JsonDocument.Parse(body[jsonStart..(jsonEnd + 1)]);

        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(issuer)),
            configuration.RootElement.GetProperty("IDENTITY_BROKER_ISSUER").GetString());
    }

    private static string FindRepositoryRoot()
    {
        foreach (var startingPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startingPath); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "apps", "web", "package.json")))
                    return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root for the Web host test.");
    }
}
