export const PLATFORM_POLICY_SCOPE_ID = 'default';
export const PLATFORM_POLICY_HISTORY_PAGE_SIZE = 20;
export const PLATFORM_POLICY_MAX_CURSOR_LENGTH = 2_048;
export const PLATFORM_POLICY_MAX_HTTP_BYTES = 5 * 1024 * 1024;

const MAX_MANIFEST_BYTES = 65_536;
const MAX_FILES = 64;
const MAX_FILE_BYTES = 262_144;
const MAX_ARTIFACT_BYTES = 1_048_576;
const MAX_HISTORY_ITEMS = 100;
const MAX_DIAGNOSTICS = 32;
const MAX_DIAGNOSTIC_MESSAGE_LENGTH = 2_048;
const MAX_DIAGNOSTIC_PATH_LENGTH = 1_024;

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

export interface PlatformPolicyTextBuffer {
  value: string;
  lineEndings: string[];
}

export interface PlatformPolicyTextEdit {
  start: number;
  end: number;
  nextEnd: number;
}

export interface PlatformPolicyArtifactRequest {
  manifestBytes: number[];
  files: { relativePath: string; bytes: number[] }[];
  contentSha256: string;
}

export interface PlatformPolicyValidationDiagnostic {
  code: string;
  message: string;
  relativePath: string | null;
  line: number | null;
  column: number | null;
}

export interface PlatformPolicyValidationResult {
  isValid: boolean;
  diagnostics: PlatformPolicyValidationDiagnostic[];
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
    value.length <= 256 &&
    !value.startsWith('/') &&
    !value.includes('\\') &&
    !value.includes(':') &&
    !hasControlCharacter(value) &&
    value !== 'manifest.yaml' &&
    value.split('/').every((part) =>
      part.length > 0 &&
      part !== '.' &&
      part !== '..' &&
      /^[A-Za-z0-9._-]+$/.test(part));
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
    return new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
  } catch {
    return null;
  }
}

function countLineEndings(value: string, end: number): number {
  let count = 0;
  for (let index = 0; index < end; index += 1)
    if (value[index] === '\n') count += 1;
  return count;
}

export function createPlatformPolicyTextBuffer(value: string): PlatformPolicyTextBuffer {
  const lineEndings: string[] = [];
  const normalized = value.replace(/\r\n|\r|\n/g, (ending) => {
    lineEndings.push(ending);
    return '\n';
  });
  return { value: normalized, lineEndings };
}

export function updatePlatformPolicyTextBuffer(
  previous: PlatformPolicyTextBuffer,
  value: string,
  edit?: PlatformPolicyTextEdit,
): PlatformPolicyTextBuffer {
  const next = value.replace(/\r\n|\r/g, '\n');
  if (edit &&
      edit.start >= 0 &&
      edit.start <= edit.end &&
      edit.end <= previous.value.length &&
      edit.nextEnd >= edit.start &&
      edit.nextEnd <= next.length &&
      previous.value.slice(0, edit.start) === next.slice(0, edit.start) &&
      previous.value.slice(edit.end) === next.slice(edit.nextEnd)) {
    const prefixLineCount = countLineEndings(previous.value, edit.start);
    const oldSuffixLine = countLineEndings(previous.value, edit.end);
    const oldEditLineCount = oldSuffixLine - prefixLineCount;
    const newEditLineCount = countLineEndings(next, edit.nextEnd) - prefixLineCount;
    const preserveEditedEndings = oldEditLineCount === newEditLineCount;
    const insertedLineEnding = previous.lineEndings[prefixLineCount - 1] ??
      previous.lineEndings[prefixLineCount] ?? '\n';
    const lineEndings: string[] = [];
    let prefixLine = 0;
    let editedLine = 0;
    let suffixLine = 0;
    for (let index = 0; index < next.length; index += 1) {
      if (next[index] !== '\n') continue;
      if (index < edit.start) {
        lineEndings.push(previous.lineEndings[prefixLine] ?? insertedLineEnding);
        prefixLine += 1;
      } else if (index >= edit.nextEnd) {
        lineEndings.push(previous.lineEndings[oldSuffixLine + suffixLine] ?? insertedLineEnding);
        suffixLine += 1;
      } else {
        lineEndings.push(preserveEditedEndings
          ? previous.lineEndings[prefixLineCount + editedLine] ?? insertedLineEnding
          : insertedLineEnding);
        editedLine += 1;
      }
    }
    return { value: next, lineEndings };
  }

  if (previous.lineEndings.length === countLineEndings(next, next.length))
    return { value: next, lineEndings: [...previous.lineEndings] };

  let prefix = 0;
  while (prefix < previous.value.length &&
      prefix < next.length &&
      previous.value[prefix] === next[prefix])
    prefix += 1;

  let suffix = 0;
  while (suffix < previous.value.length - prefix &&
      suffix < next.length - prefix &&
      previous.value[previous.value.length - suffix - 1] === next[next.length - suffix - 1])
    suffix += 1;

  const oldSuffixStart = previous.value.length - suffix;
  const newSuffixStart = next.length - suffix;
  const prefixLineCount = countLineEndings(previous.value, prefix);
  const oldSuffixLine = countLineEndings(previous.value, oldSuffixStart);
  const newSuffixLine = countLineEndings(next, newSuffixStart);
  const insertedLineEnding = previous.lineEndings[prefixLineCount - 1] ??
    previous.lineEndings[prefixLineCount] ?? '\n';
  const lineEndings: string[] = [];
  let lineIndex = 0;
  for (let index = 0; index < next.length; index += 1) {
    if (next[index] !== '\n') continue;
    if (index < prefix) {
      lineEndings.push(previous.lineEndings[lineIndex] ?? insertedLineEnding);
    } else if (index >= newSuffixStart) {
      lineEndings.push(previous.lineEndings[oldSuffixLine + lineIndex - newSuffixLine] ??
        insertedLineEnding);
    } else {
      lineEndings.push(insertedLineEnding);
    }
    lineIndex += 1;
  }
  return { value: next, lineEndings };
}

export function serializePlatformPolicyTextBuffer(buffer: PlatformPolicyTextBuffer): string {
  let output = '';
  let lineIndex = 0;
  for (const character of buffer.value) {
    if (character === '\n') {
      const ending = buffer.lineEndings[lineIndex];
      if (!ending) throw new Error('Policy text line endings are invalid.');
      output += ending;
      lineIndex += 1;
    } else {
      output += character;
    }
  }
  if (lineIndex !== buffer.lineEndings.length)
    throw new Error('Policy text line endings are invalid.');
  return output;
}

function encodeUtf8(value: string): Uint8Array {
  const bytes = new TextEncoder().encode(value);
  if (new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes) !== value)
    throw new Error('Policy text must contain valid Unicode.');
  return bytes;
}

function appendLengthPrefixedField(
  output: Uint8Array,
  view: DataView,
  offset: number,
  field: Uint8Array,
): number {
  view.setUint32(offset, field.byteLength, false);
  output.set(field, offset + 4);
  return offset + 4 + field.byteLength;
}

export async function buildPlatformPolicyValidationArtifact(
  manifest: PlatformPolicyTextBuffer,
  files: { relativePath: string; content: PlatformPolicyTextBuffer }[],
): Promise<PlatformPolicyArtifactRequest> {
  if (files.length > MAX_FILES)
    throw new Error('A Platform Policy can contain up to 64 files.');

  const manifestBytes = encodeUtf8(serializePlatformPolicyTextBuffer(manifest));
  if (manifestBytes.byteLength > MAX_MANIFEST_BYTES)
    throw new Error('The Platform Policy manifest exceeds 65,536 bytes.');

  let totalBytes = manifestBytes.byteLength;
  const seenPaths = new Set<string>();
  const encodedFiles = files.map((file) => {
    if (!isRelativePath(file.relativePath))
      throw new Error('Each Platform Policy file needs a valid relative path.');
    if (seenPaths.has(file.relativePath))
      throw new Error('Platform Policy file paths must be unique.');
    seenPaths.add(file.relativePath);
    const pathBytes = encodeUtf8(file.relativePath);
    const bytes = encodeUtf8(serializePlatformPolicyTextBuffer(file.content));
    if (bytes.byteLength > MAX_FILE_BYTES)
      throw new Error('Each Platform Policy file must be 262,144 bytes or smaller.');
    totalBytes += bytes.byteLength;
    if (totalBytes > MAX_ARTIFACT_BYTES)
      throw new Error('The Platform Policy artifact exceeds 1,048,576 bytes.');
    return { relativePath: file.relativePath, pathBytes, bytes };
  });

  const sortedFiles = [...encodedFiles].sort((left, right) =>
    left.relativePath < right.relativePath ? -1 : left.relativePath > right.relativePath ? 1 : 0);
  for (const path of seenPaths) {
    const segments = path.split('/');
    for (let index = 1; index < segments.length; index += 1) {
      const parent = segments.slice(0, index).join('/');
      if (seenPaths.has(parent))
        throw new Error('Platform Policy file paths cannot conflict with directories.');
    }
  }
  const contentSha256 = await hashPlatformPolicyArtifact(
    manifestBytes,
    sortedFiles.map(({ pathBytes, bytes }) => ({ pathBytes, bytes })),
  );
  return {
    manifestBytes: Array.from(manifestBytes),
    files: encodedFiles.map(({ relativePath, bytes }) => ({
      relativePath,
      bytes: Array.from(bytes),
    })),
    contentSha256,
  };
}

async function hashPlatformPolicyArtifact(
  manifestBytes: Uint8Array,
  files: { pathBytes: Uint8Array; bytes: Uint8Array }[],
): Promise<string> {
  const fields = [manifestBytes, ...files.flatMap((file) => [file.pathBytes, file.bytes])];
  const prefix = encodeUtf8('agentweaver-native-acs-artifact-v1\0');
  const inputLength = prefix.byteLength + fields.reduce((length, field) => length + 4 + field.byteLength, 0);
  const input = new Uint8Array(inputLength);
  const view = new DataView(input.buffer);
  input.set(prefix);
  let offset = prefix.byteLength;
  for (const field of fields)
    offset = appendLengthPrefixedField(input, view, offset, field);

  if (!globalThis.crypto?.subtle)
    throw new Error('Policy validation is unavailable in this browser.');
  const digest = new Uint8Array(await globalThis.crypto.subtle.digest('SHA-256', input));
  return Array.from(digest, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

export interface PlatformPolicyDraftImport {
  manifest: PlatformPolicyTextBuffer;
  files: { relativePath: string; content: PlatformPolicyTextBuffer }[];
}

export async function parsePlatformPolicyValidationRequestFile(
  requestBytes: Uint8Array,
): Promise<PlatformPolicyDraftImport> {
  if (requestBytes.byteLength > PLATFORM_POLICY_MAX_HTTP_BYTES)
    throw new Error('The Policy validation request exceeds 5,242,880 bytes.');

  const json = decodeUtf8(requestBytes);
  if (json === null)
    throw new Error('The Policy validation request must contain UTF-8 JSON.');
  let value: unknown;
  try {
    value = JSON.parse(json.startsWith('\uFEFF') ? json.slice(1) : json);
  } catch {
    throw new Error('The selected Policy validation request is not valid JSON.');
  }
  if (!isRecord(value) || !hasExactKeys(value, ['artifact']) || !isRecord(value.artifact))
    throw new Error('The file must contain one complete {artifact} validation request.');
  const artifact = value.artifact;
  if (!hasExactKeys(artifact, ['manifestBytes', 'files', 'contentSha256']) ||
      !Array.isArray(artifact.files) ||
      artifact.files.length > MAX_FILES ||
      !isHash(artifact.contentSha256))
    throw new Error('The Policy validation request artifact is invalid.');

  const manifestBytes = readByteArray(artifact.manifestBytes, MAX_MANIFEST_BYTES);
  if (!manifestBytes)
    throw new Error('The Policy manifest exceeds 65,536 bytes or is invalid.');
  const manifest = decodeUtf8(manifestBytes);
  if (manifest === null)
    throw new Error('The Policy manifest must contain valid UTF-8 text.');

  const seenPaths = new Set<string>();
  let totalBytes = manifestBytes.byteLength;
  const files: {
    relativePath: string;
    content: string;
    pathBytes: Uint8Array;
    bytes: Uint8Array;
  }[] = [];
  for (const fileValue of artifact.files) {
    if (!isRecord(fileValue) ||
        !hasExactKeys(fileValue, ['relativePath', 'bytes']) ||
        !isRelativePath(fileValue.relativePath) ||
        seenPaths.has(fileValue.relativePath)) {
      throw new Error('The Policy validation request contains invalid or duplicate file paths.');
    }
    const bytes = readByteArray(fileValue.bytes, MAX_FILE_BYTES, true);
    if (!bytes)
      throw new Error('A Policy file exceeds 262,144 bytes or is invalid.');
    totalBytes += bytes.byteLength;
    if (totalBytes > MAX_ARTIFACT_BYTES)
      throw new Error('The Policy artifact exceeds 1,048,576 bytes.');
    const content = decodeUtf8(bytes);
    if (content === null)
      throw new Error(`Policy file ${fileValue.relativePath} must contain valid UTF-8 text.`);
    seenPaths.add(fileValue.relativePath);
    files.push({
      relativePath: fileValue.relativePath,
      content,
      pathBytes: encodeUtf8(fileValue.relativePath),
      bytes,
    });
  }
  for (const file of files) {
    const segments = file.relativePath.split('/');
    for (let index = 1; index < segments.length; index += 1) {
      if (seenPaths.has(segments.slice(0, index).join('/')))
        throw new Error('Policy file paths cannot conflict with directories.');
    }
  }
  const sorted = [...files].sort((left, right) =>
    left.relativePath < right.relativePath ? -1 : left.relativePath > right.relativePath ? 1 : 0);
  const hash = await hashPlatformPolicyArtifact(
    manifestBytes,
    sorted.map(({ pathBytes, bytes }) => ({ pathBytes, bytes })),
  );
  if (hash !== artifact.contentSha256)
    throw new Error('The Policy validation request hash does not match its artifact bytes.');

  return {
    manifest: createPlatformPolicyTextBuffer(manifest),
    files: files.map(({ relativePath, content }) => ({
      relativePath,
      content: createPlatformPolicyTextBuffer(content),
    })),
  };
}

function parseDiagnostics(value: unknown): PlatformPolicyValidationDiagnostic[] | null {
  if (!Array.isArray(value) || value.length > MAX_DIAGNOSTICS) return null;
  const diagnostics: PlatformPolicyValidationDiagnostic[] = [];
  for (const item of value) {
    if (!isRecord(item) ||
        !hasExactKeys(item, ['code', 'message', 'relativePath', 'line', 'column']) ||
        !isText(item.code) ||
        item.code.length > 128 ||
        typeof item.message !== 'string' ||
        item.message.length > MAX_DIAGNOSTIC_MESSAGE_LENGTH ||
        !(item.relativePath === null ||
          (typeof item.relativePath === 'string' &&
            item.relativePath.length <= MAX_DIAGNOSTIC_PATH_LENGTH &&
            !hasControlCharacter(item.relativePath))) ||
        !(item.line === null ||
          (Number.isSafeInteger(item.line) && (item.line as number) >= 0)) ||
        !(item.column === null ||
          (Number.isSafeInteger(item.column) && (item.column as number) >= 0)))
      return null;
    diagnostics.push({
      code: item.code,
      message: item.message,
      relativePath: item.relativePath as string | null,
      line: item.line as number | null,
      column: item.column as number | null,
    });
  }
  return diagnostics;
}

export function parsePlatformPolicyValidationResult(
  value: unknown,
): PlatformPolicyValidationResult | null {
  if (!isRecord(value)) return null;
  const diagnostics = parseDiagnostics(value.diagnostics);
  if (!diagnostics) return null;
  if (value.isValid === true)
    return { isValid: true, diagnostics };
  if (value.code === 'native_acs_policy_invalid')
    return { isValid: false, diagnostics };
  return null;
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
