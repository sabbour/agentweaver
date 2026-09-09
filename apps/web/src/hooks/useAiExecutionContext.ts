import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { aiExecutionProviderLabel, providerIdentity } from '../components/aiExecutionContext';
import type { AiExecutionContext, EffectiveModelProvider } from '../api/types';

function replacementContext(error: unknown): AiExecutionContext | null {
  if (!(error instanceof ApiError) || error.status !== 409) return null;
  const payload = error.payload;
  if (!payload || typeof payload !== 'object') return null;
  const value = (payload as { context?: unknown }).context;
  if (!value || typeof value !== 'object') return null;
  return value as AiExecutionContext;
}

function replacementErrorCode(error: unknown): string | null {
  if (!(error instanceof ApiError) || !error.payload || typeof error.payload !== 'object')
    return null;
  const value = (error.payload as { error?: unknown }).error;
  return typeof value === 'string' ? value : null;
}

export function useAiExecutionContext(
  operation: string,
  projectId?: string,
  runId?: string,
  enabled = true,
) {
  const [preparedContext, setPreparedContext] = useState<AiExecutionContext | null>(null);
  const [displayContext, setDisplayContext] = useState<AiExecutionContext | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const previousIdentity = useRef('');
  const requestSequence = useRef(0);
  const displayContextRef = useRef<AiExecutionContext | null>(null);
  const scopeKey = `${operation}\u0000${projectId ?? ''}\u0000${runId ?? ''}`;

  const applyDisplayContext = useCallback((next: AiExecutionContext) => {
    const nextIdentity = providerIdentity(next);
    if (previousIdentity.current && previousIdentity.current !== nextIdentity) {
      setAnnouncement(`AI provider changed. ${aiExecutionProviderLabel(next)}`);
    }
    previousIdentity.current = nextIdentity;
    displayContextRef.current = next;
    setDisplayContext(next);
  }, []);

  const refresh = useCallback(async () => {
    const requestId = ++requestSequence.current;
    setLoading(true);
    setError(null);
    try {
      const next = await apiClient.prepareAiExecutionContext(operation, projectId, runId);
      if (requestId !== requestSequence.current) return null;
      setPreparedContext(next);
      if (displayContextRef.current?.phase !== 'active'
          && displayContextRef.current?.phase !== 'completed') {
        applyDisplayContext(next);
      }
      return next;
    } catch (err) {
      if (requestId !== requestSequence.current) return null;
      setError(err instanceof Error ? err.message : 'AI provider information is unavailable.');
      return null;
    } finally {
      if (requestId === requestSequence.current) setLoading(false);
    }
  }, [applyDisplayContext, operation, projectId, runId]);

  useEffect(() => {
    const sequence = requestSequence;
    const scopeRequestId = ++requestSequence.current;
    queueMicrotask(() => {
      if (scopeRequestId !== sequence.current) return;
      previousIdentity.current = '';
      displayContextRef.current = null;
      setPreparedContext(null);
      setDisplayContext(null);
      setAnnouncement('');
      setError(null);
      if (!enabled) {
        setLoading(false);
        return;
      }
      setLoading(true);
      void refresh();
    });
    return () => {
      ++sequence.current;
    };
  }, [enabled, refresh, scopeKey]);

  useEffect(() => {
    if (!preparedContext?.expires_at) return;
    const expiresAt = Date.parse(preparedContext.expires_at);
    if (!Number.isFinite(expiresAt)) return;
    const delay = Math.max(0, Math.min(2_147_000_000, expiresAt - Date.now() - 30_000));
    const timer = window.setTimeout(() => {
      void refresh();
    }, delay);
    return () => window.clearTimeout(timer);
  }, [preparedContext?.expires_at, refresh]);

  const handleInvocationError = useCallback((err: unknown): boolean => {
    const replacement = replacementContext(err);
    if (!replacement) return false;
    const errorCode = replacementErrorCode(err);
    const expired = errorCode === 'ai_execution_context_expired';
    const required = errorCode === 'ai_execution_context_required';
    setPreparedContext(replacement);
    setAnnouncement(
      expired
        ? `AI provider confirmation expired. ${aiExecutionProviderLabel(replacement)}`
        : required
          ? `AI provider confirmation required. ${aiExecutionProviderLabel(replacement)}`
          : `AI provider changed. ${aiExecutionProviderLabel(replacement)}`,
    );
    applyDisplayContext(replacement);
    setError(
      expired
        ? 'The AI provider confirmation expired. Review the provider and retry the action.'
        : required
          ? 'Review the AI provider and retry the action.'
          : 'The AI provider changed. Review the updated provider and retry the action.',
    );
    return true;
  }, [applyDisplayContext]);

  const applyCompletedContext = useCallback((next: AiExecutionContext | null | undefined) => {
    if (next) applyDisplayContext(next);
  }, [applyDisplayContext]);

  const applyProvider = useCallback((
    provider: EffectiveModelProvider | null | undefined,
    phase: AiExecutionContext['phase'],
  ) => {
    if (!provider) return;
    applyDisplayContext({
      ai_required: true,
      operation,
      phase,
      execution_key: null,
      expires_at: null,
      effective_model_provider: provider,
    });
  }, [applyDisplayContext, operation]);

  const setPhase = useCallback((phase: AiExecutionContext['phase']) => {
    setDisplayContext((current) => {
      const next = current ? { ...current, phase } : current;
      displayContextRef.current = next;
      return next;
    });
  }, []);

  const restorePreparedContext = useCallback(() => {
    if (preparedContext) applyDisplayContext(preparedContext);
  }, [applyDisplayContext, preparedContext]);

  const providerKey = preparedContext?.execution_key ?? undefined;
  const available = preparedContext?.ai_required === false
      || preparedContext?.effective_model_provider?.state === 'resolved';

  return useMemo(() => ({
    context: displayContext,
    providerKey,
    available,
    loading,
    error,
    announcement,
    refresh,
    handleInvocationError,
    applyCompletedContext,
    applyProvider,
    setPhase,
    restorePreparedContext,
  }), [
    announcement,
    applyCompletedContext,
    applyProvider,
    available,
    displayContext,
    error,
    handleInvocationError,
    loading,
    providerKey,
    refresh,
    restorePreparedContext,
    setPhase,
  ]);
}
