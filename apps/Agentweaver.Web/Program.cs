var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Serve VitePress docs at /docs/ (before general static files so /docs/ resolves index.html)
app.UseFileServer(new FileServerOptions
{
    RequestPath = "/docs",
    EnableDefaultFiles = true,
    EnableDirectoryBrowsing = false
});

app.UseDefaultFiles();

// Long-lived immutable cache for Vite's content-hashed asset bundles.
// HTML files intentionally get no cache header so browsers always revalidate.
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

// SPA fallback — only for non-/docs paths
app.MapFallback(context =>
{
    if (context.Request.Path.StartsWithSegments("/docs"))
    {
        context.Response.StatusCode = 404;
        return Task.CompletedTask;
    }
    context.Response.ContentType = "text/html";
    return context.Response.SendFileAsync(
        app.Environment.WebRootFileProvider.GetFileInfo("index.html"));
});

app.Run();
