import { appendFile, mkdir } from 'node:fs/promises';
import path from 'node:path';

import { redact } from './redaction.mjs';

/** Redacted JSON Lines output helpers used for durable harness evidence. */
/** Serialize one evidence value as a JSON Lines record after removing sensitive data. */
export function serializeRedactedJsonLine(value) {
  return JSON.stringify(redact(value));
}

/** Append one redacted JSON record with owner-only file permissions, creating its parent directory. */
export async function appendRedactedJsonLine(file, value) {
  await mkdir(path.dirname(file), { recursive: true });
  await appendFile(file, `${serializeRedactedJsonLine(value)}\n`, { encoding: 'utf8', mode: 0o600 });
}
