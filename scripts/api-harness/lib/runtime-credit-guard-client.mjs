import { randomUUID } from 'node:crypto';
import { validateNetworkTarget } from '../../harness-shared/target-guard.mjs';
import { redact } from '../../harness-shared/redaction.mjs';
import {
  RUNTIME_CREDIT_GUARD_READ_PATH,
  validateRuntimeCreditGuardReadRequest,
  validateRuntimeCreditGuardSnapshot,
} from './runtime-credit-guard-contract.mjs';

/** Reads the Orchestrator's actor-scoped runtime credit-guard snapshot for the P1 harness. */
const CORRELATION_HEADERS = ['traceparent', 'request-id', 'x-request-id', 'x-correlation-id'];

/**
 * Calls the no-store credit-guard read endpoint and records redacted HTTP evidence.
 * The endpoint response is checked against the shared contract before a successful snapshot is accepted.
 */
export class RuntimeCreditGuardClient {
  /** Require a validated root URL, origin-bound actor auth provider, and fetch implementation. */
  constructor({ baseUrl, authProvider, fetchImpl = globalThis.fetch }) {
    this.target = validateNetworkTarget(baseUrl, { exactPath: '/' });
    if (typeof authProvider?.getAuthorization !== 'function' || authProvider.origin !== this.target.origin) {
      throw new Error('An actor auth provider bound to the exact Orchestrator origin is required.');
    }
    if (typeof fetchImpl !== 'function') throw new Error('A fetch implementation is required.');
    this.authProvider = authProvider;
    this.fetchImpl = fetchImpl;
    this.calls = [];
  }

  /**
   * Read one validated intent and preserve its actual status, cache policy, and redacted response.
   * Non-no-store responses and invalid successful snapshots throw after the call evidence is retained.
   */
  async read(request, { signal } = {}) {
    validateRuntimeCreditGuardReadRequest(request);
    const url = new URL(RUNTIME_CREDIT_GUARD_READ_PATH, this.target);
    const requestId = randomUUID();
    const record = {
      requestId,
      operationId: 'readRuntimeCreditGuardSnapshot',
      method: 'POST',
      path: url.pathname,
      requestBody: redact(request),
      status: null,
      traceId: null,
      cacheControl: null,
      responseBody: null,
    };
    const started = Date.now();
    try {
      const authorization = await this.authProvider.getAuthorization(url);
      if (typeof authorization !== 'string' || !authorization.startsWith('Bearer ')) {
        throw new Error('The actor auth provider did not return an authorization value.');
      }
      const headers = new Headers({
        Accept: 'application/json, application/problem+json',
        Authorization: authorization,
        'Cache-Control': 'no-store',
        'Content-Type': 'application/json',
        'X-Correlation-Id': requestId,
      });
      const response = await this.fetchImpl(url, {
        method: 'POST',
        headers,
        body: JSON.stringify(request),
        redirect: 'error',
        signal: signal
          ? AbortSignal.any([signal, AbortSignal.timeout(30000)])
          : AbortSignal.timeout(30000),
      });
      record.status = response.status;
      record.traceId = redact(
        CORRELATION_HEADERS.map(name => response.headers.get(name)).find(Boolean) ?? null,
      );
      record.cacheControl = response.headers.get('cache-control');
      const text = await response.text();
      let responseBody = text;
      if (text && response.headers.get('content-type')?.includes('json')) {
        try {
          responseBody = JSON.parse(text);
        } catch {
          // Keep malformed JSON as redacted text evidence with the original HTTP status.
        }
      }
      record.responseBody = redact(responseBody);
      const responseCacheDirectives = (record.cacheControl ?? '')
        .split(',')
        .map(directive => directive.trim().toLowerCase());
      if (!responseCacheDirectives.includes('no-store')) {
        throw new Error('The Orchestrator credit-guard response did not satisfy no-store.');
      }
      if (response.status === 200) {
        if (responseBody === text && text) {
          throw new Error('The Orchestrator returned an invalid credit-guard snapshot.');
        }
        validateRuntimeCreditGuardSnapshot(responseBody, request.intent);
      }
      return record;
    } catch (error) {
      record.error = redact(error);
      throw error;
    } finally {
      record.ms = Date.now() - started;
      this.calls.push(record);
    }
  }
}
