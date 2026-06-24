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
            ?? Environment.GetEnvironmentVariable("AGENTWEAVER_API_KEY");

        if (useStdio && string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("AGENTWEAVER_API_KEY is required.");
            return 1;
        }

        var mcpConfig = new McpConfig(apiUrl, apiKey ?? string.Empty);
        builder.Services.AddSingleton(mcpConfig);
        builder.Services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            return new AgentweaverApiClient(http, mcpConfig);
        });

        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<McpApiKeyRegistry>();

        var mcpBuilder = builder.Services.AddMcpServer().WithToolsFromAssembly();

        if (useStdio)
            mcpBuilder.WithStdioServerTransport();
        else
            mcpBuilder.WithHttpTransport();

        var app = builder.Build();

        if (!useStdio)
            app.UseMiddleware<McpBearerTokenMiddleware>();

        app.MapMcp("/mcp");

        await app.RunAsync();
        return 0;
    }
}
