import { collectFilesFromEntry, isUploadableSkillFile } from '../utils/skillDrop';
import { describe, expect, it } from 'vitest';
import type { FsEntry } from '../utils/skillDrop';
function fileEntry(name: string, content = 'x'): FsEntry {
  const file = new File([content], name, { type: 'text/markdown' });
  return {
    isFile: true,
    isDirectory: false,
    name,
    file: (success) => success(file),
  };
}

// Directory reader that returns children in batches then an empty array, mirroring the real API.
function dirEntry(name: string, children: FsEntry[]): FsEntry {
  return {
    isFile: false,
    isDirectory: true,
    name,
    createReader: () => {
      let served = false;
      return {
        readEntries: (success) => {
          if (served) {
            success([]);
            return;
          }
          served = true;
          success(children);
        },
      };
    },
  };
}

describe('collectFilesFromEntry', () => {
  it('reads a folder containing SKILL.md and yields the correct relative path', async () => {
    const entry = dirEntry('code-review', [fileEntry('SKILL.md')]);

    const files = await collectFilesFromEntry(entry);

    expect(files).toHaveLength(1);
    expect(files[0].relativePath).toBe('code-review/SKILL.md');
    expect(files[0].file.name).toBe('SKILL.md');
  });

  it('recurses into nested directories, preserving relative paths', async () => {
    const entry = dirEntry('code-review', [
      fileEntry('SKILL.md'),
      dirEntry('templates', [fileEntry('pr.md')]),
    ]);

    const files = await collectFilesFromEntry(entry);
    const paths = files.map((f) => f.relativePath).sort();

    expect(paths).toEqual(['code-review/SKILL.md', 'code-review/templates/pr.md']);
  });

  it('reads a bare dropped file with no folder prefix', async () => {
    const files = await collectFilesFromEntry(fileEntry('SKILL.md'));

    expect(files).toHaveLength(1);
    expect(files[0].relativePath).toBe('SKILL.md');
  });
});

describe('isUploadableSkillFile', () => {
  it('accepts small text files', () => {
    expect(isUploadableSkillFile(new File(['# hi'], 'SKILL.md'))).toBe(true);
  });

  it('rejects binary extensions', () => {
    expect(isUploadableSkillFile(new File([''], 'logo.png'))).toBe(false);
  });

  it('rejects oversized files', () => {
    const big = new File(['a'], 'big.md');
    Object.defineProperty(big, 'size', { value: 5 * 1024 * 1024 });
    expect(isUploadableSkillFile(big)).toBe(false);
  });
});
