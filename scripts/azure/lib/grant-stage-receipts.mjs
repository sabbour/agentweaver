import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmdirSync, rmSync } from 'node:fs';
import path from 'node:path';

// Receipts written by the P1 initializer's grant stage. They land under the checkout's Temp directory.
const GRANT_STAGE_RECEIPT = /^p1-postgres-[a-f0-9]{32}\.jsonl$/;

/** Returns true only when `directory` resolves outside the `root` checkout. */
export function isOutsideCheckout(root, directory) {
  const relative = path.relative(path.resolve(root), path.resolve(directory));
  // A different drive gives an absolute relative path. Otherwise only `..` itself or a `../` prefix
  // leaves the checkout; a name such as `..receipts` is a folder inside it.
  return path.isAbsolute(relative) || relative === '..' || relative.startsWith(`..${path.sep}`);
}

/**
 * Moves P1 initializer grant-stage receipts out of the checkout's Temp directory and into
 * `destinationDirectory`. Returns each moved receipt with its SHA-256.
 */
export function collectGrantStageReceipts(root, destinationDirectory, runId) {
  const temp = path.join(root, 'Temp');
  if (!existsSync(temp)) return [];
  // Callers start from a fully clean checkout, so every matching file here came from this run's
  // grant stage. Other files are left alone; the later clean-checkout check then reports them.
  const names = readdirSync(temp).filter(name => GRANT_STAGE_RECEIPT.test(name)).sort();
  mkdirSync(destinationDirectory, { recursive: true });
  const moved = names.map(name => {
    const source = path.join(temp, name);
    const destination = path.join(destinationDirectory, `projects-config-grants-${runId}-${name}`);
    // Copy, then delete, instead of renaming: the receipt directory may be on another drive.
    copyFileSync(source, destination);
    const sha256 = createHash('sha256').update(readFileSync(destination)).digest('hex');
    rmSync(source);
    return { path: destination, sha256 };
  });
  // Remove Temp only when it is now empty, so an unexpected file still fails the clean check visibly.
  if (readdirSync(temp).length === 0) rmdirSync(temp);
  return moved;
}
