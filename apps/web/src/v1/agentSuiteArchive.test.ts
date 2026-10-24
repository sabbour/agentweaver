/// <reference types="node" />

import { gunzipSync } from 'node:zlib';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { inspectAgentSuiteArchive } from './agentSuiteArchive';
import { contentArchive, ociArchive } from './__tests__/agentSuiteArchiveFixtures';

function stubDecompressionStream(): void {
  // Happy DOM does not consistently expose the browser gzip stream API.
  class TestDecompressionStream extends TransformStream<Uint8Array, Uint8Array> {
    constructor(format: string) {
      if (format !== 'gzip') throw new Error('Unsupported compression format.');
      const chunks: Uint8Array[] = [];
      super({
        transform(chunk) {
          chunks.push(new Uint8Array(chunk));
        },
        flush(controller) {
          const total = chunks.reduce((size, chunk) => size + chunk.byteLength, 0);
          const encoded = new Uint8Array(total);
          let offset = 0;
          for (const chunk of chunks) {
            encoded.set(chunk, offset);
            offset += chunk.byteLength;
          }
          controller.enqueue(new Uint8Array(gunzipSync(encoded)));
        },
      });
    }
  }
  vi.stubGlobal('DecompressionStream', TestDecompressionStream);
}

afterEach(() => vi.unstubAllGlobals());

describe('inspectAgentSuiteArchive', () => {
  it('discovers remote providers in a compressed content archive', async () => {
    stubDecompressionStream();
    await expect(inspectAgentSuiteArchive(contentArchive())).resolves.toEqual({
      format: 'content-tar-gzip',
      toolProviders: ['search@1.0.0'],
    });
  });

  it('reads the compressed content layer from an OCI layout', async () => {
    stubDecompressionStream();
    await expect(inspectAgentSuiteArchive(ociArchive())).resolves.toEqual({
      format: 'oci-layout-tar',
      toolProviders: ['search@1.0.0'],
    });
  });

  it('accepts a package with no remote provider dependencies', async () => {
    stubDecompressionStream();
    await expect(inspectAgentSuiteArchive(contentArchive(false))).resolves.toEqual({
      format: 'content-tar-gzip',
      toolProviders: [],
    });
  });

  it('does not request bindings for an unused remote provider in the catalog', async () => {
    stubDecompressionStream();
    await expect(inspectAgentSuiteArchive(contentArchive(true, false, false))).resolves.toEqual({
      format: 'content-tar-gzip',
      toolProviders: [],
    });
  });

  it('accepts valid tar entries with a leading current-directory prefix', async () => {
    stubDecompressionStream();
    await expect(inspectAgentSuiteArchive(contentArchive(true, true))).resolves.toEqual({
      format: 'content-tar-gzip',
      toolProviders: ['search@1.0.0'],
    });
  });

  it('inspects packages with symlink and hardlink payload entries', async () => {
    stubDecompressionStream();
    await expect(inspectAgentSuiteArchive(contentArchive(true, false, true, true))).resolves.toEqual({
      format: 'content-tar-gzip',
      toolProviders: ['search@1.0.0'],
    });
  });
});
