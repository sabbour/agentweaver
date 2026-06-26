var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

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

// Serve VitePress docs at /docs/ with directory browsing (index.html auto-resolve)
app.UseFileServer(new FileServerOptions
{
    RequestPath = "/docs",
    EnableDefaultFiles = true,
    EnableDirectoryBrowsing = false
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
