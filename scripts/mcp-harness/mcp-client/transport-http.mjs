import { validateNetworkTarget } from '../../harness-shared/target-guard.mjs';

export function noRedirectFetch(input, init = {}, fetchImpl = globalThis.fetch) {
  return fetchImpl(input, { ...init, redirect: 'error' });
}

export async function createHttpTransport({ target, token, fetchImpl = globalThis.fetch }) {
  const url = validateNetworkTarget(target, { exactPath: '/mcp' });
  const { StreamableHTTPClientTransport } = await import('@modelcontextprotocol/sdk/client/streamableHttp.js');
  return new StreamableHTTPClientTransport(url, {
    fetch: (input, init) => noRedirectFetch(input, init, fetchImpl),
    requestInit: {
      redirect: 'error',
      ...(token ? { headers: { Authorization: `Bearer ${token}` } } : {}),
    },
  });
}
