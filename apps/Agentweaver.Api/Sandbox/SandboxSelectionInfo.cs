using System.Text.Json;

namespace Agentweaver.Api.Sandbox;

internal sealed class SandboxSelectionInfo
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public string? Backend { get; init; }
    public bool IsRealIsolation { get; init; }
    public string? Reason { get; init; }
    public string? ClaimName { get; init; }
    public string? PodName { get; init; }
    public string? Namespace { get; init; }

    public static SandboxSelectionInfo? FromPayload(object? payload)
    {
        if (payload is null)
            return null;

        var json = JsonSerializer.Serialize(payload, SerializeOptions);
        return JsonSerializer.Deserialize<SandboxSelectionInfo>(json, DeserializeOptions);
    }
}
