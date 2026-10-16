/** Reads Docker archive metadata and verifies that it contains the requested image reference. */
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import path from 'node:path';

/**
 * Extracts the image config from a Docker archive and returns its digest and label metadata.
 * Invalid archives, missing references, or command failures throw.
 */
export function readDockerArchiveConfig(archive, imageReference) {
  const manifest = JSON.parse(execFileSync('tar', ['-xOf', archive, 'manifest.json'], {
    maxBuffer: 4 * 1024 * 1024,
  }).toString('utf8'));
  const matches = Array.isArray(manifest)
    ? manifest.filter(entry => Array.isArray(entry.RepoTags) && entry.RepoTags.includes(imageReference))
    : [];
  if (matches.length !== 1 || typeof matches[0].Config !== 'string' ||
      path.posix.isAbsolute(matches[0].Config) ||
      matches[0].Config.split('/').some(part => part === '.' || part === '..' || part === '') ||
      matches[0].Config.includes('\\')) {
    throw new Error('Docker archive must contain one exact tagged image and a safe config path');
  }

  const configPath = matches[0].Config;
  const bytes = execFileSync('tar', ['-xOf', archive, configPath], { maxBuffer: 4 * 1024 * 1024 });
  const configDigest = `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
  const digestHex = configDigest.slice(7);
  if (configPath !== `blobs/sha256/${digestHex}` &&
      path.posix.basename(configPath) !== `${digestHex}.json`) {
    throw new Error('Docker archive config bytes do not match their filename digest');
  }
  return { configDigest, config: JSON.parse(bytes.toString('utf8')) };
}
