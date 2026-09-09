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

  const applyDisplayContext = useCallback((next: AiExecutionContext) => {
    const nextIdentity = providerIdentity(next);
    if (previousIdentity.current && previousIdentity.current !== nextIdentity) {
      setAnnouncement(`AI provider changed. ${aiExecutionProviderLabel(next)}`);
    }
    previousIdentity.current = nextIdentity;
    setDisplayContext(next);
  }, []);

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const next = await apiClient.prepareAiExecutionContext(operation, projectId, runId);
      setPreparedContext(next);
      applyDisplayContext(next);
      return next;
    } catch (err) {
      setError(err instanceof Error ? err.message : 'AI provider information is unavailable.');
      return null;
    } finally {
      setLoading(false);
    }
  }, [applyDisplayContext, operation, projectId, runId]);

  useEffect(() => {
    if (!enabled) {
      setPreparedContext(null);
      setDisplayContext(null);
      setError(null);
      setLoading(false);
      return;
    }
    let cancelled = false;
    queueMicrotask(() => {
      if (!cancelled) void refresh();
    });
    return () => {
      cancelled = true;
    };
  }, [enabled, refresh]);

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
    setDisplayContext((current) => current ? { ...current, phase } : current);
  }, []);

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
    setPhase,
  ]);
}
