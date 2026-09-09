import type { AiExecutionContext, EffectiveModelProvider } from '../api/types';
import type { RunStreamEvent } from '../api/sse';

function providerName(provider: EffectiveModelProvider): string {
  if (provider.state === 'unavailable') return 'AI provider unavailable';
  switch (provider.provider_kind) {
    case 'byok':
    case 'user_byok':
      switch (provider.provider_type?.toLowerCase()) {
        case 'azure': return 'Azure BYOK';
        case 'openai': return 'OpenAI BYOK';
        case 'anthropic': return 'Anthropic BYOK';
        default: return 'Custom BYOK provider';
      }
    case 'project_github_copilot':
    case 'platform_github_copilot':
    case 'user_github_copilot':
      return 'GitHub Copilot';
    default:
      return 'AI provider unavailable';
  }
}

function unavailableReason(reason: string | null): string | null {
  switch (reason) {
    case 'no_provider':
      return 'Configure a model provider to continue.';
    case 'project_binding_requires_reauthorization':
      return 'Reconnect the project model provider to continue.';
    case 'user_provider_required':
      return 'Configure personal AI access to continue.';
    case 'user_binding_requires_reauthorization':
      return 'Reconnect personal GitHub Copilot access to continue.';
    case 'operation_requires_github_copilot':
      return 'This operation currently requires GitHub Copilot.';
    default:
      return reason;
  }
}

export function aiExecutionProviderScope(context: AiExecutionContext | null): string | null {
  const scope = context?.effective_model_provider?.provider_scope;
  switch (scope) {
    case 'platform': return 'Platform';
    case 'project': return 'Project';
    case 'user': return 'Personal';
    default: return null;
  }
}

export function aiExecutionProviderLabel(context: AiExecutionContext | null): string {
  const provider = context?.effective_model_provider;
  if (!provider) return 'AI provider information unavailable';
  if (provider.state === 'unavailable') {
    const reason = unavailableReason(provider.unavailable_reason);
    return reason ? `AI provider unavailable. ${reason}` : 'AI provider unavailable';
  }
  const prefix = context.phase === 'active'
    ? 'Using'
    : context.phase === 'completed'
      ? 'Used'
      : 'Expected provider:';
  const name = providerName(provider);
  const label = prefix.endsWith(':') ? `${prefix} ${name}` : `${prefix} ${name}`;
  return provider.model_id ? `${label}. Model: ${provider.model_id}.` : `${label}.`;
}

export function providerIdentity(context: AiExecutionContext | null): string {
  const provider = context?.effective_model_provider;
  if (!provider) return '';
  return [
    provider.state,
    provider.provider_kind,
    provider.resolution_scope,
    provider.provider_scope,
    provider.provider_type ?? '',
    provider.provider_key ?? '',
    provider.model_id ?? '',
    provider.unavailable_reason ?? '',
  ].join(':');
}

function scope(value: unknown, fallback: EffectiveModelProvider['resolution_scope']) {
  return value === 'project' || value === 'platform' || value === 'user' || value === 'unknown'
    ? value
    : fallback;
}

function providerScope(value: unknown): EffectiveModelProvider['provider_scope'] {
  return value === 'project' || value === 'platform' || value === 'user' || value === 'none'
    ? value
    : 'none';
}

export function aiExecutionContextFromEvents(
  events: RunStreamEvent[],
  operation: string,
  phase: AiExecutionContext['phase'] = 'active',
): AiExecutionContext | null {
  const event = [...events].reverse().find(
    (candidate) => candidate.type === 'run.model_provider_resolved',
  );
  if (!event) return null;
  const kind = String(event.payload.providerKind ?? event.payload.provider_kind ?? 'unavailable');
  const providerKind: EffectiveModelProvider['provider_kind'] =
    kind === 'byok'
    || kind === 'project_github_copilot'
    || kind === 'platform_github_copilot'
    || kind === 'user_byok'
    || kind === 'user_github_copilot'
      ? kind
      : 'unavailable';
  return {
    ai_required: true,
    operation,
    phase,
    execution_key: null,
    expires_at: null,
    effective_model_provider: {
      state: providerKind === 'unavailable' ? 'unavailable' : 'resolved',
      provider_kind: providerKind,
      resolution_scope: scope(
        event.payload.resolutionScope ?? event.payload.resolution_scope,
        'unknown',
      ),
      provider_scope: providerScope(event.payload.providerScope ?? event.payload.provider_scope),
      provider_type: event.payload.providerType == null
        ? null
        : String(event.payload.providerType),
      model_id: event.payload.modelId == null ? null : String(event.payload.modelId),
      provider_key: event.payload.providerKey == null ? null : String(event.payload.providerKey),
      unavailable_reason: event.payload.unavailableReason == null
        ? null
        : String(event.payload.unavailableReason),
    },
  };
}
