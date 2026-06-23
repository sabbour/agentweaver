using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Agentweaver.Mcp.Tools;

[McpServerToolType]
public sealed class DiagnosticsTools(AgentweaverApiClient api)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    [McpServerTool(Name = "diagnostics_get"), Description("Get a real-time system diagnostics snapshot: API version, process uptime, project/run counts, heartbeat state, and checkpoint GC state.")]
    public async Task<string> DiagnosticsGetAsync(CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>("/api/diagnostics", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }

    [McpServerTool(Name = "heartbeat_status"), Description("Get the current coordinator heartbeat service status: enabled flag, interval, last tick time, and service state (running / waiting_first_tick / disabled).")]
    public async Task<string> HeartbeatStatusAsync(CancellationToken ct)
    {
        try
        {
            var result = await api.GetAsync<JsonElement>("/api/diagnostics/heartbeat", ct);
            return JsonSerializer.Serialize(result, JsonOpts);
        }
        catch (McpApiException) { throw; }
        catch (Exception ex) { throw new McpApiException(0, ex.Message); }
    }
}
