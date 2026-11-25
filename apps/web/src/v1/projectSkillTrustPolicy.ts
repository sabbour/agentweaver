import { updatePlatformPolicyTextBuffer } from './platformPolicyContracts';
import type { PlatformPolicyTextBuffer } from './platformPolicyContracts';

export const PROJECT_SKILLS_DATA_PATH = 'policy/skills/data.json';
export const MAX_PROJECT_SKILL_TRUST_SCORE = 1000;

export interface ProjectSkillTrustData {
  minimumScore: number;
  requireCompletedEvidence: boolean;
  scoreStart: number;
  scoreEnd: number;
}

export interface ProjectSkillTrustTarget extends ProjectSkillTrustData {
  fileId: number;
}

export type ProjectSkillTrustScoreValidation =
  | { ok: true; score: number; token: string }
  | { ok: false; message: string };

const CANONICAL_NON_NEGATIVE_INT = /^(?:0|[1-9][0-9]*)$/;

function isJsonWhitespace(character: string): boolean {
  return character === ' ' || character === '\t' || character === '\r' || character === '\n';
}

function skipWhitespace(text: string, index: number): number {
  let cursor = index;
  while (cursor < text.length && isJsonWhitespace(text[cursor])) cursor += 1;
  return cursor;
}

function parseJsonString(text: string, start: number): { value: string; next: number } | null {
  if (text[start] !== '"') return null;
  let cursor = start + 1;
  let result = '';
  while (cursor < text.length) {
    const ch = text[cursor];
    if (ch === '"') return { value: result, next: cursor + 1 };
    if (ch.charCodeAt(0) < 0x20) return null;
    if (ch !== '\\') {
      result += ch;
      cursor += 1;
      continue;
    }
    cursor += 1;
    if (cursor >= text.length) return null;
    const esc = text[cursor];
    if (esc === '"' || esc === '\\' || esc === '/') {
      result += esc;
      cursor += 1;
    } else if (esc === 'b') {
      result += '\b';
      cursor += 1;
    } else if (esc === 'f') {
      result += '\f';
      cursor += 1;
    } else if (esc === 'n') {
      result += '\n';
      cursor += 1;
    } else if (esc === 'r') {
      result += '\r';
      cursor += 1;
    } else if (esc === 't') {
      result += '\t';
      cursor += 1;
    } else if (esc === 'u') {
      const hex = text.slice(cursor + 1, cursor + 5);
      if (!/^[0-9a-fA-F]{4}$/.test(hex)) return null;
      result += String.fromCharCode(Number.parseInt(hex, 16));
      cursor += 5;
    } else {
      return null;
    }
  }
  return null;
}

function readRawToken(text: string, start: number): { token: string; next: number } {
  let cursor = start;
  while (
    cursor < text.length &&
    !isJsonWhitespace(text[cursor]) &&
    text[cursor] !== ',' &&
    text[cursor] !== '}'
  ) {
    cursor += 1;
  }
  return { token: text.slice(start, cursor), next: cursor };
}

export function parseProjectSkillTrustData(text: string): ProjectSkillTrustData | null {
  let cursor = text.startsWith('\uFEFF') ? 1 : 0;
  cursor = skipWhitespace(text, cursor);
  if (text[cursor] !== '{') return null;
  cursor = skipWhitespace(text, cursor + 1);
  if (cursor >= text.length || text[cursor] === '}') return null;

  const seenKeys = new Set<string>();
  let minimumScore: number | null = null;
  let requireCompletedEvidence: boolean | null = null;
  let scoreStart = -1;
  let scoreEnd = -1;

  while (cursor < text.length) {
    const keyResult = parseJsonString(text, cursor);
    if (!keyResult) return null;
    const key = keyResult.value;
    if (seenKeys.has(key)) return null;
    seenKeys.add(key);

    cursor = skipWhitespace(text, keyResult.next);
    if (text[cursor] !== ':') return null;
    cursor = skipWhitespace(text, cursor + 1);
    if (cursor >= text.length) return null;

    const valueStart = cursor;
    const { token, next } = readRawToken(text, valueStart);
    if (!token) return null;

    if (key === 'schemaVersion') {
      if (token !== '1') return null;
    } else if (key === 'minimumScore') {
      if (!CANONICAL_NON_NEGATIVE_INT.test(token)) return null;
      const parsed = Number(token);
      if (!Number.isSafeInteger(parsed) || parsed < 0 || parsed > MAX_PROJECT_SKILL_TRUST_SCORE)
        return null;
      minimumScore = parsed;
      scoreStart = valueStart;
      scoreEnd = next;
    } else if (key === 'requireCompletedEvidence') {
      if (token === 'true') requireCompletedEvidence = true;
      else if (token === 'false') requireCompletedEvidence = false;
      else return null;
    } else {
      return null;
    }

    cursor = skipWhitespace(text, next);
    if (text[cursor] === ',') {
      cursor = skipWhitespace(text, cursor + 1);
      continue;
    }
    if (text[cursor] === '}') {
      cursor = skipWhitespace(text, cursor + 1);
      if (cursor !== text.length) return null;
      if (
        seenKeys.size !== 3 ||
        minimumScore === null ||
        requireCompletedEvidence === null ||
        scoreStart < 0 ||
        scoreEnd <= scoreStart
      ) {
        return null;
      }
      return { minimumScore, requireCompletedEvidence, scoreStart, scoreEnd };
    }
    return null;
  }

  return null;
}

export function findProjectSkillTrustTarget(
  draftFiles: readonly { id: number; relativePath: string; content: PlatformPolicyTextBuffer }[],
): ProjectSkillTrustTarget | null {
  const matches = draftFiles.filter((file) => file.relativePath === PROJECT_SKILLS_DATA_PATH);
  if (matches.length !== 1) return null;
  const parsed = parseProjectSkillTrustData(matches[0].content.value);
  if (!parsed) return null;
  return { fileId: matches[0].id, ...parsed };
}

export function validateNextSkillTrustScore(
  rawInput: string,
  currentScore: number,
): ProjectSkillTrustScoreValidation {
  if (currentScore >= MAX_PROJECT_SKILL_TRUST_SCORE) {
    return {
      ok: false,
      message: `The minimum trust score is already ${MAX_PROJECT_SKILL_TRUST_SCORE}.`,
    };
  }
  const token = rawInput.trim();
  if (!CANONICAL_NON_NEGATIVE_INT.test(token)) {
    return {
      ok: false,
      message: `Enter a whole number greater than ${currentScore} and no greater than ${MAX_PROJECT_SKILL_TRUST_SCORE}.`,
    };
  }
  const score = Number(token);
  if (!Number.isSafeInteger(score) || score <= currentScore || score > MAX_PROJECT_SKILL_TRUST_SCORE) {
    return {
      ok: false,
      message: `Enter a whole number greater than ${currentScore} and no greater than ${MAX_PROJECT_SKILL_TRUST_SCORE}.`,
    };
  }
  return { ok: true, score, token };
}

export function applyProjectSkillTrustScoreUpdate(
  buffer: PlatformPolicyTextBuffer,
  target: Pick<ProjectSkillTrustTarget, 'scoreStart' | 'scoreEnd'>,
  nextScoreToken: string,
): PlatformPolicyTextBuffer {
  const nextValue =
    `${buffer.value.slice(0, target.scoreStart)}${nextScoreToken}${buffer.value.slice(target.scoreEnd)}`;
  return updatePlatformPolicyTextBuffer(buffer, nextValue, {
    start: target.scoreStart,
    end: target.scoreEnd,
    nextEnd: target.scoreStart + nextScoreToken.length,
  });
}
