import { isIP } from 'node:net';

function normalizedHostname(url) {
  return url.hostname.toLowerCase().replace(/^\[|\]$/g, '').replace(/\.$/, '');
}

export function isLoopbackTarget(urlOrHostname) {
  const hostname = urlOrHostname instanceof URL
    ? normalizedHostname(urlOrHostname)
    : String(urlOrHostname ?? '').toLowerCase().replace(/^\[|\]$/g, '').replace(/\.$/, '');
  if (hostname === 'localhost' || hostname.endsWith('.localhost') || hostname === '::1') return true;
  if (isIP(hostname) === 4) return hostname.split('.')[0] === '127';
  return false;
}

/**
 * Validate a harness network target without guessing an environment from its hostname.
 * TLS always uses Node/Playwright's normal certificate validation.
 */
export function validateNetworkTarget(target, { exactPath } = {}) {
  let url;
  try {
    url = new URL(target);
  } catch {
    throw new Error(`target "${target}" must be an absolute http:// or https:// URL`);
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new Error(`target protocol "${url.protocol}" is unsupported; use http:// or https://`);
  }
  if (!url.hostname) throw new Error('target must include a hostname');
  if (url.username || url.password) throw new Error('target must not contain URL credentials/userinfo');
  if (url.hash) throw new Error('target must not contain a URL fragment');
  if (url.protocol !== 'https:' && !isLoopbackTarget(url)) {
    throw new Error('HTTPS is required for non-loopback targets');
  }
  if (exactPath && (url.pathname !== exactPath || url.search)) {
    throw new Error(`target path must be exactly "${exactPath}"`);
  }
  return url;
}

export function networkTargetEvidence(target, { surface, authSource = 'none', exactPath } = {}) {
  if (target === 'stdio') {
    return {
      surface,
      transport: 'stdio',
      targetOrigin: null,
      targetPath: null,
      authSource,
      projectId: null,
      runId: null,
      cleanupIntent: 'none',
      cleanupResult: 'not-started',
      tlsMode: 'not-applicable',
    };
  }
  const url = validateNetworkTarget(target, { exactPath });
  return {
    surface,
    transport: 'http',
    targetOrigin: url.origin,
    targetPath: `${url.pathname}${url.search}`,
    authSource,
    projectId: null,
    runId: null,
    cleanupIntent: 'none',
    cleanupResult: 'not-started',
    tlsMode: 'system-default',
  };
}
