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

  it('posts a tenant-pinned validation request and parses invalid-policy diagnostics', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({
        title: 'native_acs_policy_invalid',
        status: 422,
        code: 'native_acs_policy_invalid',
        diagnostics: [{
          code: 'yaml.invalid',
          message: 'The manifest is invalid.',
          relativePath: null,
          line: 2,
          column: 1,
        }],
      }), {
        status: 422,
        headers: { 'Content-Type': 'application/problem+json' },
      }),
    );
    const client = new AgentweaverGatewayClient('/api/v1', fetcher);
    const artifact = {
      manifestBytes: [110, 97, 109, 101],
      files: [],
      contentSha256: 'a'.repeat(64),
    };

    await expect(client.validatePlatformPolicyArtifact('policy-token', 'tenant-1', artifact))
      .resolves.toEqual({
        isValid: false,
        diagnostics: [{
          code: 'yaml.invalid',
          message: 'The manifest is invalid.',
          relativePath: null,
          line: 2,
          column: 1,
        }],
      });

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe('/api/v1/policies/platform/validate');
    expect(init?.method).toBe('POST');
    expect(init?.credentials).toBe('omit');
    expect(init?.cache).toBe('no-store');
    expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer policy-token');
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(new Headers(init?.headers).get('Content-Type')).toBe('application/json');
    expect(JSON.parse(String(init?.body))).toEqual({ artifact });
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
