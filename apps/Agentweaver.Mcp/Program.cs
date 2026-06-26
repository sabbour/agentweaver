namespace Agentweaver.Mcp;

using Agentweaver.Mcp.Tools;

internal sealed class McpProgram
{
    public static async Task<int> Main(string[] args)
    {
        var useStdio = args.Contains("--stdio");

        var builder = WebApplication.CreateBuilder(args);

        // Fix 1 (Seraph T4–T7 review): in HTTP (Resource Server) mode, the issuer/audience used to
        // validate forwarded AS JWTs must be pinned to the PUBLIC host — NOT derived from the request
        // host, which behind the gateway/internal routing would not match the token's aud
        // (https://<HOST>/mcp). Fail fast at boot in Production if they are not configured. Stdio mode
        // is single-user/local and performs no JWT validation, so it is exempt.
        if (!useStdio && builder.Environment.IsProduction())
        {
            var missing = new[] { "Auth:Mcp:Issuer", "Auth:Mcp:Audience" }
                .Where(key => string.IsNullOrWhiteSpace(builder.Configuration[key]))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    "Refusing to start: the following OAuth configuration value(s) must be pinned to " +
                    $"the public host in Production but are unset/empty: {string.Join(", ", missing)}. " +
                    "Set Auth:Mcp:Issuer = https://<HOST> and Auth:Mcp:Audience = https://<HOST>/mcp. " +
                    "Host-derived issuer/audience is permitted only in Development; in Production it " +
                    "would break validation of forwarded AS access tokens (audience mismatch).");
            }
        }

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
            // Stateless mode handles each request in its own HTTP scope, so the
            // inbound HttpContext (and the caller's Bearer token captured by
            // McpBearerTokenMiddleware) flows into tool execution. In the default
            // stateful transport, tool methods run on the session message loop
            // detached from the HTTP request, leaving IHttpContextAccessor.HttpContext
            // null during tool calls — which caused the backend API to receive an
            // empty bearer and reject every tool invocation with 401.
            mcpBuilder.WithHttpTransport(o => o.Stateless = true);

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
