const SENSITIVE_KEY = /authorization|cookie|token|api[_-]?key|secret|password|kubeconfig|storagestate|signedurl/i;
const SIGNED_URL = /https?:\/\/[^\s"'\\]+[?&](?:sig|signature|token)=[^\s"'\\]+/gi;

export function redact(value) {
  if (Array.isArray(value)) return value.map(redact);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [
      key, SENSITIVE_KEY.test(key) ? '[REDACTED]' : redact(item),
    ]));
  }
  return typeof value === 'string' ? value.replace(SIGNED_URL, '[REDACTED_SIGNED_URL]') : value;
}
