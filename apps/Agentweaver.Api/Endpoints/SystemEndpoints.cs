using System.Text.Json.Serialization;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Api.Endpoints;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        // GET /api/system/runtime
        // Returns runtime context: whether we are running inside Kubernetes and, if so,
        // the pod name. Used by the web UI to annotate agent boxes in topology graphs.
        app.MapGet("/api/system/runtime", (IKubernetesEnvironment k8s) =>
        {
            return Results.Ok(new RuntimeInfoDto
            {
                Kubernetes = k8s.IsKubernetes,
                PodName    = k8s.PodName,
            });
        });
    }
}

/// <summary>
/// Wire-format DTO for GET /api/system/runtime.
/// <c>[JsonPropertyName]</c> attributes pin the exact field names the frontend contract requires.
/// </summary>
public sealed record RuntimeInfoDto
{
    [JsonPropertyName("kubernetes")] public bool    Kubernetes { get; init; }
    [JsonPropertyName("podName")]    public string? PodName    { get; init; }
}
