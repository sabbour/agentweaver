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
    // Silent authorization relays only through this same-origin callback frame.
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
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

// Report the web image's build identity for the app version badge. The image build bakes
// the component version and source commit into the environment, so a reused image keeps
// reporting the build it actually came from.
var buildVersion = WebBuildVersion.FromEnvironment(
    Environment.GetEnvironmentVariable("AGENTWEAVER_WEB_VERSION"),
    Environment.GetEnvironmentVariable("AGENTWEAVER_WEB_GIT_SHA"));
app.MapGet("/api/version", (HttpContext context) =>
{
    // A cached answer could show an old version after an upgrade.
    context.Response.Headers["Cache-Control"] = "no-store";
    return Results.Json(buildVersion);
});
// Let client-side routes resolve through the SPA entry document after explicit API/static routes.
app.MapFallbackToFile("index.html");
app.Run();

/// <summary>Exposes the top-level host entry point as a partial type.</summary>
public partial class Program { }

/// <summary>Build identity returned by <c>/api/version</c>, in the shape the web app expects.</summary>
/// <param name="Version">Semantic version of the web build, or null when the image did not record one.</param>
/// <param name="GitSha">Full source commit of the web build, or null when it is not known.</param>
/// <param name="IsRelease">True when the version is a plain release version without a prerelease suffix.</param>
public sealed record WebBuildVersion(
    [property: System.Text.Json.Serialization.JsonPropertyName("version")] string? Version,
    [property: System.Text.Json.Serialization.JsonPropertyName("gitSha")] string? GitSha,
    [property: System.Text.Json.Serialization.JsonPropertyName("isRelease")] bool IsRelease)
{
    private static readonly System.Text.RegularExpressions.Regex SemanticVersion =
        new(@"^v?(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex CommitSha =
        new("^[0-9a-fA-F]{7,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Validates the values baked into the image and drops anything that is not a real version or commit.</summary>
    public static WebBuildVersion FromEnvironment(string? version, string? gitSha)
    {
        // Local and test runs have no build values, and the Dockerfile defaults ("dev" and
        // "unknown") are placeholders. Report those as missing so the browser keeps its own
        // fallback instead of showing a placeholder as a version.
        var match = SemanticVersion.Match(version?.Trim() ?? string.Empty);
        var sha = gitSha?.Trim();
        var knownSha = sha is not null && CommitSha.IsMatch(sha) ? sha.ToLowerInvariant() : null;
        if (!match.Success) return new WebBuildVersion(null, knownSha, false);

        // Drop a leading "v" so the badge adds exactly one. Only a plain x.y.z version is a
        // release; a prerelease suffix still shows its commit in the badge.
        var normalized = match.Groups[1].Value + match.Groups[2].Value;
        return new WebBuildVersion(normalized, knownSha, !match.Groups[2].Success);
    }
}
