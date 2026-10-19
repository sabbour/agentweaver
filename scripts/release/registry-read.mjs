import { setTimeout as sleep } from 'node:timers/promises';

/** Removes URL fields that can carry credentials from diagnostic output. */
function safeUrl(value) {
  const url = new URL(value);
  url.username = '';
  url.password = '';
  url.search = '';
  url.hash = '';
  return url.href;
}

/** Replaces complete URLs in transport errors with their credential-free form. */
function diagnostic(value) {
  return String(value).replace(/https?:\/\/[^\s"'<>]+/g, url => {
    try {
      return safeUrl(url);
    } catch {
      return '[redacted URL]';
    }
  });
}

/** Returns whether a TypeError describes a failed fetch or interrupted response body. */
function isNetworkTypeError(error) {
  return error instanceof TypeError &&
    (error.message === 'fetch failed' || error.message === 'terminated' ||
      typeof error.cause?.code === 'string');
}

/**
 * Reads one registry or pinned-manifest response with four total attempts.
 * Only network TypeErrors and HTTP 429/5xx retry; content and identity checks stay strict.
 */
export async function readRegistry(
  url, options, read, { fetchImpl = globalThis.fetch, delay = sleep } = {},
) {
  const requestUrl = safeUrl(url);
  for (let attempt = 1; attempt <= 4; attempt++) {
    let response;
    let failure;
    try {
      response = await fetchImpl(url, options);
      if (response.ok) return { response, value: await read(response) };
      failure = new Error(`HTTP ${response.status}`);
    } catch (error) {
      failure = error;
    }
    const transient = response && !response.ok
      ? response.status === 429 || (response.status >= 500 && response.status <= 599)
      : isNetworkTypeError(failure);
    // Release failed responses before another request (do not retain pooled sockets).
    if (response) await response.body?.cancel?.().catch(() => {});
    if (!transient || attempt === 4) {
      const cause = failure.cause;
      const detail = cause
        ? `; cause: ${cause.code ?? ''} ${cause.message ?? cause}`.trimEnd()
        : '';
      throw new Error(
        `Registry/manifest read failed for ${requestUrl} after ${attempt} attempt(s): ` +
        diagnostic(`${failure.message ?? failure}${detail}`),
        { cause: failure },
      );
    }
    // Four attempts total, with deterministic exponential backoff: 1s, 2s, 4s.
    await delay(1000 * 2 ** (attempt - 1));
  }
}
