var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var fileProvider = app.Environment.WebRootFileProvider;

// Redirect /docs to /docs/ so default files work
app.Use(async (context, next) =>
{
    if (context.Request.Path.Value?.Equals("/docs", StringComparison.OrdinalIgnoreCase) == true)
    {
        context.Response.Redirect("/docs/", permanent: false);
        return;
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
