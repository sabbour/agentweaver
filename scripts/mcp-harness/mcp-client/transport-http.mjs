import { validateNetworkTarget } from '../../harness-shared/target-guard.mjs';

export async function createHttpTransport({ target, authProvider, tenantId, fetchImpl = globalThis.fetch }) {
  const url = validateNetworkTarget(target, { exactPath: '/mcp' });
  if (authProvider?.origin !== url.origin || typeof authProvider.getAuthorization !== 'function'
    || typeof tenantId !== 'string' || !tenantId.trim()) {
    throw new Error('MCP requires an origin-bound broker auth provider and an explicit tenant.');
  }
  const { StreamableHTTPClientTransport } = await import('@modelcontextprotocol/sdk/client/streamableHttp.js');
  return new StreamableHTTPClientTransport(url, {
    fetch: async (input, init = {}) => {
      const destination = validateNetworkTarget(input instanceof Request ? input.url : input, { exactPath: '/mcp' });
      if (destination.origin !== url.origin) throw new Error('Refusing cross-origin MCP authentication.');
      const headers = new Headers(input instanceof Request ? input.headers : undefined);
      new Headers(init.headers).forEach((value, name) => headers.set(name, value));
      headers.set('Authorization', await authProvider.getAuthorization(destination));
      headers.set('X-Agentweaver-Tenant', tenantId);
      return fetchImpl(input, { ...init, headers, redirect: 'error' });
    },
    requestInit: { redirect: 'error' },
  });
}
