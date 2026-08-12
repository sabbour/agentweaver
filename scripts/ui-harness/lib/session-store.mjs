import { existsSync } from 'node:fs';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

const SESSION_ID = /^[a-z0-9][a-z0-9_-]{0,127}$/i;

export function assertSessionId(id) {
  if (!SESSION_ID.test(String(id ?? ''))) throw new Error('invalid UI session id');
  return id;
}

export function sessionPath(sessionsDirectory, id) {
  return path.join(sessionsDirectory, `${assertSessionId(id)}.json`);
}

export async function loadSession(sessionsDirectory, id) {
  const file = sessionPath(sessionsDirectory, id);
  if (!existsSync(file)) throw new Error('no active UI session; run init first');
  return JSON.parse(await readFile(file, 'utf8'));
}

export async function saveSession(sessionsDirectory, session) {
  await mkdir(sessionsDirectory, { recursive: true });
  const file = sessionPath(sessionsDirectory, session.id);
  const temporary = `${file}.${process.pid}.${randomUUID()}.tmp`;
  await writeFile(temporary, JSON.stringify(session, null, 2), { encoding: 'utf8', mode: 0o600 });
  await rename(temporary, file);
}

export async function removeSession(sessionsDirectory, id) {
  await rm(sessionPath(sessionsDirectory, id), { force: true });
}
