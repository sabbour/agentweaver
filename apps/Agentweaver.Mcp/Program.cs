namespace Agentweaver.Mcp;

using Agentweaver.Mcp.Tools;

internal sealed class McpProgram
{
    public static async Task<int> Main(string[] args)
    {
        var useStdio = args.Contains("--stdio");

        var builder = WebApplication.CreateBuilder(args);

        var apiUrl = builder.Configuration["Agentweaver:ApiUrl"]
            ?? Environment.GetEnvironmentVariable("AGENTWEAVER_API_URL")
            ?? "http://localhost:5000";
        var apiKey = builder.Configuration["Agentweaver:ApiKey"]
            ?? Environment.GetEnvironmentVariable("AGENTWEAVER_API_KEY")
            ?? string.Empty;

        // In stdio mode, the MCP server forwards the caller's own Bearer token to the backend.
        // A shared API key is no longer required.

        var mcpConfig = new McpConfig(apiUrl, apiKey);
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
        builder.Services.AddSingleton<McpApiKeyRegistry>();
        builder.Services.AddSingleton<McpAccessTokenValidator>();

        var mcpBuilder = builder.Services.AddMcpServer().WithToolsFromAssembly();

        if (useStdio)
            mcpBuilder.WithStdioServerTransport();
        else
            mcpBuilder.WithHttpTransport();

        var app = builder.Build();

        if (!useStdio)
        {
            app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

            // RFC 9728 §3a — Protected Resource Metadata. Served unauthenticated so MCP clients can
            // discover the Authorization Server. Both the root and the resource-suffixed path are
            // served because clients (Copilot CLI / VS Code) probe the suffixed form.
            var protectedResourceMetadata = (HttpContext ctx) =>
            {
                var configuredIssuer = ctx.RequestServices
                    .GetRequiredService<IConfiguration>()["Auth:Mcp:Issuer"];
                var issuer = !string.IsNullOrWhiteSpace(configuredIssuer)
                    ? configuredIssuer.TrimEnd('/')
                    : $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";

                return Results.Json(new Dictionary<string, object>
                {
                    ["resource"] = $"{issuer}/mcp",
                    ["authorization_servers"] = new[] { issuer },
                    ["bearer_methods_supported"] = new[] { "header" },
                    ["scopes_supported"] = new[] { "mcp:invoke" },
                    ["resource_documentation"] = $"{issuer}/docs",
                });
            };
            app.MapGet("/.well-known/oauth-protected-resource", protectedResourceMetadata);
            app.MapGet("/.well-known/oauth-protected-resource/mcp", protectedResourceMetadata);

            app.UseMiddleware<McpBearerTokenMiddleware>();
        }

        app.MapMcp("/mcp");

        await app.RunAsync();
        return 0;
    }
}
