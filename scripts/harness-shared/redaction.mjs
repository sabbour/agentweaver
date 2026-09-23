const SENSITIVE_KEY = /authorization|cookie|token|credential|api[ _-]?key|execution[ _-]?key|provider[ _-]?key|secret|password|kubeconfig|storagestate|signedurl/i;
const SENSITIVE_DESCRIPTOR = /(?:^|[ _-])(?:authorization|cookie|token|credential|secret|password|kubeconfig|storagestate|signedurl)$/i;
const SENSITIVE_KEY_DESCRIPTOR = /(?:^|[ _-])(?:api|execution|provider)[ _-]?key$/i;
const SENSITIVE_STANDALONE_DESCRIPTOR = /^(?:api|provider)$/i;
const DESCRIPTOR_KEY = /^(?:descriptor|header|key|label|name|type)$/i;
const DESCRIPTOR_VALUE_KEY = /^(?:val|value|values)$/i;
const BEARER = /\bBearer\s+[A-Za-z0-9._~+/-]+=*/gi;
const JWT = /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g;
const GITHUB_TOKEN = /\bgh(?:p|o|u|s|r)_[A-Za-z0-9_]{20,}\b/g;
const URL_PATTERN = /https?:\/\/[^\s"'\\]+/gi;
const SECRET_ASSIGNMENT = /((?:authorization|cookie|token|credentials?|api[_-]?key|execution[_-]?key|provider[_-]?key|secret|password)\s*[:=]\s*["']?)(?:Bearer\s+)?[^"',;\s}\]]+/gi;
const SECRET_CANARY = /\b(?:(?:credential|secret|token|bearer)[_-]?canary|canary[_-]?(?:credential|secret|token|bearer))(?:[-_][A-Za-z0-9]+)*\b/gi;

export function sanitizeUrl(match) {
  try {
    const url = new URL(match);
    return `${url.origin}${url.pathname}`;
  } catch {
    return '[REDACTED_URL]';
  }
}

function redactSensitiveValue(value) {
  if (Array.isArray(value)) return value.map(redactSensitiveValue);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, redactSensitiveValue(item)]));
  }
  return '[REDACTED]';
}

function sensitiveDescriptorEntry(entries) {
  return entries.find(([key, item]) => DESCRIPTOR_KEY.test(key)
    && typeof item === 'string'
    && isSensitiveDescriptor(item));
}

function isSensitiveDescriptor(value) {
  const descriptor = value.trim();
  return SENSITIVE_DESCRIPTOR.test(descriptor)
    || SENSITIVE_KEY_DESCRIPTOR.test(descriptor)
    || SENSITIVE_STANDALONE_DESCRIPTOR.test(descriptor);
}

export function redact(value) {
  if (Array.isArray(value)) {
    if (value.length === 2 && typeof value[0] === 'string' && isSensitiveDescriptor(value[0])) {
      return [value[0], redactSensitiveValue(value[1])];
    }
    return value.map(redact);
  }
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
    const entries = Object.entries(value);
    const descriptor = sensitiveDescriptorEntry(entries);
    return Object.fromEntries(entries.map(([key, item]) => [
      key,
      SENSITIVE_KEY.test(key) || (descriptor && DESCRIPTOR_VALUE_KEY.test(key))
        ? redactSensitiveValue(item)
        : redact(item),
    ]));
  }
  if (typeof value === 'string' && /^\s*[\[{]/.test(value)) {
    try {
      return JSON.stringify(redact(JSON.parse(value)));
    } catch {
      // Not JSON; continue with text redaction.
    }
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
