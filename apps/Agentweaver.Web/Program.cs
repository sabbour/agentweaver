var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var fileProvider = app.Environment.WebRootFileProvider;
const string DocumentationBaseUrl = "https://sabbour.github.io/agentweaver/";

// Content-Security-Policy and related defense-in-depth headers for the SPA shell.
// The app is served same-origin (frontend + `/api`/`/auth` proxied through the same
// gateway host — see AGENTWEAVER_API_URL="" in the Dockerfile), so `'self'` covers
// script/style/connect sources without needing to allowlist a separate API origin.
// `style-src` needs `'unsafe-inline'` because @fluentui/react-components (Griffel)
// injects `<style>` rules at runtime; there is no confirmed script-injection sink
// today (see .security findings), so `script-src` intentionally stays strict with
// no `'unsafe-inline'`/`'unsafe-eval'`.
const string ContentSecurityPolicy =
    "default-src 'self'; " +
    "script-src 'self'; " +
    "style-src 'self' 'unsafe-inline'; " +
    "img-src 'self' data:; " +
    "font-src 'self' data:; " +
    "connect-src 'self'; " +
    "object-src 'none'; " +
    "base-uri 'self'; " +
    "form-action 'self'; " +
    "frame-ancestors 'none'";

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Content-Security-Policy"] = ContentSecurityPolicy;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var ext = Path.GetExtension(ctx.File.Name);
        if (!string.IsNullOrEmpty(ext) && ext != ".html")
        {
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        }
    }
});

app.MapGet("/docs", () => Results.Redirect(DocumentationBaseUrl, permanent: false));
app.MapGet("/docs/{**path}", (string? path) =>
{
    var target = string.IsNullOrWhiteSpace(path)
        ? DocumentationBaseUrl
        : $"{DocumentationBaseUrl}{path.TrimStart('/')}";
    return Results.Redirect(target, permanent: false);
});

// SPA fallback for React app
app.MapFallback(async context =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(fileProvider.GetFileInfo("index.html"));
});

app.Run();
