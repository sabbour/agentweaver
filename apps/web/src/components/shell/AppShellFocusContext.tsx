import { createContext, useContext } from 'react';

export interface AppShellFocusContextValue {
  focused: boolean;
  setFocused: (focused: boolean) => void;
}

export const AppShellFocusContext = createContext<AppShellFocusContextValue>({
  focused: false,
  setFocused: () => {},
});

export function useAppShellFocus() {
  return useContext(AppShellFocusContext);
}
