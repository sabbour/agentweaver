import { type ReactNode, useCallback, useEffect, useMemo, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { makeStyles, tokens } from '@fluentui/react-components';
import { LeftNav } from './LeftNav';
import { TopBar } from './TopBar';
import { resolveActiveKey } from './navConfig';
import {
  clearLastActiveProjectId,
  getLastActiveProjectId,
  setLastActiveProjectId,
} from './projectContext';

// Spec 011 — the persistent navigation shell (FR-001). Left nav + top bar frame
// the main content area and remain visible on every page, including deep run /
// execution pages (clarification C4, FR-006). The active project is derived from
// the route so the shell renders correctly on direct deep links.

const useStyles = makeStyles({
  root: {
    display: 'flex',
    height: '100vh',
    minHeight: 0,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  body: {
    flex: 1,
    minWidth: 0,
    minHeight: 0,
    display: 'flex',
    flexDirection: 'column',
    overflow: 'hidden',
  },
  main: {
    flex: 1,
    minHeight: 0,
    overflow: 'auto',
    padding: `${tokens.spacingVerticalXXL} ${tokens.spacingHorizontalXXL}`,
  },
});

// Extract the active project id from the route (the shell sits above <Routes>,
// so route params are not available via useParams here).
export function projectIdFromPath(pathname: string): string | undefined {
  const match = /^\/projects\/([^/]+)/.exec(pathname);
  return match?.[1];
}

export interface AppShellProps {
  children: ReactNode;
}

export function AppShell({ children }: AppShellProps) {
  const styles = useStyles();
  const location = useLocation();

  // The project id actually present in the route (undefined on global pages).
  const routeProjectId = useMemo(
    () => projectIdFromPath(location.pathname),
    [location.pathname],
  );

  // Remembered project so global pages (/overview, /) keep the user "in" their
  // project context (overview-keeps-project). Updated whenever a project route
  // is active; cleared if the persisted project no longer exists.
  const [lastActiveProjectId, setLastActiveState] = useState<string | undefined>(
    () => getLastActiveProjectId(),
  );

  useEffect(() => {
    if (routeProjectId) {
      setLastActiveProjectId(routeProjectId);
      setLastActiveState(routeProjectId);
    }
  }, [routeProjectId]);

  const clearFallbackProject = useCallback(() => {
    clearLastActiveProjectId();
    setLastActiveState(undefined);
  }, []);

  // Effective project for the switcher display + project-scoped nav targets:
  // the route's project when present, otherwise the persisted fallback.
  const effectiveProjectId = routeProjectId ?? lastActiveProjectId;
  const isFallbackProject = !routeProjectId && Boolean(lastActiveProjectId);

  // Active-item highlight stays driven by the REAL route so global pages
  // highlight Overview, not the fallback project's Dashboard.
  const activeKey = useMemo(
    () => resolveActiveKey(location.pathname, routeProjectId),
    [location.pathname, routeProjectId],
  );

  return (
    <div className={styles.root}>
      <LeftNav projectId={effectiveProjectId} activeKey={activeKey} />
      <div className={styles.body}>
        <TopBar
          projectId={effectiveProjectId}
          pathname={location.pathname}
          isFallbackProject={isFallbackProject}
          onFallbackProjectMissing={clearFallbackProject}
        />
        <main className={styles.main}>{children}</main>
      </div>
    </div>
  );
}
