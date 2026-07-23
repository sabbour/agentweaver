namespace Agentweaver.Mcp;

/// <summary>
/// Outbound configuration for the MCP server's backend API calls.
/// </summary>
/// <param name="ApiUrl">Base URL of the Agentweaver API.</param>
/// <param name="ApiKey">
/// Shared service credential (<c>AGENTWEAVER_API_KEY</c>). SECURITY (#474): this is the internal
/// service-to-service key that the API maps to the trusted <c>agentweaver-internal</c> identity,
/// which is EXEMPT from project-ownership checks. It must never be handed to a human/stdio MCP
/// client, because that would let the client read or mutate ANY project regardless of ownership.
/// It is retained only for genuine in-process/service callers and as a last-resort fallback.
/// </param>
/// <param name="UserToken">
/// The caller's OWN per-user bearer (<c>AGENTWEAVER_TOKEN</c>) — an Agentweaver-minted OAuth
/// access token or a GitHub token (e.g. <c>gh auth token</c>). When set, it is forwarded to the
/// backend so the API attributes calls to the real user and enforces project ownership normally,
/// rather than collapsing onto the shared service identity. This is the credential stdio MCP
/// clients should use (#474).
/// </param>
public sealed record McpConfig(string ApiUrl, string ApiKey, string? UserToken = null);
