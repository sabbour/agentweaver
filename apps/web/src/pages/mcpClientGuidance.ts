import { resolvePublicApiOrigin } from '../config';

/** Supported clients with client-specific setup instructions on Account Settings. */
export const MCP_CLIENT_IDS = [
  'copilot-cli',
  'copilot-desktop',
  'vs-code',
  'claude-desktop',
] as const;

export type McpClientId = (typeof MCP_CLIENT_IDS)[number];

/** Public Agentweaver custom-agent definition shown beside MCP setup guidance. */
export const AGENTWEAVER_AGENT_URL =
  `${resolvePublicApiOrigin()}/agents/agentweaver.agent.md`;
export const CLAUDE_OAUTH_CLIENT_ID = 'agentweaver-claude';

/** Text shown for one supported MCP client. */
export interface McpClientGuidance {
  label: string;
  setup: string;
  verification: string;
}

/**
 * Client setup copy kept in one typed map so tabs and instructions cannot drift
 * from the supported client identifiers.
 */
export const MCP_CLIENT_GUIDANCE: Record<McpClientId, McpClientGuidance> = {
  'claude-desktop': {
    label: 'Claude Desktop',
    setup: `Open Customize → Connectors, add a custom connector named Agentweaver, enter the MCP server URL, then open Advanced settings and set OAuth Client ID to ${CLAUDE_OAUTH_CLIENT_ID}. Leave OAuth Client Secret empty.`,
    verification: 'Open the connector details and confirm that Agentweaver tools are available.',
  },
  'vs-code': {
    label: 'VS Code',
    setup: 'Run “MCP: Add Server” from the Command Palette, choose HTTP, enter the MCP server URL, and choose the user or workspace scope.',
    verification: 'Run “MCP: List Servers” and confirm that Agentweaver is running and exposes tools.',
  },
  'copilot-cli': {
    label: 'GitHub Copilot CLI',
    setup: 'Run /mcp add, name the server agentweaver, choose HTTP, enter the MCP server URL, leave HTTP headers empty, and save.',
    verification: 'Run /mcp show agentweaver and confirm that the server is connected and its tools are listed.',
  },
  'copilot-desktop': {
    label: 'GitHub Copilot desktop',
    setup: 'Open Customize → MCP servers, add a custom remote HTTP server, name it Agentweaver, and enter the MCP server URL.',
    verification: 'Start a session and confirm that Agentweaver tools are available in the tool picker.',
  },
};
