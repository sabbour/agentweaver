var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var fileProvider = app.Environment.WebRootFileProvider;

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

// SPA fallback + docs directory index
app.MapFallback(async context =>
{
    var path = context.Request.Path.Value ?? "";

    // /docs or /docs/ → serve docs/index.html
    if (path.Equals("/docs", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/docs/", StringComparison.OrdinalIgnoreCase))
    {
        var docsIndex = fileProvider.GetFileInfo("docs/index.html");
        if (docsIndex.Exists)
        {
            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(docsIndex);
            return;
        }
    }

    // /docs sub-paths without extension → try .html (VitePress clean URLs)
    if (path.StartsWith("/docs/", StringComparison.OrdinalIgnoreCase) &&
        !Path.HasExtension(path))
    {
        var htmlPath = path[1..] + ".html"; // strip leading /
        var htmlFile = fileProvider.GetFileInfo(htmlPath);
        if (htmlFile.Exists)
        {
            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(htmlFile);
            return;
        }
        // Try as directory with index.html
        var dirIndex = fileProvider.GetFileInfo(path[1..] + "/index.html");
        if (dirIndex.Exists)
        {
            context.Response.ContentType = "text/html";
            await context.Response.SendFileAsync(dirIndex);
            return;
        }
        context.Response.StatusCode = 404;
        return;
    }

    // All other /docs paths that weren't served by UseStaticFiles → 404
    if (path.StartsWith("/docs", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 404;
        return;
    }

    // SPA fallback for React app
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(fileProvider.GetFileInfo("index.html"));
});

app.Run();
