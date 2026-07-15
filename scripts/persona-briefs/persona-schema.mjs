export const CORE_HEADINGS = [
  '## Who you are',
  '## What you are trying to get done right now',
  '## Voice & behavior',
  '## Where to stop (safe checkpoint)',
  '## What a good outcome would look like (for your own judgment, not a script)',
];

export const CORE_BEHAVIOR_HEADINGS = [
  '## MANDATORY behavior: push back at least TWICE',
  '## Judgment, not a script',
];

export const ADAPTER_HEADINGS = [
  '## Surface context',
  '## Intent mapping',
  '## Guardrails',
];

export const SUPPORTED_SURFACES = ['api', 'ui', 'mcp'];

const CORE_SURFACE_LEAKS = [
  /\bcurl\b/i,
  /\bclick\b/i,
  /\bsubmit-goal\b/i,
  /\brevise-spec\b/i,
  /\btools\/call\b/i,
];

function asText(value) {
  return typeof value === 'string' ? value.replace(/\r\n/g, '\n') : '';
}

function validateHeadings(text, expected, label) {
  const errors = [];
  for (const heading of expected) {
    if (!text.includes(heading)) errors.push(`${label} is missing required heading "${heading}"`);
  }
  return errors;
}

function firstHeading(text) {
  return text.match(/^#\s+(.+)$/m)?.[1]?.trim() ?? null;
}

export function validatePersonaCore(core) {
  const text = asText(typeof core === 'object' && core ? core.content : core);
  const errors = [];
  const heading = firstHeading(text);

  if (!text.trim()) errors.push('persona core must be non-empty markdown');
  if (!heading?.startsWith('Persona core: ')) errors.push('persona core must start with "# Persona core: <name>"');
  errors.push(...validateHeadings(text, CORE_HEADINGS, 'persona core'));
  if (!CORE_BEHAVIOR_HEADINGS.some((required) => text.includes(required))) {
    errors.push(`persona core is missing required behavior heading (${CORE_BEHAVIOR_HEADINGS.join(' or ')})`);
  }
  for (const leak of CORE_SURFACE_LEAKS) {
    if (leak.test(text)) errors.push(`persona core contains surface-specific language matching ${leak}`);
  }

  return {
    valid: errors.length === 0,
    errors,
    name: heading?.replace(/^Persona core:\s*/, '').split(' — ')[0] ?? null,
    content: text,
  };
}

export function validateSurfaceAdapter(adapter, expectedSurface = undefined) {
  const text = asText(typeof adapter === 'object' && adapter ? adapter.content : adapter);
  const errors = [];
  const heading = firstHeading(text);
  const match = heading?.match(/^Persona surface adapter:\s+(.+?)\s+—\s+([a-z]+)$/);
  const surface = match?.[2] ?? null;

  if (!text.trim()) errors.push('surface adapter must be non-empty markdown');
  if (!match) errors.push('surface adapter must start with "# Persona surface adapter: <name> — <surface>"');
  if (surface && !SUPPORTED_SURFACES.includes(surface)) errors.push(`unsupported surface "${surface}"`);
  if (expectedSurface && surface !== expectedSurface) {
    errors.push(`adapter surface "${surface ?? 'unknown'}" does not match requested surface "${expectedSurface}"`);
  }
  errors.push(...validateHeadings(text, ADAPTER_HEADINGS, 'surface adapter'));

  return {
    valid: errors.length === 0,
    errors,
    name: match?.[1] ?? null,
    surface,
    content: text,
  };
}

export function validatePersonaBrief(persona) {
  const core = validatePersonaCore(persona?.core ?? persona?.coreText);
  const adapter = persona?.adapter || persona?.adapterText
    ? validateSurfaceAdapter(persona.adapter ?? persona.adapterText, persona.surface)
    : null;
  const errors = [...core.errors, ...(adapter?.errors ?? [])];

  if (adapter && core.name && adapter.name && core.name !== adapter.name) {
    errors.push(`adapter persona "${adapter.name}" does not match core persona "${core.name}"`);
  }

  return { valid: errors.length === 0, errors, core, adapter };
}
