/**
 * Client for the caller's pending-gate notifications (`/api/v1/notifications`).
 * Every call needs an UNBOUND token: a token with a `project_id` or `run_id`
 * claim gets `403 caller_binding_mismatch`. Call these through
 * `apiCall(operation, null)`. This is the only module that talks to the
 * notifications routes or imports their contract types.
 */
import { gatewayBaseUrl } from './config';
import { encodeSegments, sendGatewayRequest } from './api';
import type { UserNotification, UserNotificationPage } from './contracts';

export type { UserNotification, UserNotificationPage };

/** Filters for one notifications page. Pass `nextCursor` back with the same filters. */
export interface NotificationQuery {
  projectId?: string;
  /** True also returns items the caller dismissed while their gate is still pending. */
  includeDismissed?: boolean;
  cursor?: string | null;
  pageSize?: number;
}

/** Notifications routes on the Gateway. Tests can pass their own base URL and fetch. */
export class NotificationsClient {
  private readonly baseUrl: string;
  private readonly fetcher: typeof fetch;

  constructor(baseUrl = gatewayBaseUrl, fetcher: typeof fetch = (...args) => fetch(...args)) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.fetcher = fetcher;
  }

  /** Reads one newest-first page of pending-gate notifications. */
  list(token: string, query: NotificationQuery = {}, tenantSelector?: string | null): Promise<UserNotificationPage> {
    const params = new URLSearchParams();
    if (query.projectId) params.set('projectId', query.projectId);
    if (query.includeDismissed !== undefined) params.set('includeDismissed', String(query.includeDismissed));
    if (query.cursor) params.set('cursor', query.cursor);
    if (query.pageSize) params.set('pageSize', String(query.pageSize));
    const suffix = params.size > 0 ? `?${params.toString()}` : '';
    return sendGatewayRequest(this.fetcher, token, `${this.baseUrl}/notifications${suffix}`, {}, tenantSelector);
  }

  /** Hides one notification from the default list. The gate stays pending. Returns 204. */
  dismiss(token: string, notificationId: string, tenantSelector?: string | null): Promise<void> {
    return sendGatewayRequest(
      this.fetcher,
      token,
      `${this.baseUrl}/notifications/${encodeSegments(notificationId, 'dismiss')}`,
      { method: 'POST' },
      tenantSelector,
    );
  }
}

export const notificationsClient = new NotificationsClient();