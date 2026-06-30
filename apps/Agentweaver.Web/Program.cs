var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var fileProvider = app.Environment.WebRootFileProvider;
const string DocumentationBaseUrl = "https://sabbour.github.io/agentweaver/";

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
