using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

/// <summary>
/// Exercises owner HTTP validation on Skill refresh routes that completes before opening PostgreSQL.
/// </summary>
public sealed class SkillRefreshEndpointValidationTests : IAsyncLifetime
{
    private const string Issuer = "https://broker.test/";
    private const string RefreshInputsPath = "/api/projects/project-1/runs/run-1/agents/agent-1/skills/refresh-inputs";
    private const string RefreshPath = "/api/projects/project-1/runs/run-1/agents/agent-1/skills/refresh";
    private const string ActorClassHeader = "X-Test-Actor-Class";
    private const string AuthHeader = "X-Test-Auth";

    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(
        "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1");
    private IHost _host = null!;
    private TestServer _server = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(HeaderAuthHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.SchemeName, _ => { });
                    services.AddAuthorization();
                    services.AddDbContextFactory<ProjectsConfigDbContext>(options => options.UseNpgsql(_dataSource));
                    services.AddScoped(provider =>
                        provider.GetRequiredService<IDbContextFactory<ProjectsConfigDbContext>>().CreateDbContext());
                    services.AddSingleton(Assert.IsType<Agentweaver.Providers.ProviderCatalog>(
                        Agentweaver.Providers.ProviderCatalog.Create([], [], []).Value));
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(new ProjectsConfigIdentityOptions(Issuer));
                    services.AddScoped<ProjectAuthorizationOwner>();
                    services.AddScoped<ProjectsConfigService>();
                    services.AddScoped<SkillContentService>(provider => new SkillContentService(
                        provider.GetRequiredService<ProjectsConfigDbContext>(),
                        objects: null,
                        provider.GetRequiredService<ProjectsConfigService>(),
                        provider.GetRequiredService<TimeProvider>()));
                    services.AddScoped<ISkillContentService>(provider =>
                        provider.GetRequiredService<SkillContentService>());
                    services.AddScoped<ISkillAssignmentService>(provider =>
                        provider.GetRequiredService<SkillContentService>());
                    services.AddScoped<ProjectGovernanceDecisionStore>();
                    services.AddScoped<ProjectSkillTrustEvidenceService>();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSkillContentEndpoints());
                });
            })
            .StartAsync();
        _server = _host.GetTestServer();
        _client = _server.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected()
    {
        using var getResponse = await _client.GetAsync(RefreshInputsPath);
        Assert.Equal(HttpStatusCode.Unauthorized, getResponse.StatusCode);

        using var postResponse = await _client.PostAsync(
            RefreshPath,
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, postResponse.StatusCode);
    }

    [Fact]
    public async Task Bare_query_string_is_rejected_with_400_and_no_store_on_both_routes()
    {
        var getContext = await _server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = RefreshInputsPath;
            context.Request.QueryString = new QueryString("?");
            context.Request.Headers[AuthHeader] = "true";
            context.Request.Headers[ActorClassHeader] = "human";
        });
        Assert.Equal(StatusCodes.Status400BadRequest, getContext.Response.StatusCode);
        Assert.Contains("no-store", getContext.Response.Headers.CacheControl.ToString(), StringComparison.OrdinalIgnoreCase);

        var postContext = await _server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = RefreshPath;
            context.Request.QueryString = new QueryString("?");
            context.Request.Headers[AuthHeader] = "true";
            context.Request.Headers[ActorClassHeader] = "human";
            context.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes("{}");
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
        });
        Assert.Equal(StatusCodes.Status400BadRequest, postContext.Response.StatusCode);
        Assert.Contains("no-store", postContext.Response.Headers.CacheControl.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("human,human")]
    public async Task Invalid_or_duplicate_actor_class_is_rejected_with_403_and_no_store(string actorClassHeader)
    {
        using var getRequest = new HttpRequestMessage(HttpMethod.Get, RefreshInputsPath);
        getRequest.Headers.Add(AuthHeader, "true");
        foreach (var value in actorClassHeader.Split(','))
            getRequest.Headers.Add(ActorClassHeader, value);
        using var getResponse = await _client.SendAsync(getRequest);
        Assert.Equal(HttpStatusCode.Forbidden, getResponse.StatusCode);
        Assert.True(getResponse.Headers.CacheControl?.NoStore);

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, RefreshPath)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        postRequest.Headers.Add(AuthHeader, "true");
        foreach (var value in actorClassHeader.Split(','))
            postRequest.Headers.Add(ActorClassHeader, value);
        using var postResponse = await _client.SendAsync(postRequest);
        Assert.Equal(HttpStatusCode.Forbidden, postResponse.StatusCode);
        Assert.True(postResponse.Headers.CacheControl?.NoStore);
    }

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "TestHeader";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(AuthHeader))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new("sub", "11111111-1111-4111-8111-111111111111"),
                new("scope", $"{ProjectAuthorizationOwner.ApiReadScope} {ProjectAuthorizationOwner.ProjectAdminScope}"),
            };
            if (Request.Headers.TryGetValue(ActorClassHeader, out var values))
            {
                foreach (var value in values)
                    claims.Add(new Claim("aw_actor_class", value!));
            }
            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
