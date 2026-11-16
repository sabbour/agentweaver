import { describe, expect, it, vi } from 'vitest';
import { AgentweaverGatewayClient } from './api';
import {
  PLATFORM_POLICY_MAX_HTTP_BYTES,
} from './platformPolicyContracts';

const emptyHistory = {
  scopeType: 'platform',
  scopeId: 'default',
  policyId: null,
  snapshotRevision: null,
  items: [],
  nextCursor: null,
};

describe('Platform Policy API reads', () => {
  it('sends an explicit tenant and does not fall back when the selector is missing', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify(emptyHistory), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.test/api/v1', fetcher);

    await client.getPlatformPolicyRevisionHistory('policy-token', 'tenant-1');
    const [url, init] = fetcher.mock.calls[0];
    const headers = new Headers(init?.headers);
    expect(String(url)).toBe('https://gateway.test/api/v1/policies/platform/revisions?pageSize=20');
    expect(headers.get('Authorization')).toBe('Bearer policy-token');
    expect(headers.get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(init).toMatchObject({ credentials: 'omit', cache: 'no-store' });

    await expect(client.getPlatformPolicyRevisionHistory('policy-token', ''))
      .rejects.toMatchObject({ status: 400, code: 'tenant_selector_required' });
    expect(fetcher).toHaveBeenCalledOnce();
  });

  it('rejects oversized and forbidden responses without exposing server details', async () => {
    const exactLimitFetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify(emptyHistory), {
        status: 200,
        headers: { 'Content-Length': String(PLATFORM_POLICY_MAX_HTTP_BYTES) },
      }),
    );
    const exactLimitClient = new AgentweaverGatewayClient('/api/v1', exactLimitFetcher);
    await expect(exactLimitClient.getPlatformPolicyRevisionHistory('token', 'tenant-1'))
      .resolves.toMatchObject({ policyId: null, items: [] });

    const oversizedFetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response('{"detail":"private"}', {
        status: 200,
        headers: { 'Content-Length': String(PLATFORM_POLICY_MAX_HTTP_BYTES + 1) },
      }),
    );
    const oversizedClient = new AgentweaverGatewayClient('/api/v1', oversizedFetcher);
    await expect(oversizedClient.getPlatformPolicyRevisionHistory('token', 'tenant-1'))
      .rejects.toMatchObject({ status: 502, code: 'platform_policy_response_too_large' });

    const deniedClient = new AgentweaverGatewayClient('/api/v1', vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({ detail: 'private server detail' }), { status: 403 }),
    ));
    await expect(deniedClient.getPlatformPolicyRevisionHistory('token', 'tenant-1'))
      .rejects.toMatchObject({
        status: 403,
        code: 'platform_policy_request_failed',
        message: 'Platform Policy access was denied.',
      });
  });
});
