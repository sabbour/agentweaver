namespace Agentweaver.Mcp;

using Agentweaver.Mcp.Tools;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Validation.AspNetCore;

public sealed class McpProgram
{
    public static async Task<int> Main(string[] args)
    {
        var useStdio = args.Contains("--stdio");

        var builder = WebApplication.CreateBuilder(args);

        var apiUrl = builder.Configuration["Agentweaver:ApiUrl"]
            ?? Environment.GetEnvironmentVariable("AGENTWEAVER_API_URL")
            ?? "http://localhost:5000";
        var brokerToken = builder.Configuration["Agentweaver:Token"]
            ?? Environment.GetEnvironmentVariable("AGENTWEAVER_TOKEN")
            ?? string.Empty;

        if (useStdio && string.IsNullOrWhiteSpace(brokerToken))
        {
            Console.Error.WriteLine(
                "[agentweaver-mcp] ERROR: AGENTWEAVER_TOKEN must contain an Agentweaver broker " +
                "token with the mcp:invoke scope in stdio mode.");
            return 1;
        }

        var oauth = useStdio
            ? null
            : McpOAuthConfiguration.Resolve(
                builder.Configuration, builder.Environment, apiUrl);
        var mcpConfig = new McpConfig(apiUrl, brokerToken);
        if (oauth is not null)
            builder.Services.AddSingleton(oauth);
        builder.Services.AddSingleton(mcpConfig);
        builder.Services.AddSingleton(sp =>
        {
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var accessor = sp.GetService<IHttpContextAccessor>();
            return new AgentweaverApiClient(http, mcpConfig, accessor);
        });

        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient();
        builder.Services.AddHttpContextAccessor();
        if (!useStdio)
        {
            builder.Services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = McpBrokerAuthenticationDefaults.Scheme;
                    options.DefaultChallengeScheme = McpBrokerAuthenticationDefaults.Scheme;
                })
                .AddScheme<AuthenticationSchemeOptions, McpBrokerAuthenticationHandler>(
                    McpBrokerAuthenticationDefaults.Scheme, _ => { });
            builder.Services.AddAuthorization(options =>
                options.AddPolicy(
                    McpBrokerAuthenticationDefaults.Policy,
                    policy => policy
                        .AddAuthenticationSchemes(McpBrokerAuthenticationDefaults.Scheme)
                        .RequireAuthenticatedUser()));
            builder.Services.AddOpenIddict()
                .AddValidation(options =>
                {
                    options.SetIssuer(oauth!.Issuer);
                    options.AddAudiences(oauth.Resource.AbsoluteUri);
                    options.UseSystemNetHttp();
                    options.UseAspNetCore();
                });
        }

        var mcpBuilder = builder.Services.AddMcpServer().WithToolsFromAssembly();

        if (useStdio)
            mcpBuilder.WithStdioServerTransport();
        else
            // Stateless mode handles each request in its own HTTP scope, so the
            // inbound HttpContext (and the caller's Bearer token captured by
            // broker-authenticated request token) flows into tool execution. In the default
            // stateful transport, tool methods run on the session message loop
            // detached from the HTTP request, leaving IHttpContextAccessor.HttpContext
            // null during tool calls — which caused the backend API to receive an
            // empty bearer and reject every tool invocation with 401.
            mcpBuilder.WithHttpTransport(o => o.Stateless = true);

        var app = builder.Build();

        if (!useStdio)
        {
            app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
            app.MapGet("/.well-known/oauth-protected-resource", () => ProtectedResourceMetadata(oauth!));
            app.MapGet("/.well-known/oauth-protected-resource/mcp", () => ProtectedResourceMetadata(oauth!));
            app.UseAuthentication();
            app.UseAuthorization();
        }

        var mcp = app.MapMcp("/mcp");
        if (!useStdio)
            mcp.RequireAuthorization(McpBrokerAuthenticationDefaults.Policy);

        await app.RunAsync();
        return 0;
    }

    private static IResult ProtectedResourceMetadata(McpOAuthConfiguration configuration) =>
        Results.Json(new
        {
            resource = configuration.Resource.AbsoluteUri,
            authorization_servers = new[] { configuration.Issuer.AbsoluteUri },
            scopes_supported = new[] { McpOAuthConfiguration.RequiredScope },
        });
}
