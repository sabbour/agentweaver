using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
var oauthRedirectUri = builder.Configuration["VITE_OAUTH_REDIRECT_URI"] ?? string.Empty;
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.StaticFiles", LogLevel.Warning);

var app = builder.Build();
var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var indexPath = Path.Combine(webRoot, "index.html");

// Apply baseline browser protections to both static content and application endpoints.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next(context);
});

// Serve deployment-specific public settings without caching; values are base64 encoded so
// configuration text cannot become executable JavaScript when embedded in the response.
app.Use(async (context, next) =>
{
    if (context.Request.Path.Value != "/env-config.js")
    {
        await next(context);
        return;
    }

    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.ContentType = "application/javascript; charset=utf-8";
    if (HttpMethods.IsHead(context.Request.Method)) return;
    if (!HttpMethods.IsGet(context.Request.Method))
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        return;
    }

    var gatewayUrl = Environment.GetEnvironmentVariable("VITE_GATEWAY_URL");
    var values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GATEWAY_URL"] = string.IsNullOrEmpty(gatewayUrl) ? "/api/v1" : gatewayUrl,
        ["IDENTITY_BROKER_URL"] = builder.Configuration["VITE_IDENTITY_BROKER_URL"] ?? string.Empty,
        ["IDENTITY_BROKER_ISSUER"] = builder.Configuration["VITE_IDENTITY_BROKER_ISSUER"] ?? string.Empty,
        ["OAUTH_CLIENT_ID"] = Environment.GetEnvironmentVariable("VITE_OAUTH_CLIENT_ID") ?? string.Empty,
        ["OAUTH_REDIRECT_URI"] = oauthRedirectUri,
        ["OAUTH_SCOPES"] = Environment.GetEnvironmentVariable("VITE_OAUTH_SCOPES") ?? string.Empty,
    };
    var encoded = values.ToDictionary(
        pair => pair.Key,
        pair => Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value)),
        StringComparer.Ordinal);

    await context.Response.WriteAsync(
        $"window.__AGENTWEAVER_CONFIG_BASE64__ = {JsonSerializer.Serialize(encoded)};\n",
        context.RequestAborted);
});

// Cache static assets under the asset path for a year; the prefix is separately prevented from falling
// through to the SPA document so missing files remain real 404 responses.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.Context.Request.Path.StartsWithSegments("/assets"))
        {
            context.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        }
    },
});

app.MapWhen(
    context => context.Request.Path.StartsWithSegments("/assets"),
    branch => branch.Run(context =>
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }));

// OAuth providers redirect to these frontend routes. Avoid caching callback pages or sending
// callback query values as a referrer to later requests.
app.MapGet("/auth/github/copilot-app/callback", async context =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexPath, context.RequestAborted);
});

app.MapGet("/auth/callback", async context =>
{
    context.Response.Headers["Cache-Control"] = "no-store";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexPath, context.RequestAborted);
});

// Liveness reports process availability; readiness also requires the built SPA entry document.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => File.Exists(indexPath)
    ? Results.Ok(new { status = "ready" })
    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
// Let client-side routes resolve through the SPA entry document after explicit API/static routes.
app.MapFallbackToFile("index.html");
app.Run();

/// <summary>Exposes the top-level host entry point as a partial type.</summary>
public partial class Program { }
