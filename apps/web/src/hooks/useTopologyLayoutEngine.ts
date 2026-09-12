import { useEffect, useState } from 'react';
import {
  DEFAULT_TOPOLOGY_LAYOUT_ENGINE,
  type TopologyLayoutEngine,
} from '../utils/dagLayout';

const STORAGE_KEY = 'agentweaver.topology-layout-engine';

function readLayoutEngine(): TopologyLayoutEngine {
  if (typeof window === 'undefined') return DEFAULT_TOPOLOGY_LAYOUT_ENGINE;
  return window.localStorage.getItem(STORAGE_KEY) === 'legacy-staircase'
    ? 'legacy-staircase'
    : DEFAULT_TOPOLOGY_LAYOUT_ENGINE;
}

export function useTopologyLayoutEngine(): [TopologyLayoutEngine, (engine: TopologyLayoutEngine) => void] {
  const [engine, setEngine] = useState<TopologyLayoutEngine>(readLayoutEngine);
  useEffect(() => {
    window.localStorage.setItem(STORAGE_KEY, engine);
  }, [engine]);
  return [engine, setEngine];
}
