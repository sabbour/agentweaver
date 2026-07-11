import { useConsolePanel } from './ConsolePanelContext';
import { useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
export function ConsoleRouteRedirect() {
  const navigate = useNavigate();
  const { openConsole } = useConsolePanel();

  useEffect(() => {
    openConsole();
    navigate('/overview', { replace: true });
  }, [navigate, openConsole]);

  return null;
}
