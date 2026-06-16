using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Scaffolder.Mcp;
using Scaffolder.Mcp.Tools;

var apiUrl = Environment.GetEnvironmentVariable("SCAFFOLDER_API_URL") ?? "http://localhost:5000";
var apiKey = Environment.GetEnvironmentVariable("SCAFFOLDER_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("SCAFFOLDER_API_KEY environment variable is required.");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);
var mcpConfig = new McpConfig(apiUrl, apiKey);
builder.Services.AddSingleton(mcpConfig);
builder.Services.AddSingleton(_ =>
{
    var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    return new ScaffolderApiClient(http, mcpConfig);
});
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
await app.RunAsync();
return 0;
