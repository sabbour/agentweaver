using Agentweaver.Api.Notifications;
using Agentweaver.Api.Security;

namespace Agentweaver.Api.Endpoints;

public static class NotificationsEndpoints
{
    /// <summary>
    /// GET /api/notifications — the signed-in user's pending Human Review + Tool Approval requests
    /// across every project/run they own. Polled by the frontend notification provider (#247); see
    /// <see cref="NotificationsService"/> for the polling-vs-SSE delivery rationale and MVP scope.
    /// </summary>
    public static void MapNotificationsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/notifications", async (
            HttpContext httpContext,
            NotificationsService notifications,
            CancellationToken ct) =>
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            var result = await notifications.GetPendingAsync(caller, ct).ConfigureAwait(false);
            return Results.Ok(result);
        });

        app.MapPost("/api/notifications/{notificationId}/dismiss", async (
            string notificationId,
            HttpContext httpContext,
            NotificationsService notifications,
            CancellationToken ct) =>
        {
            var caller = ApiKeyAuthMiddleware.GetCaller(httpContext);
            await notifications.DismissAsync(caller, notificationId, ct).ConfigureAwait(false);
            return Results.NoContent();
        });
    }
}
