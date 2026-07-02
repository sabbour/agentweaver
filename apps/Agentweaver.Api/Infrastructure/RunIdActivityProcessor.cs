using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// OpenTelemetry processor that propagates <c>run_id</c> as an Activity tag so it
/// surfaces as <c>customDimensions["run_id"]</c> in Application Insights
/// (<c>dependencies</c>, <c>requests</c>, and <c>traces</c> tables).
/// </summary>
/// <remarks>
/// For each new Activity the processor checks, in order:
/// <list type="number">
///   <item>Whether the tag is already present (set by the agent host or a parent span).</item>
///   <item>HTTP route values (<c>runId</c> or <c>id</c> that parse as a valid GUID).</item>
///   <item>The <c>X-Run-Id</c> request header.</item>
/// </list>
/// </remarks>
internal sealed class RunIdActivityProcessor : BaseProcessor<Activity>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public RunIdActivityProcessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public override void OnStart(Activity activity)
    {
        // Already tagged (e.g. by the agent host or a parent span).
        if (activity.GetTagItem("run_id") is not null)
            return;

        var context = _httpContextAccessor.HttpContext;
        if (context is null) return;

        // Try route values first: /api/runs/{id}/... or /api/metrics/runs/{runId}/...
        var candidate = TryGetRouteRunId(context)
            ?? TryGetHeaderRunId(context);

        if (!string.IsNullOrWhiteSpace(candidate))
            activity.SetTag("run_id", candidate);
    }

    private static string? TryGetRouteRunId(HttpContext context)
    {
        if (context.Request.RouteValues.TryGetValue("runId", out var runIdVal))
        {
            var value = runIdVal?.ToString();
            if (Guid.TryParse(value, out _))
                return value;
        }

        // Many routes use "id" for the run — validate it is a GUID to avoid
        // tagging project-id or other entity routes.
        if (context.Request.RouteValues.TryGetValue("id", out var idVal))
        {
            var value = idVal?.ToString();
            if (Guid.TryParse(value, out _))
                return value;
        }

        return null;
    }

    private static string? TryGetHeaderRunId(HttpContext context)
    {
        var value = context.Request.Headers["X-Run-Id"].FirstOrDefault();
        return Guid.TryParse(value, out _) ? value : null;
    }
}
