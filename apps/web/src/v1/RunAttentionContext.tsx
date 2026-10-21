/**
 * Shares one notifications state between the notification bell and the
 * Overview "Needs attention" list. The provider polls once; every reader sees
 * the same items, so a dismiss in one place shows in the other at once.
 */
import { createContext, useContext, type ReactNode } from 'react';
import { useRunAttention, type RunAttention } from './runAttention';

const RunAttentionContext = createContext<RunAttention | null>(null);

/** Reads the notifications once for everything inside it. */
export function RunAttentionProvider({ children }: { children: ReactNode }) {
  const attention = useRunAttention();
  return <RunAttentionContext.Provider value={attention}>{children}</RunAttentionContext.Provider>;
}

/** Returns the shared notifications state. Must be used inside RunAttentionProvider. */
// eslint-disable-next-line react-refresh/only-export-components
export function useSharedRunAttention(): RunAttention {
  const attention = useContext(RunAttentionContext);
  // Calling useRunAttention here instead would start a second poll, so fail loudly.
  if (!attention) throw new Error('useSharedRunAttention must be used inside RunAttentionProvider.');
  return attention;
}
