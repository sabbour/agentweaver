#!/usr/bin/env node
// Converts the subset of Mermaid sequenceDiagram syntax used by Agentweaver
// docs into docs/diagrams/src/sequence-spec.schema.json.

const PARTICIPANT_STYLES = [
  [/\b(user|human|reviewer|operator|client|browser|caller)\b/i, { icon: 'globe', badge: { text: 'Client', tone: 'lavender' } }],
  [/\b(git|github|repo|worktree|branch)\b/i, { icon: 'branch', badge: { text: 'Git', tone: 'lavender' } }],
  [/\b(auth|oauth|token|key|secret|identity)\b/i, { icon: 'key', badge: { text: 'Security', tone: 'marigold' } }],
  [/\b(db|database|store|registry|postgres|sqlite|memory|queue)\b/i, { icon: 'database', badge: { text: 'Data', tone: 'teal' } }],
  [/\b(ui|web|frontend|portal|page)\b/i, { icon: 'window', badge: { text: 'UI', tone: 'teal' } }],
  [/\b(agent|worker|sandbox|pod|runtime|model|controller)\b/i, { icon: 'bot', badge: { text: 'Runtime', tone: 'green' } }],
  [/\b(api|service|server|coordinator|orchestrat|engine|gateway|mcp)\b/i, { icon: 'server', badge: { text: 'Service', tone: 'green' } }],
];

function decodeEntities(value) {
  return value
    .replace(/&nbsp;/g, ' ')
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&amp;/g, '&');
}

function clean(value) {
  return decodeEntities(value)
    .replace(/^["'`](.*)["'`]$/s, '$1')
    .replace(/\s+/g, ' ')
    .trim();
}

function styleFor(id, label, actor) {
  if (actor) return { icon: 'globe', badge: { text: 'Actor', tone: 'lavender' } };
  const haystack = `${id} ${label}`;
  for (const [pattern, style] of PARTICIPANT_STYLES) if (pattern.test(haystack)) return style;
  return { icon: 'box', badge: { text: 'Component', tone: 'neutral' } };
}

function ensureParticipant(participants, id) {
  if (participants.some((p) => p.id === id)) return;
  const style = styleFor(id, id, false);
  participants.push({ id, label: id, ...style });
}

function parseMessage(line) {
  const match = line.match(/^([A-Za-z0-9_.-]+?)\s*(-->>|->>|--x|-x|-->|->)([+-]?)\s*([A-Za-z0-9_.-]+)\s*:\s*(.+)$/);
  if (!match) return null;
  const [, from, connector, activation, to, rawLabel] = match;
  const dashed = connector.startsWith('--');
  const cross = connector.endsWith('x');
  const open = connector.endsWith('>>') && dashed;
  return {
    from,
    to,
    label: clean(rawLabel),
    line: dashed ? 'dashed' : 'solid',
    arrow: cross ? 'cross' : open ? 'open' : 'filled',
    activation,
  };
}

/**
 * Convert one Mermaid sequenceDiagram source.
 * @returns {{spec: object, warnings: string[]} | null}
 */
export function convertSequenceDiagram(src, { title, name } = {}) {
  const lines = src
    .split(/\r?\n/)
    .map((line) => line.replace(/%%\{.*?\}%%/g, '').trim())
    .filter((line) => line && !line.startsWith('%%'));
  if (!/^sequenceDiagram\b/i.test(lines[0] ?? '')) return null;

  const participants = [];
  const rootSteps = [];
  const warnings = [];
  const stack = [];
  let currentSteps = rootSteps;
  let autonumber = false;

  for (let i = 1; i < lines.length; i += 1) {
    const line = lines[i];
    if (/^autonumber\b/i.test(line)) {
      autonumber = true;
      continue;
    }

    let match = line.match(/^(participant|actor)\s+([A-Za-z0-9_.-]+)(?:\s+as\s+(.+))?$/i);
    if (match) {
      const actor = match[1].toLowerCase() === 'actor';
      const id = match[2];
      const label = clean(match[3] ?? id);
      const style = styleFor(id, label, actor);
      if (!participants.some((p) => p.id === id)) participants.push({ id, label, ...style });
      continue;
    }

    match = line.match(/^(activate|deactivate)\s+([A-Za-z0-9_.-]+)$/i);
    if (match) {
      ensureParticipant(participants, match[2]);
      currentSteps.push({ type: 'activation', participant: match[2], action: match[1].toLowerCase() === 'activate' ? 'start' : 'end' });
      continue;
    }

    match = line.match(/^Note\s+(?:over|right of|left of)\s+([^:]+)\s*:\s*(.+)$/i);
    if (match) {
      const over = match[1].split(',').map((id) => id.trim()).filter(Boolean);
      over.forEach((id) => ensureParticipant(participants, id));
      currentSteps.push({ type: 'note', over, label: clean(match[2]) });
      continue;
    }

    match = line.match(/^(alt|opt|loop)(?:\s+(.+))?$/i);
    if (match) {
      const fragment = {
        type: 'fragment',
        operator: match[1].toLowerCase(),
        label: clean(match[2] ?? '') || undefined,
        sections: [{ steps: [] }],
      };
      currentSteps.push(fragment);
      stack.push({ fragment, parentSteps: currentSteps });
      currentSteps = fragment.sections[0].steps;
      continue;
    }

    match = line.match(/^else(?:\s+(.+))?$/i);
    if (match) {
      const frame = stack.at(-1);
      if (!frame || frame.fragment.operator !== 'alt') {
        warnings.push(`line ${i + 1}: ignored '${line}' without an enclosing alt`);
        continue;
      }
      const section = { label: clean(match[1] ?? '') || undefined, steps: [] };
      frame.fragment.sections.push(section);
      currentSteps = section.steps;
      continue;
    }

    if (/^end\b/i.test(line)) {
      const frame = stack.pop();
      if (!frame) warnings.push(`line ${i + 1}: ignored unmatched end`);
      else currentSteps = frame.parentSteps;
      continue;
    }

    const message = parseMessage(line);
    if (message) {
      ensureParticipant(participants, message.from);
      ensureParticipant(participants, message.to);
      if (message.activation === '+') currentSteps.push({ type: 'activation', participant: message.to, action: 'start' });
      const { activation, ...step } = message;
      currentSteps.push({ type: 'message', ...step });
      if (activation === '-') currentSteps.push({ type: 'activation', participant: message.from, action: 'end' });
      continue;
    }

    warnings.push(`line ${i + 1}: unsupported sequence syntax '${line}'`);
  }

  if (stack.length) warnings.push(`${stack.length} unclosed combined fragment(s)`);
  if (!participants.length || !rootSteps.length) return null;

  const participantNames = participants.map((p) => p.label);
  const specTitle = title ?? name ?? 'Sequence diagram';
  return {
    spec: {
      $schema: './sequence-spec.schema.json',
      kind: 'sequence',
      title: specTitle,
      alt: `${specTitle}: ${participantNames.join(', ')}`,
      ...(autonumber ? { autonumber: true } : {}),
      participants,
      steps: rootSteps,
    },
    warnings,
  };
}
