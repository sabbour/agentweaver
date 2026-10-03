using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Agentweaver.Identity.Broker.Tests;

internal sealed class ResourceServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    private ResourceServer(IHost host)
    {
        _host = host;
        _client = host.GetTestClient();
        _client.BaseAddress = new Uri("https://resource.test");
    }

    public static async Task<ResourceServer> StartAsync(SecurityKey signingKey, string audience = "https://api.test")
    {
        var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
                services.AddAuthorization();
                services.AddOpenIddict().AddValidation(options =>
                {
                    options.SetIssuer(IdentityBrokerWebApplicationFactory.Issuer);
                    options.AddAudiences(audience);
                    options.Configure(configuration => configuration.Configuration = new OpenIddictConfiguration
                    {
                        Issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer),
                        SigningKeys = { signingKey },
                    });
                    options.UseAspNetCore();
                });
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapGet("/resource",
                    () => "authorized").RequireAuthorization());
            });
        }).StartAsync();
        return new ResourceServer(host);
    }

    public Task<HttpResponseMessage> CallAsync(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/resource");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
