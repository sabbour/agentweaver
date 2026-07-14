// Persona markdown parser.
//
// Persona definitions live at specs/personas/*.md and follow a shared shape:
//   # <Name> — <Title>
//   ## Identity & background
//   ## Goals & motivations
//   ## Behavioral profile & decision patterns
//   ## Agentweaver scenarios
//     ### <Scenario name>
//       - **Trigger/goal:** ...
//       - **Team/agents:** ...
//       - **UI steps attempted:** ...
//       - **Success looks like:** ...
//   ## Failure signals to watch for
//     - ...
//
// The harness treats a scenario's "Success looks like" as observable acceptance
// criteria and "Failure signals" as bug-classification heuristics. A scenario
// *playbook* (scenarios/*.mjs) turns those into a concrete API call sequence and
// a judge; this parser surfaces the human-authored intent so findings can quote it.

import { readFile } from 'node:fs/promises';

/**
 * @param {string} filePath absolute path to a persona markdown file
 */
export async function loadPersona(filePath) {
  const md = await readFile(filePath, 'utf8');
  const lines = md.split(/\r?\n/);

  const title = (lines.find((l) => l.startsWith('# ')) ?? '# Unknown').slice(2).trim();

  const sections = splitByHeading(md, /^##\s+/m);
  const scenarioBlock = sections.find((s) => /agentweaver scenarios/i.test(s.heading));
  const failureBlock = sections.find((s) => /failure signals/i.test(s.heading));

  const scenarios = scenarioBlock
    ? splitByHeading(scenarioBlock.body, /^###\s+/m)
        .filter((s) => s.heading)
        .map((s) => ({
          name: s.heading.trim(),
          fields: extractLabeledBullets(s.body),
          raw: s.body.trim(),
        }))
    : [];

  const failureSignals = failureBlock
    ? failureBlock.body
        .split(/\r?\n/)
        .filter((l) => /^\s*-\s+/.test(l))
        .map((l) => l.replace(/^\s*-\s+/, '').trim())
    : [];

  return { title, filePath, scenarios, failureSignals };
}

/** Split a markdown blob at a heading regex into { heading, body } chunks. */
function splitByHeading(md, headingRe) {
  const lines = md.split(/\r?\n/);
  const chunks = [];
  let current = null;
  for (const line of lines) {
    if (headingRe.test(line + '\n')) {
      if (current) chunks.push(current);
      current = { heading: line.replace(headingRe, '').trim(), body: '' };
    } else if (current) {
      current.body += line + '\n';
    } else {
      current = { heading: '', body: line + '\n' };
    }
  }
  if (current) chunks.push(current);
  return chunks;
}

/** Extract `- **Label:** value` bullets into a keyed object. */
function extractLabeledBullets(body) {
  const out = {};
  for (const line of body.split(/\r?\n/)) {
    const m = line.match(/^\s*-\s+\*\*(.+?):\*\*\s*(.*)$/);
    if (m) out[m[1].trim().toLowerCase()] = m[2].trim();
  }
  return out;
}
