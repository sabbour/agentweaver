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

export function parseBeatPlan(markdown) {
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
