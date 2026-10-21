import { describe, expect, it, vi } from 'vitest';
import { GatewayError } from './api';
import { NotificationsClient } from './notificationsClient';
import type { UserNotificationPage } from './notificationsClient';

const page: UserNotificationPage = { items: [], count: 0, nextCursor: null };

/** Builds a client with a fake fetch that always returns `response()`, so tests can inspect each request. */
function client(response: () => Response) {
  const fetcher = vi.fn<(input: RequestInfo | URL, init?: RequestInit) => Promise<Response>>(async () => response());
  return { fetcher, notifications: new NotificationsClient('https://gw.test/api/v1/', fetcher as unknown as typeof fetch) };
}

describe('NotificationsClient', () => {
  it('reads one page with the project filter and dismissed items', async () => {
    const { fetcher, notifications } = client(() => Response.json(page));
    await expect(notifications.list('token', { projectId: 'p 1', includeDismissed: true, cursor: 'c1', pageSize: 100 }, 'tenant-1'))
      .resolves.toEqual(page);
    const [url, init] = fetcher.mock.calls[0];
    // Every filter must reach the server, with spaces encoded, and the base URL's trailing slash removed.
    expect(String(url)).toBe('https://gw.test/api/v1/notifications?projectId=p+1&includeDismissed=true&cursor=c1&pageSize=100');
    const headers = new Headers(init?.headers);
    // The token and the tenant selector must both be forwarded as headers.
    expect(headers.get('Authorization')).toBe('Bearer token');
    expect(headers.get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('sends no query when no filter is set', async () => {
    const { fetcher, notifications } = client(() => Response.json(page));
    await notifications.list('token');
    expect(String(fetcher.mock.calls[0][0])).toBe('https://gw.test/api/v1/notifications');
  });

  it('dismisses one notification with POST', async () => {
    const { fetcher, notifications } = client(() => new Response(null, { status: 204 }));
    await expect(notifications.dismiss('token', 'n/1')).resolves.toBeUndefined();
    // A '/' in the id must be encoded so it cannot reach a different route.
    expect(String(fetcher.mock.calls[0][0])).toBe('https://gw.test/api/v1/notifications/n%2F1/dismiss');
    expect(fetcher.mock.calls[0][1]?.method).toBe('POST');
  });

  // A token bound to a project or run is rejected with 403. The UI needs the error code
  // to explain this, so the client must surface it on GatewayError.
  it('keeps the error code of a bound-token rejection', async () => {
    const { notifications } = client(() => Response.json({ error: 'caller_binding_mismatch' }, { status: 403 }));
    const error = await notifications.list('bound-token').catch((reason: unknown) => reason);
    expect(error).toBeInstanceOf(GatewayError);
    expect((error as GatewayError).status).toBe(403);
    expect((error as GatewayError).code).toBe('caller_binding_mismatch');
  });
});