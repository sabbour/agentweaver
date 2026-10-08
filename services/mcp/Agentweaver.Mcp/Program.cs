using Agentweaver.Abstractions;
using Agentweaver.Mcp;
using Agentweaver.Telemetry;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenIddict.Validation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
var options = McpServiceOptions.Read(builder.Configuration);

builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);
builder.Logging.AddFilter("OpenIddict.Validation.OpenIddictValidationDispatcher", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
builder.Services.AddSingleton(options);
builder.Services.AddAgentweaverTelemetry("agentweaver.mcp");
builder.Services.AddHttpClient<GatewayApiClient>(client =>
    {
        client.BaseAddress = new Uri(options.GatewayBaseAddress.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        client.Timeout = McpServiceOptions.GatewayRequestTimeout;
    })
    .ConfigurePrimaryHttpMessageHandler(CreateGatewayHandler);
builder.Services.AddHttpClient<GatewayToolCatalog>(client =>
    {
        client.BaseAddress = options.GatewayBaseAddress;
        client.Timeout = McpServiceOptions.GatewayRequestTimeout;
    })
    .ConfigurePrimaryHttpMessageHandler(CreateGatewayHandler);
builder.Services.AddTransient<McpToolHandlers>();
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddOpenIddict().AddValidation(validation =>
{
    validation.SetIssuer(options.IdentityIssuer);
    validation.AddAudiences(options.IdentityAudience);
    validation.UseSystemNetHttp();
    validation.UseAspNetCore();
});
builder.Services.AddAuthorization();
builder.Services.AddMcpServer(server =>
{
    server.ServerInfo = new Implementation { Name = "Agentweaver MCP", Version = "0.1.0" };
    server.Capabilities = new ServerCapabilities
    {
        Tools = new ToolsCapability
        {
            ListToolsHandler = (request, cancellationToken) =>
                GetToolHandlers(request.Services).ListAsync(cancellationToken),
        },
    };
}).WithHttpTransport(transport =>
{
    transport.Stateless = true;
    transport.ConfigureSessionOptions = (context, server, _) =>
    {
        var tools = server.Capabilities?.Tools
            ?? throw new InvalidOperationException("The MCP tools capability is unavailable.");
        tools.CallToolHandler = (request, cancellationToken) =>
            GetToolHandlers(request.Services).CallAsync(
                request.Params ?? throw new InvalidOperationException("MCP call parameters are missing."),
                context,
                cancellationToken);
        return Task.CompletedTask;
    };
});

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/mcp")
    {
        context.Response.OnStarting(() =>
        {
            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                context.Response.Headers.Append(
                    "WWW-Authenticate",
                    $"Bearer resource_metadata=\"{options.ProtectedResourceMetadataUri.AbsoluteUri}\"");
            return Task.CompletedTask;
        });
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapMcp("/mcp").RequireAuthorization()
    .WithMetadata(new RequestSizeLimitAttribute(
        KnowledgeRecordTransferContract.MaximumBytes + 64 * 1024));
app.MapGet(options.ProtectedResourceMetadataPath, () => Results.Json(new
{
    resource = options.IdentityAudience,
    authorization_servers = new[] { options.IdentityIssuer.AbsoluteUri },
}));
app.Run();

static HttpMessageHandler CreateGatewayHandler() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
};

static McpToolHandlers GetToolHandlers(IServiceProvider? services) =>
    services?.GetRequiredService<McpToolHandlers>()
    ?? throw new InvalidOperationException("MCP request services are unavailable.");

public partial class Program;
