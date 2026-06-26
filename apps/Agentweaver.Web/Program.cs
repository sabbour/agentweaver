var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var fileProvider = app.Environment.WebRootFileProvider;

// Rewrite /docs clean URLs before static file handling
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.Equals("/docs", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("/docs/", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Path = "/docs/index.html";
    }
    else if (path.StartsWith("/docs/", StringComparison.OrdinalIgnoreCase) &&
             !Path.HasExtension(path))
    {
        context.Request.Path = path + ".html";
    }
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

// SPA fallback — skip /docs paths (VitePress is static HTML, not SPA)
app.MapFallback(context =>
{
    if (context.Request.Path.StartsWithSegments("/docs"))
    {
        context.Response.StatusCode = 404;
        return Task.CompletedTask;
    }
    context.Response.ContentType = "text/html";
    return context.Response.SendFileAsync(fileProvider.GetFileInfo("index.html"));
});

app.Run();
