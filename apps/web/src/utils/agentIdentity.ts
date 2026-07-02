export interface ResolvedAgentIdentity {
  rawLabel: string;
  displayName: string;
  roleTitle: string;
  roleKey: string;
  isNamedAgent: boolean;
  isModelFallback: boolean;
}

const MODEL_HINTS = [
  'claude',
  'gpt',
  'gemini',
  'llama',
  'mistral',
  'deepseek',
  'qwen',
  'codex',
  'sonnet',
  'opus',
  'haiku',
  'flash',
  'mini',
  'o1',
  'o3',
  'o4',
];

function normalize(value: string): string {
  return value.trim().toLowerCase();
}

function titleizeToken(token: string): string {
  if (!token) return token;
  if (/^\d+(\.\d+)?$/.test(token)) return token;
  const lower = token.toLowerCase();
  if (lower === 'gpt') return 'GPT';
  if (lower === 'claude') return 'Claude';
  if (lower === 'gemini') return 'Gemini';
  if (lower === 'llama') return 'Llama';
  if (lower === 'qwen') return 'Qwen';
  if (lower === 'codex') return 'Codex';
  if (lower === 'o1' || lower === 'o3' || lower === 'o4') return lower;
  if (token.length <= 3) return token.toUpperCase();
  return `${token.charAt(0).toUpperCase()}${token.slice(1)}`;
}

export function formatModelLabel(model: string | null | undefined): string {
  const trimmed = model?.trim();
  if (!trimmed) return '—';
  return trimmed
    .split(/[\/:_-]+/)
    .filter(Boolean)
    .map(titleizeToken)
    .join(' ');
}

function looksLikeModelIdentifier(value: string): boolean {
  const normalized = normalize(value);
  return MODEL_HINTS.some((hint) => normalized.includes(hint));
}

function roleTitleForKey(roleKey: string): string {
  const map: Record<string, string> = {
    agent: 'AI Assistant',
    rai: 'RAI Reviewer',
    review: 'Human Review',
    merge: 'Merge Coordinator',
    scribe: 'Session Logger',
    coordinator: 'Coordinator',
    assembly: 'Awaiting collective assembly',
  };
  return map[roleKey] ?? 'AI Assistant';
}

function inferRoleKey(value: string): string {
  const normalized = normalize(value);
  if (normalized.includes('rai') || normalized.includes('safety')) return 'rai';
  if (normalized.includes('merge')) return 'merge';
  if (normalized.includes('scribe') || normalized.includes('logger')) return 'scribe';
  if (normalized.includes('review')) return 'review';
  if (normalized.includes('coordinator')) return 'coordinator';
  return 'agent';
}

function findRoleTitle(label: string, roleByAgent?: Record<string, string>): string | undefined {
  if (!roleByAgent) return undefined;
  const target = normalize(label);
  for (const [name, roleTitle] of Object.entries(roleByAgent)) {
    if (normalize(name) === target) return roleTitle;
  }
  return undefined;
}

export function resolveAgentIdentity(
  label: string | null | undefined,
  roleByAgent?: Record<string, string>,
): ResolvedAgentIdentity {
  const rawLabel = label?.trim() || 'Unknown agent';
  const explicitRoleTitle = findRoleTitle(rawLabel, roleByAgent);
  if (explicitRoleTitle) {
    const roleKey = inferRoleKey(explicitRoleTitle);
    return {
      rawLabel,
      displayName: rawLabel,
      roleTitle: explicitRoleTitle,
      roleKey,
      isNamedAgent: true,
      isModelFallback: false,
    };
  }

  if (looksLikeModelIdentifier(rawLabel)) {
    return {
      rawLabel,
      displayName: formatModelLabel(rawLabel),
      roleTitle: 'Agent metadata unavailable',
      roleKey: 'agent',
      isNamedAgent: false,
      isModelFallback: true,
    };
  }

  const roleKey = inferRoleKey(rawLabel);
  return {
    rawLabel,
    displayName: roleKey === 'agent' ? rawLabel : roleTitleForKey(roleKey),
    roleTitle: rawLabel === 'Unknown agent' ? 'Metadata unavailable' : roleTitleForKey(roleKey),
    roleKey,
    isNamedAgent: rawLabel !== 'Unknown agent',
    isModelFallback: false,
  };
}
