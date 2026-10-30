import path from 'node:path';

/** Returns true only when `directory` resolves outside the `root` checkout. */
export function isOutsideCheckout(root, directory) {
  const relative = path.relative(path.resolve(root), path.resolve(directory));
  // A different drive gives an absolute relative path. Otherwise only `..` itself or a `../` prefix
  // leaves the checkout; a name such as `..receipts` is a folder inside it.
  return path.isAbsolute(relative) || relative === '..' || relative.startsWith(`..${path.sep}`);
}
