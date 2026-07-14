const LOCAL_HOSTS = new Set(['localhost', '127.0.0.1', '::1']);

export function isAllowedTargetHost(host) {
  const normalized = String(host ?? '').toLowerCase().replace(/\.$/, '');
  return LOCAL_HOSTS.has(normalized) || normalized.endsWith('.localhost') ||
    normalized.includes('.staging.') || normalized.endsWith('.staging');
}

/**
 * Reject non-local, non-staging targets unless production was deliberately
 * double-confirmed. This belongs at transport construction so no caller can
 * bypass it by avoiding CLI parsing.
 */
export function assertTargetAllowed(baseUrl, { allowProd = false, confirmProduction = false } = {}) {
  let host;
  try {
    host = new URL(baseUrl).hostname;
  } catch {
    throw new Error(`target "${baseUrl}" is not a valid URL`);
  }
  if (isAllowedTargetHost(host)) return;
  if (allowProd && confirmProduction) return;
  throw new Error(
    `refusing non-staging target "${host}"; use both --allow-prod and ` +
    '--i-understand-this-targets-production to override',
  );
}
