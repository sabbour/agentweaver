import { assertTargetAllowed } from '../../harness-shared/target-guard.mjs';

export async function createHttpTransport({ target, token, allowProd = false, iUnderstandProd = false }) {
  const url = assertTargetAllowed(target, { allowProd, confirmProduction: iUnderstandProd });
  const { StreamableHTTPClientTransport } = await import('@modelcontextprotocol/sdk/client/streamableHttp.js');
  return new StreamableHTTPClientTransport(url, { requestInit: token ? { headers: { Authorization: `Bearer ${token}` } } : undefined });
}
