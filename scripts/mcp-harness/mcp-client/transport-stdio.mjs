// Stdio transport spawns a LOCAL subprocess; there is no network target to
// validate, so network target validation for the HTTP transport does not
// apply here. The `target` option is only ever the transport-selector
// sentinel 'stdio' for this transport and must not be passed to a URL parser.
export async function createStdioTransport({ command, args = [], env }) {
  if (typeof command !== 'string' || !command.trim()) throw new Error('A stdio server command is required');
  const { StdioClientTransport } = await import('@modelcontextprotocol/sdk/client/stdio.js');
  return new StdioClientTransport({ command, args, env });
}
