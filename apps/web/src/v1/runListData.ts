/**
 * Data adapters for the shared run list. The browser adapter reads runs the
 * user opened in this browser. A Gateway adapter for the run list (gap G2)
 * returns the same `RunListData` shape, so callers keep the same layout.
 */
import { useCallback, useMemo, useState } from 'react';
import { useAuth } from './AuthContext';
import { forgetRun, readRecentRuns } from './recentRuns';

/** One row in a run list, independent of where the data came from. */
export interface RunListItem {
  projectId: string;
  runId: string;
  executionState: string | null;
  /** Time shown in the row and the meaning of that time. */
  time: { label: string; value: string };
}

/** Rows plus how to remove one, for the active data adapter. */
export interface RunListData {
  items: RunListItem[];
  /** Removes a row from the source when the source supports it. */
  remove?: (item: RunListItem) => void;
  /** Reads the source again. */
  reload: () => void;
}

/**
 * Reads the browser-local run list for the signed-in tenant. Pass a project ID
 * to limit rows to one project.
 */
export function useBrowserRunList(projectId?: string): RunListData {
  const { authorizationContext } = useAuth();
  const tenantId = authorizationContext?.tenantId;
  const [generation, setGeneration] = useState(0);
  const remove = useCallback((item: RunListItem) => {
    if (!tenantId) return;
    forgetRun(tenantId, item.projectId, item.runId);
    setGeneration((value) => value + 1);
  }, [tenantId]);
  const reload = useCallback(() => setGeneration((value) => value + 1), []);
  const items = useMemo(() => {
    if (!tenantId || generation < 0) return [];
    return readRecentRuns(tenantId, projectId).map((run) => ({
      projectId: run.projectId,
      runId: run.runId,
      executionState: run.executionState,
      time: { label: 'Last opened', value: run.lastOpenedAt },
    }));
  }, [tenantId, projectId, generation]);
  return { items, remove, reload };
}
