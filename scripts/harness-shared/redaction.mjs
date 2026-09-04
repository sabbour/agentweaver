const SENSITIVE_KEY = /authorization|cookie|token|api[_-]?key|secret|password|kubeconfig|storagestate|signedurl/i;
const BEARER = /\bBearer\s+[A-Za-z0-9._~+/-]+=*/gi;
const JWT = /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g;
const GITHUB_TOKEN = /\bgh(?:p|o|u|s|r)_[A-Za-z0-9_]{20,}\b/g;
const URL_PATTERN = /https?:\/\/[^\s"'\\]+/gi;
const SECRET_ASSIGNMENT = /((?:authorization|cookie|token|api[_-]?key|secret|password)\s*[:=]\s*["']?)(?:Bearer\s+)?[^"',;\s}\]]+/gi;
const SECRET_CANARY = /\b(?:(?:credential|secret|token|bearer)[_-]?canary|canary[_-]?(?:credential|secret|token|bearer))(?:[-_][A-Za-z0-9]+)*\b/gi;

export function sanitizeUrl(match) {
  try {
    const url = new URL(match);
    return `${url.origin}${url.pathname}`;
  } catch {
    return '[REDACTED_URL]';
  }
}

export function redact(value) {
  if (Array.isArray(value)) return value.map(redact);
  if (value instanceof Error) {
    return redact({
      name: value.name,
      message: value.message,
      stack: value.stack,
      cause: value.cause ?? null,
    });
  }
  if (typeof Headers !== 'undefined' && value instanceof Headers) {
    return redact(Object.fromEntries(value.entries()));
  }
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [
      key, SENSITIVE_KEY.test(key) ? '[REDACTED]' : redact(item),
    ]));
  }
  return typeof value === 'string'
    ? value
      .replace(URL_PATTERN, sanitizeUrl)
      .replace(BEARER, 'Bearer [REDACTED]')
      .replace(JWT, '[REDACTED_JWT]')
      .replace(GITHUB_TOKEN, '[REDACTED_GITHUB_TOKEN]')
      .replace(SECRET_ASSIGNMENT, '$1[REDACTED]')
      .replace(SECRET_CANARY, '[REDACTED_CANARY]')
    : value;
}
