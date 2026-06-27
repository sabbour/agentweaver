import { useEffect, useState } from 'react';
import { apiClient } from '../api/apiClient';
import type { RuntimeInfo } from '../api/types';

interface RuntimeInfoState {
  podName: string | null;
  kubernetes: boolean;
}

const defaultState: RuntimeInfoState = { podName: null, kubernetes: false };

// Module-level cache so the fetch happens exactly once per app lifetime.
let cachedState: RuntimeInfoState | null = null;
let fetchPromise: Promise<void> | null = null;
const listeners: Array<(s: RuntimeInfoState) => void> = [];

function ensureFetched() {
  if (cachedState !== null || fetchPromise !== null) return;
  fetchPromise = Promise.resolve()
    .then(() => apiClient.getSystemRuntime())
    .then((info: RuntimeInfo) => {
      cachedState = {
        kubernetes: info.kubernetes,
        podName: info.kubernetes && info.podName ? info.podName : null,
      };
      listeners.forEach((cb) => cb(cachedState!));
    })
    .catch(() => {
      cachedState = defaultState;
      listeners.forEach((cb) => cb(cachedState!));
    });
}

/** Returns the kubernetes runtime info. Fetches once app-wide; degrades to defaults on error. */
export function useRuntimeInfo(): RuntimeInfoState {
  const [state, setState] = useState<RuntimeInfoState>(
    () => cachedState ?? defaultState,
  );

  useEffect(() => {
    if (cachedState !== null) return;
    ensureFetched();
    const cb = (s: RuntimeInfoState) => setState(s);
    listeners.push(cb);
    return () => {
      const idx = listeners.indexOf(cb);
      if (idx !== -1) listeners.splice(idx, 1);
    };
  }, []);

  return state;
}

/** Reset the module-level cache (for tests only). */
export function _resetRuntimeInfoCache() {
  cachedState = null;
  fetchPromise = null;
  listeners.length = 0;
}
