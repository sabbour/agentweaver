import { createContext, useContext } from 'react';

export interface AppShellFocusContextValue {
  focused: boolean;
  setFocused: (focused: boolean) => void;
}

/** Shares focus coordination state between the app shell and embedded surfaces. */
export const AppShellFocusContext = createContext<AppShellFocusContextValue>({
  focused: false,
  setFocused: () => {},
});

/**
 * Reads the current app-shell focus context so embedded surfaces can coordinate focus behavior.
 * Returns the context value from the nearest provider; it makes no request and owns no state.
 */
export function useAppShellFocus() {
  return useContext(AppShellFocusContext);
}
