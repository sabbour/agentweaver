import { validateNetworkTarget } from '../../harness-shared/target-guard.mjs';

export async function createHttpTransport({ target, token }) {
  const url = validateNetworkTarget(target, { exactPath: '/mcp' });
  const { StreamableHTTPClientTransport } = await import('@modelcontextprotocol/sdk/client/streamableHttp.js');
  return new StreamableHTTPClientTransport(url, { requestInit: token ? { headers: { Authorization: `Bearer ${token}` } } : undefined });
}
