const SENSITIVE_KEY = /authorization|cookie|token|api[_-]?key|secret|password|kubeconfig|storagestate|signedurl/i;
const SIGNED_URL = /https?:\/\/[^\s"'\\]+[?&](?:sig|signature|token)=[^\s"'\\]+/gi;
const BEARER = /\bBearer\s+[A-Za-z0-9._~+/-]+=*/gi;
const JWT = /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g;
const GITHUB_TOKEN = /\bgh(?:p|o|u|s|r)_[A-Za-z0-9_]{20,}\b/g;

export function redact(value) {
  if (Array.isArray(value)) return value.map(redact);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [
      key, SENSITIVE_KEY.test(key) ? '[REDACTED]' : redact(item),
    ]));
  }
  return typeof value === 'string'
    ? value
      .replace(SIGNED_URL, '[REDACTED_SIGNED_URL]')
      .replace(BEARER, 'Bearer [REDACTED]')
      .replace(JWT, '[REDACTED_JWT]')
      .replace(GITHUB_TOKEN, '[REDACTED_GITHUB_TOKEN]')
    : value;
}
