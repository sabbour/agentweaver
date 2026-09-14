namespace Agentweaver.Mcp;

using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;

internal static class McpStaleSessionRecoveryMiddleware
{
    internal const string SessionIdHeaderName = "Mcp-Session-Id";

    public static IApplicationBuilder UseMcpStaleSessionRecovery(
        this IApplicationBuilder app,
        PathString mcpPath) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (CryptographicException ex) when (IsMcpSessionUnprotectFailure(context, mcpPath, ex))
            {
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Agentweaver.Mcp.StaleSession");
                var sessionId = context.Request.Headers[SessionIdHeaderName].ToString();
                logger.LogInformation(
                    "MCP session id {McpSessionId} is stale or cannot be read. Returning 404.",
                    sessionId);

                if (context.Response.HasStarted)
                    throw;

                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        });

    internal static bool IsMcpSessionUnprotectFailure(
        HttpContext context,
        PathString mcpPath,
        CryptographicException exception)
    {
        if (!context.Request.Path.StartsWithSegments(mcpPath)
            || !context.Request.Headers.ContainsKey(SessionIdHeaderName))
            return false;

        var stack = exception.StackTrace;
        return stack is not null
            && stack.Contains("ModelContextProtocol.AspNetCore.StreamableHttpHandler.GetSessionAsync",
                StringComparison.Ordinal);
    }
}
