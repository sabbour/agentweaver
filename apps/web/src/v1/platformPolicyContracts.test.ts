import { describe, expect, it } from 'vitest';
import {
  buildPlatformPolicyValidationArtifact,
  createPlatformPolicyTextBuffer,
  parsePlatformPolicyRevisionPage,
  parsePlatformPolicyRevisionReadResult,
  parsePlatformPolicyValidationRequestFile,
  parsePlatformPolicyValidationResult,
  serializePlatformPolicyTextBuffer,
  updatePlatformPolicyTextBuffer,
} from './platformPolicyContracts';

const hash = 'a'.repeat(64);

function bytes(value: string): number[] {
  return [...new TextEncoder().encode(value)];
}

function detail(overrides: Record<string, unknown> = {}) {
  return {
    scopeType: 'platform',
    scopeId: 'default',
    policyId: 'policy-1',
    revision: 3,
    contentSha256: hash,
    createdAt: '2026-01-02T03:04:05Z',
    artifact: {
      manifestBytes: bytes('name: example\n'),
      files: [{ relativePath: 'rules/example.yaml', bytes: bytes('allow: false\n') }],
      contentSha256: hash,
    },
    ...overrides,
  };
}

function history(overrides: Record<string, unknown> = {}) {
  return {
    scopeType: 'platform',
    scopeId: 'default',
    policyId: 'policy-1',
    snapshotRevision: 3,
    items: [{ revision: 3, contentSha256: hash, createdAt: '2026-01-02T03:04:05Z' }],
    nextCursor: null,
    ...overrides,
  };
}

describe('Platform Policy response contracts', () => {
  it('preserves BOMs and mixed line endings while building the canonical validation hash', async () => {
    const manifest = createPlatformPolicyTextBuffer('\uFEFFname: x\r\n');
    const content = createPlatformPolicyTextBuffer('allow=true\r');
    const editedContent = updatePlatformPolicyTextBuffer(content, 'allow=false\n');
    expect(serializePlatformPolicyTextBuffer(manifest)).toBe('\uFEFFname: x\r\n');
    expect(serializePlatformPolicyTextBuffer(editedContent)).toBe('allow=false\r');

    const artifact = await buildPlatformPolicyValidationArtifact(manifest, [
      { relativePath: 'rules/a.rego', content },
    ]);
    expect(artifact.manifestBytes).toEqual(bytes('\uFEFFname: x\r\n'));
    expect(artifact.files[0]?.bytes).toEqual(bytes('allow=true\r'));
    expect(artifact.contentSha256)
      .toBe('f9591534f88ca70e815637008d4841a94fe4c58b622582a9078ea32be3844357');
  });

  it('imports one exact validation request and preserves its bytes', async () => {
    const manifest = createPlatformPolicyTextBuffer('\uFEFFname: x\r\n');
    const artifact = await buildPlatformPolicyValidationArtifact(manifest, [
      { relativePath: 'rules/a.rego', content: createPlatformPolicyTextBuffer('allow=true\r') },
    ]);
    const requestBytes = new TextEncoder().encode(
      `\uFEFF${JSON.stringify({ artifact })}\r\n`,
    );
    const imported = await parsePlatformPolicyValidationRequestFile(requestBytes);
    expect(serializePlatformPolicyTextBuffer(imported.manifest)).toBe('\uFEFFname: x\r\n');
    expect(serializePlatformPolicyTextBuffer(imported.files[0].content)).toBe('allow=true\r');
    expect(await buildPlatformPolicyValidationArtifact(
      imported.manifest,
      imported.files,
    )).toEqual(artifact);

    await expect(parsePlatformPolicyValidationRequestFile(new TextEncoder().encode(
      JSON.stringify({
        artifact: { ...artifact, contentSha256: 'b'.repeat(64) },
      }),
    ))).rejects.toThrow('hash does not match');
  });

  it('keeps unchanged mixed separators when a browser-normalized textarea value changes', () => {
    const original = createPlatformPolicyTextBuffer('first\r\nsecond\rthird\n');
    const edited = updatePlatformPolicyTextBuffer(original, 'first\nsecond changed\nthird\n');
    expect(serializePlatformPolicyTextBuffer(edited)).toBe('first\r\nsecond changed\rthird\n');
  });

  it('preserves mixed separators across multiline edits and adjacent-line deletion', () => {
    const original = createPlatformPolicyTextBuffer('a\r\nb\rc\n');
    const multilineEdit = updatePlatformPolicyTextBuffer(
      original,
      'a\nB\nC\n',
      { start: 2, end: 5, nextEnd: 5 },
    );
    expect(serializePlatformPolicyTextBuffer(multilineEdit)).toBe('a\r\nB\rC\n');

    const adjacent = createPlatformPolicyTextBuffer('first\r\n\rthird\n');
    const deletedFirstSeparator = updatePlatformPolicyTextBuffer(
      adjacent,
      'first\nthird\n',
      { start: 5, end: 6, nextEnd: 5 },
    );
    expect(serializePlatformPolicyTextBuffer(deletedFirstSeparator)).toBe('first\rthird\n');
  });

  it('rejects duplicate or unsafe draft paths and artifacts over the request bounds', async () => {
    const manifest = createPlatformPolicyTextBuffer('name: test');
    const content = createPlatformPolicyTextBuffer('rule');
    await expect(buildPlatformPolicyValidationArtifact(manifest, [
      { relativePath: 'rules/../unsafe.rego', content },
    ])).rejects.toThrow('valid relative path');
    await expect(buildPlatformPolicyValidationArtifact(manifest, [
      { relativePath: 'manifest.yaml', content },
    ])).rejects.toThrow('valid relative path');
    await expect(buildPlatformPolicyValidationArtifact(manifest, [
      { relativePath: 'rules/bad:name.rego', content },
    ])).rejects.toThrow('valid relative path');
    await expect(buildPlatformPolicyValidationArtifact(manifest, [
      { relativePath: 'rules/a.rego', content },
      { relativePath: 'rules/a.rego', content },
    ])).rejects.toThrow('must be unique');
    await expect(buildPlatformPolicyValidationArtifact(manifest, [
      { relativePath: 'rules', content },
      { relativePath: 'rules/example.rego', content },
    ])).rejects.toThrow('conflict with directories');
    await expect(buildPlatformPolicyValidationArtifact(manifest, [
      { relativePath: 'rules/a.rego', content: createPlatformPolicyTextBuffer('x'.repeat(262_145)) },
    ])).rejects.toThrow('262,144 bytes or smaller');
  });

  it('accepts valid and invalid server results and rejects malformed diagnostics', () => {
    const diagnostic = {
      code: 'yaml.invalid',
      message: 'The manifest is invalid.',
      relativePath: null,
      line: 2,
      column: 1,
    };
    expect(parsePlatformPolicyValidationResult({
      isValid: true,
      diagnostics: [],
    })).toEqual({ isValid: true, diagnostics: [] });
    expect(parsePlatformPolicyValidationResult({
      title: 'native_acs_policy_invalid',
      code: 'native_acs_policy_invalid',
      diagnostics: [diagnostic],
    })).toEqual({ isValid: false, diagnostics: [diagnostic] });
    expect(parsePlatformPolicyValidationResult({
      code: 'native_acs_policy_invalid',
      diagnostics: [{ ...diagnostic, message: 'x'.repeat(2_049) }],
    })).toBeNull();
    expect(parsePlatformPolicyValidationResult({
      isValid: true,
      diagnostics: Array.from({ length: 33 }, () => diagnostic),
    })).toBeNull();
  });

  it('accepts an authorized empty history and preserves the exact snapshot head', () => {
    expect(parsePlatformPolicyRevisionPage({
      scopeType: 'platform',
      scopeId: 'default',
      policyId: 'policy-1',
      snapshotRevision: 4,
      items: [
        { revision: 3, contentSha256: hash, createdAt: '2026-01-02T03:04:05Z' },
        { revision: 4, contentSha256: hash, createdAt: '2026-01-03T03:04:05Z' },
      ],
      nextCursor: null,
    })?.snapshotRevision).toBe(4);
    expect(parsePlatformPolicyRevisionPage({
      scopeType: 'platform',
      scopeId: 'default',
      policyId: null,
      snapshotRevision: null,
      items: [],
      nextCursor: null,
    })?.items).toEqual([]);
  });

  it('rejects duplicate revisions and leaves snapshot-head checks to the controller', () => {
    const item = { revision: 2, contentSha256: hash, createdAt: '2026-01-02T03:04:05Z' };
    expect(parsePlatformPolicyRevisionPage({
      scopeType: 'platform', scopeId: 'default', policyId: 'policy-1',
      snapshotRevision: 2, items: [item, item], nextCursor: null,
    })).toBeNull();
    expect(parsePlatformPolicyRevisionPage({
      scopeType: 'platform', scopeId: 'default', policyId: 'policy-1',
      snapshotRevision: 3, items: [item], nextCursor: null,
    })).not.toBeNull();
  });

  it('decodes bounded source as strict UTF-8 and rejects inconsistent hashes and paths', () => {
    const parsed = parsePlatformPolicyRevisionReadResult(detail());
    expect(parsed?.artifact.manifest).toBe('name: example\n');
    expect(parsed?.artifact.files[0]?.content).toBe('allow: false\n');
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: bytes('\uFEFFname: example\n'),
        files: [],
        contentSha256: hash,
      },
    }))?.artifact.manifest).toBe('\uFEFFname: example\n');
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: [0xc3, 0x28],
        files: [],
        contentSha256: hash,
      },
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: bytes('name: example\n'),
        files: [{ relativePath: '../secret', bytes: bytes('text') }],
        contentSha256: hash,
      },
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: bytes('name: example\n'),
        files: [{ relativePath: 'rules/invalid\u0001.yaml', bytes: bytes('text') }],
        contentSha256: hash,
      },
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: bytes('name: example\n'),
        files: [],
        contentSha256: 'b'.repeat(64),
      },
    }))).toBeNull();
  });

  it('accepts the exact cursor, manifest, file-count, file-size, and aggregate limits', () => {
    expect(parsePlatformPolicyRevisionPage(history({
      nextCursor: 'c'.repeat(2_048),
    }))?.nextCursor).toHaveLength(2_048);
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: new Array(65_536).fill(97),
        files: Array.from({ length: 64 }, (_, index) => ({
          relativePath: `rules/${index}.rego`,
          bytes: [],
        })),
        contentSha256: hash,
      },
    }))?.artifact.files).toHaveLength(64);
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: [97],
        files: [{ relativePath: 'rules/max.rego', bytes: new Array(262_144).fill(97) }],
        contentSha256: hash,
      },
    }))?.artifact.files[0]?.content).toHaveLength(262_144);
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: new Array(65_536).fill(97),
        files: [
          { relativePath: 'rules/1.rego', bytes: new Array(262_144).fill(97) },
          { relativePath: 'rules/2.rego', bytes: new Array(262_144).fill(97) },
          { relativePath: 'rules/3.rego', bytes: new Array(262_144).fill(97) },
          { relativePath: 'rules/4.rego', bytes: new Array(196_608).fill(97) },
        ],
        contentSha256: hash,
      },
    }))?.artifact.manifest).toHaveLength(65_536);
  });

  it('rejects values over the byte limits and unsafe revision integers', () => {
    expect(parsePlatformPolicyRevisionPage(history({
      nextCursor: 'c'.repeat(2_049),
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionPage(history({
      snapshotRevision: Number.MAX_SAFE_INTEGER + 1,
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionPage(history({
      items: [{
        revision: Number.MAX_SAFE_INTEGER + 1,
        contentSha256: hash,
        createdAt: '2026-01-02T03:04:05Z',
      }],
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      revision: Number.MAX_SAFE_INTEGER + 1,
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: new Array(65_537).fill(97),
        files: [],
        contentSha256: hash,
      },
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: [97],
        files: Array.from({ length: 65 }, (_, index) => ({
          relativePath: `rules/${index}.rego`,
          bytes: [],
        })),
        contentSha256: hash,
      },
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: [97],
        files: [{ relativePath: 'rules/max.rego', bytes: new Array(262_145).fill(97) }],
        contentSha256: hash,
      },
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: new Array(65_536).fill(97),
        files: [
          { relativePath: 'rules/1.rego', bytes: new Array(262_144).fill(97) },
          { relativePath: 'rules/2.rego', bytes: new Array(262_144).fill(97) },
          { relativePath: 'rules/3.rego', bytes: new Array(262_144).fill(97) },
          { relativePath: 'rules/4.rego', bytes: new Array(196_609).fill(97) },
        ],
        contentSha256: hash,
      },
    }))).toBeNull();
  });

  it('rejects non-byte integers and invalid UTF-8 file content', () => {
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: [97],
        files: [{ relativePath: 'rules/invalid.rego', bytes: [256] }],
        contentSha256: hash,
      },
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: [97],
        files: [{ relativePath: 'rules/invalid.rego', bytes: [-1] }],
        contentSha256: hash,
      },
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: [97],
        files: [{ relativePath: 'rules/invalid.rego', bytes: [1.5] }],
        contentSha256: hash,
      },
    }))).toBeNull();
    expect(parsePlatformPolicyRevisionReadResult(detail({
      artifact: {
        manifestBytes: [97],
        files: [{ relativePath: 'rules/invalid.rego', bytes: [0xc3, 0x28] }],
        contentSha256: hash,
      },
    }))).toBeNull();
  });
});
