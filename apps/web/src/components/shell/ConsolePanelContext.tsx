import { createContext, useContext } from 'react';

export interface ConsolePanelContextValue {
  open: boolean;
  openConsole: () => void;
  closeConsole: () => void;
}

const ConsolePanelContext = createContext<ConsolePanelContextValue | null>(null);

export const ConsolePanelProvider = ConsolePanelContext.Provider;

export function useConsolePanel(): ConsolePanelContextValue {
  const value = useContext(ConsolePanelContext);
  if (!value) {
    throw new Error('useConsolePanel must be used inside ConsolePanelProvider');
  }
  return value;
}
