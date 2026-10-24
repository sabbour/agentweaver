import type { AgentSuiteImportRequest } from './contracts';

const MAX_ARCHIVE_BYTES = 16 * 1024 * 1024;
const MAX_EXPANDED_BYTES = 64 * 1024 * 1024;
const MAX_TAR_ENTRIES = 10_000;
const CONTENT_MEDIA_TYPE = 'application/vnd.agentsuite.content.v1.tar+gzip';
const SHA256_PATTERN = /^sha256:[0-9a-f]{64}$/;
const utf8 = new TextDecoder('utf-8', { fatal: true });

export interface InspectedAgentSuiteArchive {
  format: AgentSuiteImportRequest['format'];
  toolProviders: string[];
}

/** Reads only the suite catalog needed to bind remote providers before the server validates the archive. */
export async function inspectAgentSuiteArchive(file: File): Promise<InspectedAgentSuiteArchive> {
  // Apply the same compressed-size limit as the import service before reading user-selected bytes.
  if (file.size === 0 || file.size > MAX_ARCHIVE_BYTES) {
    throw new Error('Choose an AgentSuite archive that is between 1 byte and 16 MiB.');
  }
  const bytes = new Uint8Array(await file.arrayBuffer());
  if (bytes.byteLength !== file.size) {
    throw new Error('The selected file could not be read completely.');
  }

  let format: AgentSuiteImportRequest['format'];
  let contentFiles: Map<string, Uint8Array>;
  if (isGzip(bytes)) {
    format = 'content-tar-gzip';
    contentFiles = readTar(await gunzip(bytes));
  } else {
    format = 'oci-layout-tar';
    contentFiles = await readOciContent(bytes);
  }

  // The Gateway remains authoritative; this shallow parse only discovers connection binding slots.
  const suite = parseJsonFile(contentFiles, 'agentsuite.json');
  if (!Array.isArray(suite.agents)) {
    throw new Error('The suite does not contain an agent list.');
  }
  const requiredProviders = new Set<string>();
  for (const value of suite.agents) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) {
      throw new Error('The suite agent list is invalid.');
    }
    const reference = value as Record<string, unknown>;
    const path = requiredString(reference.path, 'An agent manifest path is missing.');
    const agent = parseJsonFile(contentFiles, path);
    if (agent.toolProviders === undefined) continue;
    if (!Array.isArray(agent.toolProviders)) {
      throw new Error('An agent tool provider list is invalid.');
    }
    for (const requirement of agent.toolProviders) {
      if (!requirement || typeof requirement !== 'object' || Array.isArray(requirement)) {
        throw new Error('An agent tool provider requirement is invalid.');
      }
      const toolProvider = requirement as Record<string, unknown>;
      requiredProviders.add(
        `${requiredString(toolProvider.id, 'A tool provider ID is missing.')}@` +
        requiredString(toolProvider.version, 'A tool provider version is missing.'),
      );
    }
  }
  const catalogPath = requiredString(
    (suite.toolProviderCatalog as Record<string, unknown> | undefined)?.path,
    'The suite does not contain a tool provider catalog.',
  );
  const catalog = parseJsonFile(contentFiles, catalogPath);
  if (!Array.isArray(catalog.toolProviders)) {
    throw new Error('The suite tool provider catalog is invalid.');
  }
  const toolProviders: string[] = [];
  for (const value of catalog.toolProviders) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) {
      throw new Error('The suite tool provider catalog is invalid.');
    }
    const reference = value as Record<string, unknown>;
    const id = requiredString(reference.id, 'A tool provider ID is missing.');
    const version = requiredString(reference.version, 'A tool provider version is missing.');
    if (!requiredProviders.has(`${id}@${version}`)) continue;
    const path = requiredString(reference.path, 'A tool provider manifest path is missing.');
    const manifest = parseJsonFile(contentFiles, path);
    if (manifest.remote && typeof manifest.remote === 'object' && !Array.isArray(manifest.remote)) {
      toolProviders.push(`${id}@${version}`);
    }
  }
  return { format, toolProviders: [...new Set(toolProviders)].sort() };
}

function isGzip(bytes: Uint8Array): boolean {
  // AgentSuite content archives start with the gzip magic bytes; OCI layouts are plain tar files.
  return bytes.length >= 2 && bytes[0] === 0x1f && bytes[1] === 0x8b;
}

async function gunzip(bytes: Uint8Array): Promise<Uint8Array> {
  // Decompress through a bounded stream so a small upload cannot expand without limit in the browser.
  if (typeof DecompressionStream === 'undefined') {
    throw new Error('This browser cannot inspect compressed AgentSuite archives.');
  }
  const copy = new Uint8Array(bytes.byteLength);
  copy.set(bytes);
  const stream = new Blob([copy.buffer]).stream().pipeThrough(new DecompressionStream('gzip'));
  const reader = stream.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const result = await reader.read();
      if (result.done) break;
      total += result.value.byteLength;
      if (total > MAX_EXPANDED_BYTES) {
        await reader.cancel();
        throw new Error('The expanded AgentSuite archive is larger than 64 MiB.');
      }
      chunks.push(result.value);
    }
  } finally {
    reader.releaseLock();
  }
  const output = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    output.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return output;
}

async function readOciContent(bytes: Uint8Array): Promise<Map<string, Uint8Array>> {
  // Read the OCI index and select the AgentSuite artifact's compressed content layer.
  const layoutFiles = readTar(bytes);
  const index = parseJsonFile(layoutFiles, 'index.json');
  if (!Array.isArray(index.manifests)) {
    throw new Error('The OCI layout does not contain an AgentSuite artifact.');
  }
  const descriptor = index.manifests.find((value) =>
    value && typeof value === 'object' && !Array.isArray(value) &&
    (value as Record<string, unknown>).artifactType === 'application/vnd.agentsuite.suite.v1');
  if (!descriptor || typeof descriptor !== 'object' || Array.isArray(descriptor)) {
    throw new Error('The OCI layout does not contain an AgentSuite artifact.');
  }
  const manifestDigest = requiredDigest(
    (descriptor as Record<string, unknown>).digest,
    'The OCI artifact manifest digest is invalid.',
  );
  const manifestPath = `blobs/sha256/${manifestDigest.slice(7)}`;
  const manifest = parseJsonFile(layoutFiles, manifestPath);
  if (!Array.isArray(manifest.layers)) {
    throw new Error('The OCI artifact does not contain content layers.');
  }
  const layer = manifest.layers.find((value) =>
    value && typeof value === 'object' && !Array.isArray(value) &&
    (value as Record<string, unknown>).mediaType === CONTENT_MEDIA_TYPE);
  if (!layer || typeof layer !== 'object' || Array.isArray(layer)) {
    throw new Error('The OCI artifact does not contain an AgentSuite content layer.');
  }
  const contentDigest = requiredDigest(
    (layer as Record<string, unknown>).digest,
    'The OCI content layer digest is invalid.',
  );
  const content = layoutFiles.get(`blobs/sha256/${contentDigest.slice(7)}`);
  if (!content) {
    throw new Error('The OCI content layer is missing.');
  }
  return readTar(await gunzip(content));
}

function readTar(bytes: Uint8Array): Map<string, Uint8Array> {
  // Parse only regular files and reject malformed paths before any file content is inspected.
  if (bytes.byteLength > MAX_EXPANDED_BYTES) {
    throw new Error('The expanded AgentSuite archive is larger than 64 MiB.');
  }
  const files = new Map<string, Uint8Array>();
  const decoder = utf8;
  let offset = 0;
  let entries = 0;
  while (offset + 512 <= bytes.byteLength) {
    const header = bytes.subarray(offset, offset + 512);
    if (header.every((value) => value === 0)) break;
    entries++;
    if (entries > MAX_TAR_ENTRIES) {
      throw new Error('The AgentSuite archive contains too many entries.');
    }
    const name = readTarString(decoder, header, 0, 100);
    const prefix = readTarString(decoder, header, 345, 155);
    const rawPath = prefix ? `${prefix}/${name}` : name;
    const path = rawPath.replace(/^(?:\.\/)+/, '');
    const type = header[156] === 0 ? '0' : String.fromCharCode(header[156]);
    const size = readTarOctal(decoder, header.subarray(124, 136));
    if (!path || path.startsWith('/') || path.includes('\\') ||
        path.split('/').some(segment => segment === '.' || segment === '..')) {
      throw new Error('The AgentSuite archive contains an unsafe path.');
    }
    if (size > MAX_EXPANDED_BYTES || offset + 512 + size > bytes.byteLength) {
      throw new Error('The AgentSuite archive contains an invalid file size.');
    }
    if ((type === '1' || type === '2') && size !== 0) {
      throw new Error('The AgentSuite archive contains an invalid link entry.');
    }
    const dataStart = offset + 512;
    if (type === '0') {
      if (files.has(path)) throw new Error('The AgentSuite archive contains duplicate paths.');
      files.set(path, bytes.slice(dataStart, dataStart + size));
    } else if (type !== '5' && type !== '1' && type !== '2') {
      throw new Error('The AgentSuite archive contains an unsupported tar entry.');
    }
    offset = dataStart + Math.ceil(size / 512) * 512;
  }
  return files;
}

function readTarString(decoder: TextDecoder, header: Uint8Array, start: number, length: number): string {
  // Tar header strings end at the first NUL and use UTF-8 for non-ASCII paths.
  const value = header.subarray(start, start + length);
  const end = value.indexOf(0);
  return decoder.decode(end < 0 ? value : value.subarray(0, end));
}

function readTarOctal(decoder: TextDecoder, field: Uint8Array): number {
  // AgentSuite content and OCI layouts use standard octal tar sizes, not base-256 extensions.
  const value = decoder.decode(field).replace(/\0.*$/s, '').trim();
  if (!/^[0-7]*$/.test(value)) throw new Error('The AgentSuite archive contains an invalid tar size.');
  const parsed = value ? Number.parseInt(value, 8) : 0;
  if (!Number.isSafeInteger(parsed) || parsed < 0) {
    throw new Error('The AgentSuite archive contains an invalid tar size.');
  }
  return parsed;
}

function parseJsonFile(files: Map<string, Uint8Array>, path: string): Record<string, unknown> {
  // Decode only files named by the suite graph; all archive validation still runs on the server.
  const bytes = files.get(path);
  if (!bytes) throw new Error(`The AgentSuite archive is missing '${path}'.`);
  try {
    const parsed: unknown = JSON.parse(utf8.decode(bytes));
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      throw new Error('Expected a JSON object.');
    }
    return parsed as Record<string, unknown>;
  } catch {
    throw new Error(`The AgentSuite archive contains invalid JSON in '${path}'.`);
  }
}

function requiredString(value: unknown, message: string): string {
  // Paths and identities are read as strings only; the server checks their full native grammar.
  if (typeof value !== 'string' || value.length === 0) throw new Error(message);
  return value;
}

function requiredDigest(value: unknown, message: string): string {
  // Only lowercase SHA-256 blob names can be used to select data from the OCI layout.
  const digest = requiredString(value, message);
  if (!SHA256_PATTERN.test(digest)) throw new Error(message);
  return digest;
}
