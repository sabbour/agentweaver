import { randomUUID } from 'node:crypto';
import { validateNetworkTarget } from '../../harness-shared/target-guard.mjs';
import { redact } from '../../harness-shared/redaction.mjs';

/** Gateway API harness client, live contract discovery, and redacted request evidence. */
/** HTTP client for exercising only operations discovered from the live Agentweaver Gateway contract. */
const METHODS = new Set(['GET', 'POST', 'PUT', 'PATCH', 'DELETE']);
const CORRELATION_HEADERS = ['traceparent', 'request-id', 'x-request-id', 'x-correlation-id'];

/**
 * Calls the selected Gateway origin as one explicit tenant and records redacted request evidence.
 * Discovery and invocation are deliberately tied to the live OpenAPI document, not a local route list.
 */
export class AgentweaverClient {
  /** Bind the client to a validated root URL, broker identity, tenant, and injectable fetch implementation. */
  constructor({ baseUrl, authProvider, tenantId, fetchImpl = globalThis.fetch }) {
    this.target = validateNetworkTarget(baseUrl, { exactPath: '/' });
    if (typeof authProvider?.getAuthorization !== 'function' || authProvider.origin !== this.target.origin) {
      throw new Error('A broker auth provider bound to the exact API origin is required.');
    }
    if (typeof tenantId !== 'string' || !tenantId.trim()) {
      throw new Error('An explicit authorized tenant selector is required.');
    }
    this.authProvider = authProvider;
    this.tenantId = tenantId;
    this.fetchImpl = fetchImpl;
    this.calls = [];
    this.operations = new Map();
    this.schemas = new Map();
  }

  /**
   * Send one same-origin API request and retain a redacted record even when the network call fails.
   * Authentication, redirects, and request identity cannot be overridden by caller-supplied headers.
   */
  async call(method, path, body, { authenticated = true, headers = {}, signal } = {}) {
    if (!METHODS.has(method)) throw new Error('Unsupported API method.');
    const url = validateNetworkTarget(new URL(path, this.target));
    if (url.origin !== this.target.origin) throw new Error('Refusing cross-origin API requests.');
    const requestHeaders = new Headers(headers);
    for (const name of ['authorization', 'cookie', 'x-agentweaver-tenant']) {
      if (requestHeaders.has(name)) throw new Error('Request headers cannot override the selected auth identity.');
    }
    requestHeaders.set('Accept', 'application/json');
    requestHeaders.set('X-Agentweaver-Tenant', this.tenantId);
    const requestId = randomUUID();
    requestHeaders.set('X-Correlation-Id', requestId);
    if (authenticated) {
      const authorization = await this.authProvider.getAuthorization(url);
      if (typeof authorization !== 'string' || !authorization.startsWith(['Bearer', ''].join(' '))) {
        throw new Error('The broker auth provider did not return an authorization value.');
      }
      requestHeaders.set('Authorization', authorization);
    }
    if (body !== undefined) requestHeaders.set('Content-Type', 'application/json');
    const started = Date.now();
    const record = {
      requestId, method, path: url.pathname, query: redact(Object.fromEntries(url.searchParams)),
      requestBody: redact(body ?? null), status: null, traceId: null, responseBody: null,
    };
    try {
      const response = await this.fetchImpl(url, {
        method, headers: requestHeaders, redirect: 'error',
        signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(30000)]) : AbortSignal.timeout(30000),
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });
      record.status = response.status;
      record.cacheControl = response.headers.get('cache-control');
      record.traceId = redact(CORRELATION_HEADERS.map(name => response.headers.get(name)).find(Boolean) ?? null);
      const text = await response.text();
      let rawBody = text;
      if (text && response.headers.get('content-type')?.includes('json')) rawBody = JSON.parse(text);
      record.responseBody = redact(rawBody);
      Object.defineProperty(record, 'transientResponseBody', { value: rawBody });
      return record;
    } catch (error) {
      record.error = redact(error);
      throw error;
    } finally {
      record.ms = Date.now() - started;
      this.calls.push(record);
    }
  }

  /**
   * Read and validate the live Gateway OpenAPI menu, replacing the cached operation map on success.
   * Throws when the document is unavailable, malformed, empty, or has ambiguous operation IDs.
   */
  async discover() {
    this.operations.clear();
    const call = await this.call('GET', '/openapi/v1.json', undefined, { authenticated: false });
    const document = call.transientResponseBody;
    if (call.status !== 200 || document?.openapi !== '3.1.0'
      || !document.paths || typeof document.paths !== 'object' || Array.isArray(document.paths)) {
      throw new Error('The live Gateway OpenAPI document is missing or invalid.');
    }
    const operations = new Map();
    for (const [path, item] of Object.entries(document.paths)) {
      if (!path.startsWith('/api/v1/')) continue;
      for (const [method, operation] of Object.entries(item)) {
        if (!METHODS.has(method.toUpperCase())) continue;
        if (!operation.operationId || operations.has(operation.operationId)) {
          throw new Error('The live Gateway operation identity is missing or duplicated.');
        }
        operations.set(operation.operationId, {
          ...operation, method: method.toUpperCase(), path,
          parameters: [...(item.parameters ?? []), ...(operation.parameters ?? [])],
        });
      }
    }
    if (!operations.size) throw new Error('The live Gateway advertises no P1 operations.');
    this.operations = operations;
    const schemas = document.components?.schemas;
    this.schemas = schemas && typeof schemas === 'object' && !Array.isArray(schemas)
      ? new Map(Object.entries(schemas))
      : new Map();
    return document;
  }

  /**
   * Invoke a discovered operation after checking its path, query, and request-body contract.
   * Returns the request evidence; unknown operations or contract mismatches are rejected before sending.
   */
  async invoke(operationId, { pathParameters = {}, query = {}, body, headers, signal } = {}) {
    const operation = this.operations.get(operationId);
    if (!operation) throw new Error('The requested operation has not been discovered from the live Gateway.');
    let path = operation.path;
    const requiredPaths = [...path.matchAll(/\{([^}]+)\}/g)].map(match => match[1]);
    for (const name of requiredPaths) {
      if (typeof pathParameters[name] !== 'string' || !pathParameters[name]) {
        throw new Error(`Missing live-contract path parameter: ${name}`);
      }
      path = path.replace(`{${name}}`, encodeURIComponent(pathParameters[name]));
    }
    if (Object.keys(pathParameters).some(name => !requiredPaths.includes(name))) {
      throw new Error('Unexpected live-contract path parameter.');
    }
    const advertisedQuery = new Set(operation.parameters.filter(item => item.in === 'query').map(item => item.name));
    if (operation.parameters.some(item => item.in === 'query' && item.required && query[item.name] === undefined)) {
      throw new Error('Missing required live-contract query parameter.');
    }
    const url = new URL(path, this.target);
    for (const [name, value] of Object.entries(query)) {
      if (!advertisedQuery.has(name) || !['string', 'number', 'boolean'].includes(typeof value)) {
        throw new Error('Unexpected or invalid live-contract query parameter.');
      }
      url.searchParams.set(name, String(value));
    }
    if (!operation.requestBody && body !== undefined || operation.requestBody?.required && body === undefined) {
      throw new Error('The request body does not match the discovered operation.');
    }
    const call = await this.call(operation.method, url, body, { headers, signal });
    call.operationId = operationId;
    return call;
  }
}
