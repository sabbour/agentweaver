import { apiClient } from '../api/apiClient';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { PendingApprovalDto } from '../api/types';

export function usePendingApprovals(runId: string, refreshKey: string) {
  const [approvals, setApprovals] = useState<PendingApprovalDto[]>([]);
  const [loadedRunId, setLoadedRunId] = useState('');
  const [loading, setLoading] = useState(Boolean(runId));
  const [error, setError] = useState<string | null>(null);
  const [errorRunId, setErrorRunId] = useState('');
  const generationRef = useRef(0);

  const refresh = useCallback(async () => {
    const generation = ++generationRef.current;
    if (!runId) {
      setApprovals([]);
      setLoadedRunId('');
      setLoading(false);
      setError(null);
      setErrorRunId('');
      return;
    }
    setLoading(true);
    try {
      const response = await apiClient.getPendingApprovals(runId);
      if (generation !== generationRef.current) return;
      setApprovals(response.approvals);
      setLoadedRunId(runId);
      setError(null);
      setErrorRunId('');
    } catch (err) {
      if (generation !== generationRef.current) return;
      setApprovals([]);
      setLoadedRunId(runId);
      setError(err instanceof Error ? err.message : 'Approval review data could not be loaded.');
      setErrorRunId(runId);
    } finally {
      if (generation === generationRef.current) setLoading(false);
    }
  }, [runId]);

  useEffect(() => {
    const load = async () => {
      await refresh();
    };
    void load();
  }, [refresh, refreshKey]);

  return {
    approvals: loadedRunId === runId ? approvals : [],
    loading: Boolean(runId) && (loadedRunId !== runId || loading),
    error: errorRunId === runId ? error : null,
    refresh,
  };
}
