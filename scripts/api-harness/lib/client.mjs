// Thin Agentweaver REST API client for the persona-driven E2E harness.
//
// Every persona scenario drives Agentweaver exclusively through these calls —
// the same surface a human user's browser talks to. No browser automation.
//
// Auth: a bearer token (on staging, a GitHub token from `gh auth token`) is sent
// on every /api request, matching apps/Agentweaver.Api/API.md.

/**
 * @typedef {Object} ApiCall
 * @property {string} method
 * @property {string} path
 * @property {number} status
 * @property {number} ms
 * @property {any}    requestBody
 * @property {any}    responseBody
 * @property {boolean} ok
 * @property {?string} traceId  Backend correlation id from the response headers
 *                              (traceparent / request-id / x-request-id /
 *                              x-correlation-id), if present — lets a judge tie a
 *                              harness turn to App Insights / backend logs. null if
 *                              the server sent none. NOTE: the current staging
 *                              backend emits none (only istio-envoy proxy headers),
 *                              so this is best-effort and populates only if the API
 *                              starts returning a correlation header — an
 *                              observability gap worth filing.
 * @property {?number} upstreamMs Server-side processing time from the
 *                              `x-envoy-upstream-service-time` header (backend-only
 *                              latency, excludes network) — sharper P4 signal than
 *                              round-trip `ms`. null if the header is absent.
 */

import { assertTargetAllowed } from '../../harness-shared/target-guard.mjs';

// Response headers, in priority order, that carry a backend correlation id we can
// pin each turn to. traceparent (W3C) first — that's what App Insights ingests.
const CORRELATION_HEADERS = ['traceparent', 'request-id', 'x-request-id', 'x-correlation-id'];

export class AgentweaverClient {
  /**
   * @param {Object} opts
   * @param {string} opts.baseUrl   e.g. https://agentweaver.<zone>.westus2.staging.aksapp.io
   * @param {string} opts.token     bearer token
   * @param {boolean} [opts.insecure] skip TLS verification (staging self-signed / SAN drift)
   */
  constructor({ baseUrl, token, insecure = false, allowProd = false, confirmProduction = false }) {
    assertTargetAllowed(baseUrl, { allowProd, confirmProduction });
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.token = token;
    /** @type {ApiCall[]} */
    this.calls = [];
    if (insecure) {
      // Node's global fetch (undici) honours this env for TLS verification.
      process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
    }
  }

  /**
   * Perform a single API call, recording it for the evidence log.
   * Never throws on non-2xx — the scenario judge decides what a bad status means.
   * @returns {Promise<ApiCall>}
   */
  async call(method, path, body) {
    const url = path.startsWith('http') ? path : `${this.baseUrl}${path}`;
    const started = Date.now();
    /** @type {RequestInit} */
    const init = {
      method,
      headers: {
        Authorization: `Bearer ${this.token}`,
        Accept: 'application/json',
      },
    };
    if (body !== undefined) {
      init.headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }

    let status = 0;
    let responseBody = null;
    let traceId = null;
    let upstreamMs = null;
    try {
      const res = await fetch(url, init);
      status = res.status;
      for (const h of CORRELATION_HEADERS) {
        const v = res.headers.get(h);
        if (v) {
          traceId = v;
          break;
        }
      }
      const upstream = res.headers.get('x-envoy-upstream-service-time');
      if (upstream != null && upstream !== '') {
        const n = Number(upstream);
        if (Number.isFinite(n)) upstreamMs = n;
      }
      const text = await res.text();
      try {
        responseBody = text ? JSON.parse(text) : null;
      } catch {
        responseBody = text; // non-JSON (e.g. SSE / plain text)
      }
    } catch (err) {
      responseBody = { error: 'transport_error', message: String(err?.message ?? err) };
    }

    const record = {
      method,
      path: path.replace(this.baseUrl, ''),
      status,
      ms: Date.now() - started,
      requestBody: body ?? null,
      responseBody,
      ok: status >= 200 && status < 300,
      traceId,
      upstreamMs,
    };
    this.calls.push(record);
    return record;
  }

  get(path) {
    return this.call('GET', path);
  }
  post(path, body) {
    return this.call('POST', path, body);
  }
  put(path, body) {
    return this.call('PUT', path, body);
  }
  del(path) {
    return this.call('DELETE', path);
  }
}
