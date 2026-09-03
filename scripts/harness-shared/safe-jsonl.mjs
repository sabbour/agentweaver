import { appendFile, mkdir } from 'node:fs/promises';
import path from 'node:path';

import { redact } from './redaction.mjs';

export function serializeRedactedJsonLine(value) {
  return JSON.stringify(redact(value));
}

export async function appendRedactedJsonLine(file, value) {
  await mkdir(path.dirname(file), { recursive: true });
  await appendFile(file, `${serializeRedactedJsonLine(value)}\n`, { encoding: 'utf8', mode: 0o600 });
}
