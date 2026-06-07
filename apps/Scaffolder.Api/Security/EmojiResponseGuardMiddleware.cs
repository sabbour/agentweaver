using System.Text;

namespace Scaffolder.Api.Security;

/// <summary>
/// Scans buffered response bodies for emoji codepoints and fails the response if
/// any are found, enforcing the no-emoji rule on every outbound surface
/// (Principle VIII). Server-sent event streams are not buffered (that would break
/// live streaming); their payloads are already emoji-checked at write time by the
/// event emitter, so the rule is still enforced for streamed content.
/// </summary>
public sealed class EmojiResponseGuardMiddleware
{
    private readonly RequestDelegate _next;

    public EmojiResponseGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsStreamingRoute(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            buffer.Position = 0;
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            EmojiGuard.EnsureNone(text, "response body");

            buffer.Position = 0;
            context.Response.Body = originalBody;
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool IsStreamingRoute(HttpContext context) =>
        context.Request.Path.Value?.EndsWith("/stream", StringComparison.OrdinalIgnoreCase) == true;
}
