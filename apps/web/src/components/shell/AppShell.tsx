import './shell.css';
import { ProjectListProvider } from '../../hooks/useProjectList';
import { StartOrchestrationFab } from '../StartOrchestrationFab';
import { GitHubCopilotConnectionRequiredAction } from '../GitHubCopilotConnectionRequiredAction';
import {
  GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT,
  isGitHubCopilotConnectionRequirement,
} from '../../api/githubConnectionRequirement';
import { LeftNav } from './LeftNav';
import { NotificationsProvider } from '../../notifications/NotificationsProvider';
import { resolveActiveKey } from './navConfig';
import { projectIdFromPath } from './projectIdFromPath';
import { clearLastActiveProjectId, getLastActiveProjectId, setLastActiveProjectId } from './projectContext';
import { useCallback, useEffect, useMemo, useState } from 'react';
import { useLocation } from 'react-router-dom';
import type { ReactNode } from 'react';
import type { GitHubCopilotConnectionRequirement } from '../../api/githubConnectionRequirement';
// Spec 011 — the persistent navigation shell (FR-001). Native FluentUI rebuild:
// a full-height flex row [left rail | main canvas]. The canvas hosts a lighter
// rounded floating panel (Copilot "content floats on the sidebar" look). No
// copilot-fluent-system kit imports — theme tokens carry all visual styling.

export interface AppShellProps {
  children: ReactNode;
  banner?: ReactNode;
}

export function AppShell({ children, banner }: AppShellProps) {
  const location = useLocation();
  const [connectionRequirement, setConnectionRequirement] =
    useState<GitHubCopilotConnectionRequirement | null>(null);

  useEffect(() => {
    const showConnectionRequirement = (event: Event) => {
      if (event instanceof CustomEvent && isGitHubCopilotConnectionRequirement(event.detail))
        setConnectionRequirement(event.detail);
    };
    window.addEventListener(GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT, showConnectionRequirement);
    return () => window.removeEventListener(GITHUB_COPILOT_CONNECTION_REQUIRED_EVENT, showConnectionRequirement);
  }, []);

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
    let cancelled = false;
    if (routeProjectId) {
      setLastActiveProjectId(routeProjectId);
      queueMicrotask(() => {
        if (!cancelled) setLastActiveState(routeProjectId);
      });
    }
    return () => { cancelled = true; };
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
    <ProjectListProvider>
      <NotificationsProvider>
        <div className="aw-app-shell">
          <LeftNav
            projectId={effectiveProjectId}
            activeKey={activeKey}
            pathname={location.pathname}
            isFallbackProject={isFallbackProject}
            onFallbackProjectMissing={clearFallbackProject}
          />
          <div className="aw-shell-canvas">
            {/* key remounts the content area when the active project changes,
                clearing stale page state the same way the old bodyKey did. */}
            <main
              key={routeProjectId ?? '__global__'}
              className="aw-shell-content"
              aria-label="Main content"
            >
              <div className="aw-floating-actions">
                <StartOrchestrationFab currentProjectId={effectiveProjectId} />
              </div>
              <div className="aw-shell-scroll">
                {connectionRequirement && (
                  <GitHubCopilotConnectionRequiredAction
                    requirement={connectionRequirement}
                    onDismiss={() => setConnectionRequirement(null)}
                  />
                )}
                {banner}
                {children}
              </div>
            </main>
          </div>
        </div>
      </NotificationsProvider>
    </ProjectListProvider>
  );
}
