import fs from 'node:fs/promises';

const beatHeadingPattern = /^## Beat ([0-9]+\.[0-9]+) — (.+)$/gm;

function parseActLabel(beatId) {
  const [act] = beatId.split('.');
  return `Act ${act}`;
}

function extractNarration(body) {
  const narrationMatch = body.match(/Narration:\s*[“"]([\s\S]*?)[”"]\s*(?:\n\n|$)/);
  if (narrationMatch) return narrationMatch[1].replace(/\s+/g, ' ').trim();
  const draftMatch = body.match(/DRAFT VO[^:]*:\s*[“"]([\s\S]*?)[”"]\s*(?:\n\n|$)/);
  if (draftMatch) return draftMatch[1].replace(/\s+/g, ' ').trim();
  return '';
}

function extractBlockers(body) {
  return Array.from(body.matchAll(/BLOCKED\(([^)]+)\)/g), (match) => match[1]);
}

function extractStartUrl(body) {
  const match = body.match(/(?:^|\n)Start URL:\s*(\S+)\s*(?:\n|$)/i);
  return match ? match[1].trim() : null;
}

function extractFreshNavigation(body) {
  const match = body.match(/(?:^|\n)Fresh navigation:\s*(true|false)\s*(?:\n|$)/i);
  return match ? match[1].toLowerCase() === 'true' : false;
}

export function parseBeatPlan(markdown) {
  // Normalize CRLF/CR to LF first: the beat file is stored with CRLF line endings in
  // this repo, but the narration/paragraph regexes below key off "\n\n" — without this
  // a fresh checkout would silently extract empty narration for every beat.
  markdown = String(markdown).replace(/\r\n?/g, '\n');
  const matches = Array.from(markdown.matchAll(beatHeadingPattern));
  return matches.map((match, index) => {
    const bodyStart = match.index + match[0].length;
    const bodyEnd = index + 1 < matches.length ? matches[index + 1].index : markdown.length;
    const body = markdown.slice(bodyStart, bodyEnd).trim();
    return {
      id: match[1],
      title: match[2].trim(),
      act: parseActLabel(match[1]),
      narrationSource: extractNarration(body),
      blockers: extractBlockers(body),
      startUrl: extractStartUrl(body),
      freshNavigation: extractFreshNavigation(body),
      markdown: body,
    };
  });
}

export async function loadBeatPlan(planPath) {
  return parseBeatPlan(await fs.readFile(planPath, 'utf8'));
}

export function formatNarrationFile(beats) {
  return beats.map((beat) => {
    const lines = [
      `${beat.act}, Beat ${beat.id} — ${beat.title}`,
      `Narration: ${beat.generatedNarration ?? beat.narrationSource ?? ''}`,
    ];
    if (beat.blockedReason) lines.push(`Blocked: ${beat.blockedReason}`);
    return lines.join('\n');
  }).join('\n\n');
}
