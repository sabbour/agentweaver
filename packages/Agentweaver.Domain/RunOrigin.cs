namespace Agentweaver.Domain;

/// <summary>
/// Durable provenance marker for a run. Interactive runs (the default) are started by a human via
/// the HTTP/MCP surface; BacklogPickup runs are coordinator runs created unattended by the backlog
/// heartbeat from a claimed Ready task. Persisted as TEXT 'interactive' | 'backlog_pickup'.
/// </summary>
public enum RunOrigin
{
    Interactive,
    BacklogPickup,
}
