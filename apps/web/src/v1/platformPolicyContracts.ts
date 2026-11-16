export const PLATFORM_POLICY_SCOPE_ID = 'default';
export const PLATFORM_POLICY_HISTORY_PAGE_SIZE = 20;
export const PLATFORM_POLICY_MAX_CURSOR_LENGTH = 2_048;
export const PLATFORM_POLICY_MAX_HTTP_BYTES = 5 * 1024 * 1024;

const MAX_MANIFEST_BYTES = 65_536;
const MAX_FILES = 64;
const MAX_FILE_BYTES = 262_144;
const MAX_ARTIFACT_BYTES = 1_048_576;
const MAX_HISTORY_ITEMS = 100;

export interface PlatformPolicyRevisionSummary {
  revision: number;
  contentSha256: string;
  createdAt: string;
}

export interface PlatformPolicyRevisionPage {
  scopeType: 'platform';
  scopeId: typeof PLATFORM_POLICY_SCOPE_ID;
  policyId: string | null;
  snapshotRevision: number | null;
  items: PlatformPolicyRevisionSummary[];
  nextCursor: string | null;
}

export interface PlatformPolicyArtifactText {
  manifest: string;
  files: { relativePath: string; content: string }[];
  contentSha256: string;
}

export interface PlatformPolicyRevisionReadResult {
  scopeType: 'platform';
  scopeId: typeof PLATFORM_POLICY_SCOPE_ID;
  policyId: string;
  revision: number;
  contentSha256: string;
  artifact: PlatformPolicyArtifactText;
  createdAt: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}

function isText(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function isHash(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
}

function isRevision(value: unknown): value is number {
  return Number.isSafeInteger(value) && (value as number) > 0;
}

function isTimestamp(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0 && Number.isFinite(Date.parse(value));
}

function hasControlCharacter(value: string): boolean {
  for (const character of value) {
    const code = character.charCodeAt(0);
    if (code <= 0x1f || code === 0x7f) return true;
  }
  return false;
}

function isRelativePath(value: unknown): value is string {
  return typeof value === 'string' &&
    value.length > 0 &&
    value.trim().length > 0 &&
    value.length <= 1_024 &&
    !value.startsWith('/') &&
    !value.includes('\\') &&
    !hasControlCharacter(value) &&
    !/^[A-Za-z]:/.test(value) &&
    value.split('/').every((part) => part.length > 0 && part !== '.' && part !== '..');
}

function readByteArray(value: unknown, maximum: number, allowEmpty = false): Uint8Array | null {
  if (!Array.isArray(value) || value.length > maximum || (!allowEmpty && value.length === 0))
    return null;
  const bytes = new Uint8Array(value.length);
  for (let index = 0; index < value.length; index += 1) {
    const byte = value[index];
    if (!Number.isInteger(byte) || (byte as number) < 0 || (byte as number) > 255)
      return null;
    bytes[index] = byte as number;
  }
  return bytes;
}

function decodeUtf8(bytes: Uint8Array): string | null {
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return null;
  }
}

export function parsePlatformPolicyRevisionPage(value: unknown): PlatformPolicyRevisionPage | null {
  if (!isRecord(value) ||
      !hasExactKeys(value, [
        'scopeType', 'scopeId', 'policyId', 'snapshotRevision', 'items', 'nextCursor',
      ]) ||
      value.scopeType !== 'platform' ||
      value.scopeId !== PLATFORM_POLICY_SCOPE_ID ||
      !(value.policyId === null || (isText(value.policyId) && value.policyId.length <= 256)) ||
      !(value.snapshotRevision === null || isRevision(value.snapshotRevision)) ||
      !Array.isArray(value.items) ||
      value.items.length > MAX_HISTORY_ITEMS ||
      !(value.nextCursor === null ||
        (typeof value.nextCursor === 'string' &&
          value.nextCursor.length > 0 &&
          value.nextCursor.length <= PLATFORM_POLICY_MAX_CURSOR_LENGTH)))
    return null;

  const empty = value.policyId === null && value.snapshotRevision === null;
  if (empty && (value.items.length !== 0 || value.nextCursor !== null)) return null;
  if ((value.policyId === null) !== (value.snapshotRevision === null)) return null;

  const seen = new Set<number>();
  const items: PlatformPolicyRevisionSummary[] = [];
  for (const itemValue of value.items) {
    if (!isRecord(itemValue) ||
        !hasExactKeys(itemValue, ['revision', 'contentSha256', 'createdAt']) ||
        !isRevision(itemValue.revision) ||
        itemValue.revision > (value.snapshotRevision ?? 0) ||
        !isHash(itemValue.contentSha256) ||
        !isTimestamp(itemValue.createdAt) ||
        seen.has(itemValue.revision))
      return null;
    seen.add(itemValue.revision);
    items.push({
      revision: itemValue.revision,
      contentSha256: itemValue.contentSha256,
      createdAt: itemValue.createdAt,
    });
  }

  return {
    scopeType: 'platform',
    scopeId: PLATFORM_POLICY_SCOPE_ID,
    policyId: value.policyId as string | null,
    snapshotRevision: value.snapshotRevision as number | null,
    items,
    nextCursor: value.nextCursor as string | null,
  };
}

export function parsePlatformPolicyRevisionReadResult(
  value: unknown,
): PlatformPolicyRevisionReadResult | null {
  if (!isRecord(value) ||
      !hasExactKeys(value, [
        'scopeType', 'scopeId', 'policyId', 'revision', 'contentSha256', 'artifact', 'createdAt',
      ]) ||
      value.scopeType !== 'platform' ||
      value.scopeId !== PLATFORM_POLICY_SCOPE_ID ||
      !isText(value.policyId) ||
      value.policyId.length > 256 ||
      !isRevision(value.revision) ||
      !isHash(value.contentSha256) ||
      !isTimestamp(value.createdAt) ||
      !isRecord(value.artifact) ||
      !hasExactKeys(value.artifact, ['manifestBytes', 'files', 'contentSha256']) ||
      !isHash(value.artifact.contentSha256) ||
      value.artifact.contentSha256 !== value.contentSha256 ||
      !Array.isArray(value.artifact.files) ||
      value.artifact.files.length > MAX_FILES)
    return null;

  const manifestBytes = readByteArray(value.artifact.manifestBytes, MAX_MANIFEST_BYTES);
  if (!manifestBytes) return null;
  const manifest = decodeUtf8(manifestBytes);
  if (manifest === null) return null;

  let totalBytes = manifestBytes.byteLength;
  const paths = new Set<string>();
  const files: { relativePath: string; content: string }[] = [];
  for (const fileValue of value.artifact.files) {
    if (!isRecord(fileValue) ||
        !hasExactKeys(fileValue, ['relativePath', 'bytes']) ||
        !isRelativePath(fileValue.relativePath) ||
        paths.has(fileValue.relativePath))
      return null;
    const bytes = readByteArray(fileValue.bytes, MAX_FILE_BYTES, true);
    if (!bytes) return null;
    totalBytes += bytes.byteLength;
    if (totalBytes > MAX_ARTIFACT_BYTES) return null;
    const content = decodeUtf8(bytes);
    if (content === null) return null;
    paths.add(fileValue.relativePath);
    files.push({ relativePath: fileValue.relativePath, content });
  }

  return {
    scopeType: 'platform',
    scopeId: PLATFORM_POLICY_SCOPE_ID,
    policyId: value.policyId,
    revision: value.revision,
    contentSha256: value.contentSha256,
    artifact: {
      manifest,
      files,
      contentSha256: value.artifact.contentSha256,
    },
    createdAt: value.createdAt,
  };
}
