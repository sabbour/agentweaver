/// <reference types="node" />

import { createHash } from 'node:crypto';
import { gzipSync } from 'node:zlib';

const CONTENT_MEDIA_TYPE = 'application/vnd.agentsuite.content.v1.tar+gzip';

function bytes(value: string): Uint8Array {
  // Keep fixture serialization consistent with the browser's UTF-8 archive paths.
  return new TextEncoder().encode(value);
}

function concat(parts: Uint8Array[]): Uint8Array {
  // Join tar headers and bodies without relying on Node-only Buffer APIs.
  const length = parts.reduce((total, part) => total + part.byteLength, 0);
  const result = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.byteLength;
  }
  return result;
}

function writeString(target: Uint8Array, offset: number, length: number, value: string): void {
  // Tar fields are fixed-width byte ranges and are NUL-padded by the zeroed header.
  target.set(bytes(value).subarray(0, length), offset);
}

function tar(files: Array<{ path: string; content: Uint8Array; type?: string; linkName?: string }>): Uint8Array {
  // Emit deterministic ustar headers so the archive inspector tests exercise real tar bytes.
  const parts: Uint8Array[] = [];
  for (const file of files) {
    const header = new Uint8Array(512);
    writeString(header, 0, 100, file.path);
    writeString(header, 100, 8, '0000644');
    writeString(header, 108, 8, '0000000');
    writeString(header, 116, 8, '0000000');
    writeString(header, 124, 12, `${file.content.byteLength.toString(8).padStart(11, '0')}\0`);
    writeString(header, 136, 12, '00000000000');
    header.fill(0x20, 148, 156);
    header[156] = file.type?.charCodeAt(0) ?? 0x30;
    if (file.linkName) writeString(header, 157, 100, file.linkName);
    writeString(header, 257, 6, 'ustar\0');
    writeString(header, 263, 2, '00');
    const checksum = header.reduce((total, value) => total + value, 0);
    writeString(header, 148, 8, `${checksum.toString(8).padStart(6, '0')}\0 `);
    parts.push(header, file.content);
    const padding = (512 - file.content.byteLength % 512) % 512;
    if (padding > 0) parts.push(new Uint8Array(padding));
  }
  parts.push(new Uint8Array(1024));
  return concat(parts);
}

function suiteFiles(
  hasRemoteProvider: boolean,
  hasRemoteDependency: boolean,
): Array<{ path: string; content: Uint8Array }> {
  // The shallow graph has only the manifests that the browser must inspect before import.
  const providers = hasRemoteProvider
    ? [{ id: 'search', version: '1.0.0', path: 'providers/search.json' }]
    : [];
  const files = [
    {
      path: 'agentsuite.json',
      content: bytes(JSON.stringify({
        agents: [{ path: 'agents/writer.json' }],
        toolProviderCatalog: { path: 'catalog.json' },
      })),
    },
    {
      path: 'agents/writer.json',
      content: bytes(JSON.stringify({
        toolProviders: hasRemoteDependency ? [{ id: 'search', version: '1.0.0' }] : [],
      })),
    },
    { path: 'catalog.json', content: bytes(JSON.stringify({ toolProviders: providers })) },
  ];
  if (hasRemoteProvider) {
    files.push({
      path: 'providers/search.json',
      content: bytes(JSON.stringify({ remote: { endpointRef: 'portable-endpoint' } })),
    });
  }
  return files;
}

function toFile(content: Uint8Array, name: string, type: string): File {
  // Copy into an exact-sized ArrayBuffer so File never retains unrelated view bytes.
  const buffer = new ArrayBuffer(content.byteLength);
  new Uint8Array(buffer).set(content);
  return new File([buffer], name, { type });
}

function gzipContent(content: Uint8Array): Uint8Array {
  // Generate real compressed bytes so the test exercises the browser's gzip path.
  return new Uint8Array(gzipSync(content));
}

export function contentArchive(
  hasRemoteProvider = true,
  prefixPaths = false,
  hasRemoteDependency = hasRemoteProvider,
  includeLinks = false,
): File {
  // Produce the direct gzip content-layer format supported by the importer.
  const files: Array<{ path: string; content: Uint8Array; type?: string; linkName?: string }> =
    suiteFiles(hasRemoteProvider, hasRemoteDependency).map((file) => ({
    ...file,
    path: prefixPaths ? `./${file.path}` : file.path,
    }));
  if (includeLinks) {
    files.push(
      { path: 'payload/search-link', content: new Uint8Array(), type: '2', linkName: 'search' },
      { path: 'payload/search-hardlink', content: new Uint8Array(), type: '1', linkName: 'payload/search' },
    );
  }
  return toFile(gzipContent(tar(files)), 'team.tar.gz', 'application/gzip');
}

export function ociArchive(): File {
  // Build an OCI index and manifest that point to the same compressed suite content layer.
  const contentLayer = gzipContent(tar(suiteFiles(true, true)));
  const contentDigest = `sha256:${createHash('sha256').update(contentLayer).digest('hex')}`;
  const manifest = bytes(JSON.stringify({
    schemaVersion: 2,
    layers: [{ mediaType: CONTENT_MEDIA_TYPE, digest: contentDigest, size: contentLayer.byteLength }],
  }));
  const manifestDigest = `sha256:${createHash('sha256').update(manifest).digest('hex')}`;
  const index = bytes(JSON.stringify({
    schemaVersion: 2,
    manifests: [{
      artifactType: 'application/vnd.agentsuite.suite.v1',
      digest: manifestDigest,
      size: manifest.byteLength,
    }],
  }));
  const archive = tar([
    { path: 'index.json', content: index },
    { path: `blobs/sha256/${manifestDigest.slice(7)}`, content: manifest },
    { path: `blobs/sha256/${contentDigest.slice(7)}`, content: contentLayer },
  ]);
  return toFile(archive, 'team.oci.tar', 'application/x-tar');
}
