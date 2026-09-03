namespace Agentweaver.Mcp;

/// <summary>Outbound configuration for the MCP server's backend API calls.</summary>
/// <param name="ApiUrl">Base URL of the Agentweaver API.</param>
/// <param name="BrokerToken">
/// Agentweaver broker token used by stdio mode. HTTP mode always forwards the independently
/// validated inbound broker token instead.
/// </param>
public sealed record McpConfig(string ApiUrl, string? BrokerToken = null);
