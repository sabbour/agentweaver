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

app.MapFallbackToFile("index.html");

app.Run();
